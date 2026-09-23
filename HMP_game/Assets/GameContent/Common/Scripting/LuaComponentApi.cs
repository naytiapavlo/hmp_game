using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HMProtection.Entities;
using HMProtection.EntityAdapters;
using HMProtection.Modules.Fire;
using HMProtection.Modules.Interaction;
using HMProtection.Modules.Visibility;
using HMProtection.Sessions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HMProtection.Scripting
{
    /// <summary>Lua binding surface: strings in, strings out. Never returns Unity objects or reflection handles.</summary>
    public sealed partial class LuaComponentApi : IDisposable
    {
        sealed class ApiError : Exception { public readonly string Code; public ApiError(string code, string error) : base(error) { Code = code; } }
        sealed class Inbox
        {
            public IDisposable Subscription;
            public readonly Queue<NamedEvent> Events = new Queue<NamedEvent>();
            public int Dropped;
            public int Bytes;
        }
        readonly ComponentScriptService owner;
        readonly string scriptId;
        readonly string clientId = Guid.NewGuid().ToString("N");
        readonly Dictionary<string, Inbox> inboxes = new Dictionary<string, Inbox>();
        readonly Dictionary<string, TimerHandle> timers = new Dictionary<string, TimerHandle>();
        readonly Dictionary<string, IDisposable> leases = new Dictionary<string, IDisposable>();
        readonly HashSet<string> requests = new HashSet<string>(StringComparer.Ordinal);
        EntityGuidanceService guidance;
        Guid route;
        bool disposed;
        int queuedBytes;
        LevelSession Session => owner.Session;
        EntityScope Scope => Session.Entities;
        internal LuaComponentApi(ComponentScriptService service, string name) { owner = service; scriptId = name; }

        public string Call(string method, string argsJson)
        {
            try
            {
                Require(Thread.CurrentThread.ManagedThreadId == owner.MainThread, "wrong_thread", "Lua component calls must run on the Unity main thread.");
                if (method == "client.dispose") { Dispose(); return Success(new JObject()); }
                Require(!disposed && Session.State == LevelSessionState.Running && Scope.IsReady, "session_stopped", "This script client no longer owns a running session. Create a new client for a new run.");
                Require(!string.IsNullOrWhiteSpace(method) && method.Length <= 128, "invalid_argument", "Method is required.");
                JObject args = ReadObject(argsJson ?? "{}");
                return Success(Execute(method, args));
            }
            catch (ApiError error) { return Failure(error.Code, error.Message); }
            catch (JsonException error) { return Failure("invalid_json", error.Message); }
            catch (Exception error) { Debug.LogException(error); return Failure("internal_error", "Component call failed. See the Unity log."); }
        }

        JObject Execute(string method, JObject args)
        {
            switch (method)
            {
                case "level.config": return ReadLevelScriptConfig();
                case "flow.prepare": return PrepareFlow();
                case "flow.stage": return RunFlowStage(args);
                case "flow.status": return FlowStatus();
                case "flow.finish": return FinishFlow();
                case "session.info": return new JObject { ["scopeId"] = Scope.ScopeId, ["levelId"] = Scope.LevelId,
                    ["scriptId"] = scriptId, ["clientId"] = clientId, ["simulationTime"] = Session.Clock.SimulationTime,
                    ["presentationTime"] = Session.Clock.PresentationTime };
                case "entity.resolve":
                    Check(Scope.Registry.TryResolve(Text(args, "id"), out var found, out var resolveError), resolveError);
                    return Entity(found);
                case "role.resolve":
                    Require(Scope.Bindings != null, "missing_binding", "This scene has no role binding set.");
                    Check(Scope.Bindings.TryResolveRole(Text(args, "role"), Scope, out var bound, out var bindError), bindError);
                    return Entity(bound);
                case "entity.find":
                    var matches = new JArray();
                    foreach (var handle in Scope.Registry.FindByTag(Text(args, "tag"))) matches.Add(Entity(handle));
                    return new JObject { ["entities"] = matches };
                case "entity.active.get":
                    Check(new EntityFacade(Scope).TryGetActive(Handle(args), out var self, out var hierarchy, out var activeError), activeError);
                    return new JObject { ["activeSelf"] = self, ["activeInHierarchy"] = hierarchy };
                case "entity.active.set":
                    Check(new EntityFacade(Scope).TrySetActive(Handle(args), Boolean(args, "active"), out activeError), activeError);
                    return new JObject();
                case "entity.anchor":
                    Check(new EntityFacade(Scope).TryGetAnchorPose(Handle(args), OptionalText(args, "slot", "origin"), out var pose, out var anchorError), anchorError);
                    return new JObject { ["position"] = Vector(pose.Position), ["rotation"] = new JObject { ["x"] = pose.Rotation.x, ["y"] = pose.Rotation.y, ["z"] = pose.Rotation.z, ["w"] = pose.Rotation.w } };
                case "fire.get":
                    var fire = Capability<IFireCapability>(args);
                    return new JObject { ["state"] = fire.CurrentState.ToString(), ["hasEffect"] = fire.HasEffect };
                case "fire.set":
                    string stateName = Text(args, "state");
                    Require(Enum.TryParse<FireState>(stateName, false, out var state) && Enum.IsDefined(typeof(FireState), state) && state.ToString() == stateName, "invalid_argument", "state must be None, SmokeOnly, Small, Medium or Large.");
                    Check(Capability<IFireCapability>(args).TrySetState(state, out var fireError), fireError); return new JObject();
                case "fire.freeze":
                    Check(Capability<IFireCapability>(args).TrySetFrozen(Boolean(args, "frozen"), out fireError), fireError); return new JObject();
                case "fire.visual":
                    float intensity = Number(args, "intensity", 0, 1), scale = Number(args, "scale", 0, 100), smoke = Number(args, "smoke", 0, 1);
                    Check(Capability<IFireCapability>(args).TrySetVisualParameters(intensity, scale, smoke, out fireError), fireError); return new JObject();
                case "interaction.prompt":
                    var interaction = Capability<IInteractionCapability>(args);
                    bool available = interaction.TryGetPrompt(Actor(), out var prompt, out var promptError);
                    return new JObject { ["available"] = available, ["prompt"] = prompt ?? "", ["reason"] = promptError ?? "" };
                case "interaction.invoke": return Interact(args);
                case "door.get":
                    var door = Capability<IDoorCapability>(args);
                    return new JObject { ["open"] = door.IsOpen, ["animating"] = door.IsAnimating };
                case "door.set":
                    Check(Capability<IDoorCapability>(args).TrySetOpen(Actor(), Boolean(args, "open"), out var commandError), commandError); return new JObject { ["accepted"] = true };
                case "pickup.get": return new JObject { ["held"] = Capability<IPickupCapability>(args).IsHeld };
                case "pickup.drop":
                    Check(Capability<IPickupCapability>(args).TryDrop(Actor(), Boolean(args, "toss", false), out commandError), commandError); return new JObject { ["accepted"] = true };
                case "seat.get":
                    var seat = Capability<ISeatCapability>(args);
                    return new JObject { ["occupied"] = seat.IsOccupied, ["transitioning"] = seat.IsTransitioning };
                case "seat.leave":
                    Check(Capability<ISeatCapability>(args).TryLeave(Actor(), out commandError), commandError); return new JObject { ["accepted"] = true };
                case "visibility.get": return new JObject { ["visible"] = Capability<IVisibilityCapability>(args).IsVisible };
                case "visibility.set":
                    Check(Capability<IVisibilityCapability>(args).TrySetVisible(Boolean(args, "visible"), out commandError), commandError); return new JObject();
                case "timer.start": return StartTimer(args);
                case "timer.cancel":
                    string timerId = Text(args, "timer");
                    Require(timers.TryGetValue(timerId, out var timer), "unknown_timer", "Timer does not belong to this client or has already elapsed.");
                    Session.Timers.Cancel(timer); timers.Remove(timerId); return new JObject();
                case "control.acquire": return AcquireControls(args);
                case "control.release":
                    string leaseId = Text(args, "lease");
                    Require(leases.TryGetValue(leaseId, out var lease), "unknown_lease", "Lease does not belong to this client.");
                    lease.Dispose(); leases.Remove(leaseId); return new JObject();
                case "events.subscribe": return Subscribe(args);
                case "events.poll": return Poll(args);
                case "events.unsubscribe":
                    string subscription = Text(args, "subscription");
                    Require(inboxes.TryGetValue(subscription, out var inbox), "unknown_subscription", "Subscription does not belong to this client.");
                    inbox.Subscription.Dispose(); queuedBytes -= inbox.Bytes; inbox.Events.Clear(); inboxes.Remove(subscription); return new JObject();
                case "events.emit":
                    string topic = Text(args, "topic");
                    Require(topic.StartsWith("script.", StringComparison.Ordinal) && topic.Length > 7, "reserved_topic", "Lua may publish only script.* events; component facts are engine-owned.");
                    Check(Session.ScriptEvents.TryPublish(topic, Payload(args).ToString(Formatting.None), out var eventError), eventError); return new JObject();
                case "guidance.start": return StartRoute(args);
                case "guidance.cancel":
                    Require(guidance != null && route != Guid.Empty && guidance.ActiveOperation == route, "unknown_route", "This client has no active route.");
                    guidance.Cancel(); return new JObject();
                default: throw new ApiError("unknown_method", "Unknown component API method: " + method);
            }
        }

        JObject Interact(JObject args)
        {
            var handle = Handle(args);
            string request = Text(args, "requestId");
            Require(!requests.Contains(request), "duplicate_request", "An interaction request ID can be submitted only once per client.");
            Require(requests.Count < 1024, "quota_exceeded", "Interaction request history is full; create a fresh client at a script lifecycle boundary.");
            Require(!Session.Controls.IsBlocked(ControlMask.Interaction), "control_blocked", "Interaction is blocked.");
            Require(owner.Bindings != null, "missing_service", "This session has no scene interaction bridge.");
            requests.Add(request);
            bool accepted = owner.Bindings is EntityInteractionBridge bridge
                ? bridge.TryInteractRequest(handle, clientId + ":" + request, out var error)
                : owner.Bindings.TryInteract(handle, out error);
            Publish(accepted ? "interaction.accepted" : "interaction.rejected", new JObject {
                ["entity"] = ScriptEntityToken.Encode(handle), ["id"] = handle.Id.Value, ["requestId"] = request, ["reason"] = error ?? "", ["clientId"] = clientId });
            Check(accepted, error); return new JObject { ["accepted"] = true, ["requestId"] = request };
        }
        JObject StartTimer(JObject args)
        {
            Require(timers.Count < 128, "quota_exceeded", "A client may own at most 128 active timers.");
            float seconds = Number(args, "seconds", 0, 86400);
            string domainName = OptionalText(args, "domain", "simulation");
            Require(domainName == "simulation" || domainName == "presentation", "invalid_argument", "domain must be simulation or presentation.");
            var payload = Payload(args).DeepClone();
            string id = Guid.NewGuid().ToString("N");
            Check(Session.Timers.Start(seconds, domainName == "simulation" ? ClockDomain.Simulation : ClockDomain.Presentation, _ =>
            {
                timers.Remove(id);
                if (!disposed) Publish("timer.elapsed", new JObject { ["timer"] = id, ["clientId"] = clientId, ["payload"] = payload });
            }, out var handle, out var error), error);
            timers.Add(id, handle); return new JObject { ["timer"] = id };
        }
        JObject AcquireControls(JObject args)
        {
            Require(leases.Count < 128, "quota_exceeded", "A client may own at most 128 control leases.");
            var masks = args["masks"] as JArray;
            Require(masks != null && masks.Count > 0 && masks.Count <= 5, "invalid_argument", "masks must be an array of one to five control names.");
            ControlMask combined = ControlMask.None;
            foreach (var value in masks)
            {
                string name = value.Type == JTokenType.String ? (string)value : null;
                Require(name != null && Enum.TryParse<ControlMask>(name, false, out var parsed) && parsed != ControlMask.None
                    && Enum.IsDefined(typeof(ControlMask), parsed) && parsed.ToString() == name, "invalid_argument", "Unknown control mask.");
                Enum.TryParse(name, out ControlMask mask); combined |= mask;
            }
            string id = Guid.NewGuid().ToString("N"); leases.Add(id, Session.Controls.Acquire(this, combined));
            return new JObject { ["lease"] = id };
        }
        JObject Subscribe(JObject args)
        {
            Require(inboxes.Count < 64, "quota_exceeded", "A client may own at most 64 subscriptions.");
            string topic = Text(args, "topic");
            Require(topic.Length <= 128, "invalid_argument", "topic must contain at most 128 characters.");
            var inbox = new Inbox();
            inbox.Subscription = Session.ScriptEvents.Subscribe(topic, message =>
            {
                if (disposed) return;
                if (message.Topic == "timer.elapsed" && (string)JObject.Parse(message.PayloadJson)["clientId"] != clientId) return;
                int bytes = System.Text.Encoding.UTF8.GetByteCount(message.PayloadJson);
                if (inbox.Events.Count >= 256 || queuedBytes + bytes > 1048576) { inbox.Dropped++; return; }
                inbox.Events.Enqueue(message); inbox.Bytes += bytes; queuedBytes += bytes;
            });
            string id = Guid.NewGuid().ToString("N"); inboxes.Add(id, inbox);
            return new JObject { ["subscription"] = id };
        }
        JObject Poll(JObject args)
        {
            string id = Text(args, "subscription");
            Require(inboxes.TryGetValue(id, out var inbox), "unknown_subscription", "Subscription does not belong to this client.");
            int maximum = args["max"] == null ? 64 : Integer(args, "max", 1, 64);
            var events = new JArray();
            while (events.Count < maximum && inbox.Events.Count > 0)
            {
                var item = inbox.Events.Dequeue();
                int bytes = System.Text.Encoding.UTF8.GetByteCount(item.PayloadJson);
                inbox.Bytes -= bytes; queuedBytes -= bytes;
                events.Add(new JObject { ["topic"] = item.Topic, ["sequence"] = item.Sequence, ["payload"] = JToken.Parse(item.PayloadJson) });
            }
            int dropped = inbox.Dropped; inbox.Dropped = 0;
            return new JObject { ["events"] = events, ["dropped"] = dropped };
        }
        JObject StartRoute(JObject args)
        {
            var target = Handle(args);
            if (guidance == null)
            {
                guidance = owner.Bindings != null ? owner.Bindings.GetComponent<EntityGuidanceService>() : null;
                Require(guidance != null, "missing_service", "This scene has no entity guidance service.");
                guidance.Finished += RouteFinished;
            }
            // One presentation backend, one route. Never silently replace another script's route.
            Require(guidance.ActiveOperation == Guid.Empty || guidance.ActiveOperation == route, "route_busy", "Another owner already has an active route.");
            bool accepted = guidance.TryNavigate(target, OptionalText(args, "slot", "origin"), OptionalText(args, "label", ""), out var operation, out var error);
            route = accepted ? operation : Guid.Empty;
            Check(accepted, error); return new JObject { ["operation"] = operation.ToString("N") };
        }
        void RouteFinished(Guid operation, RouteOutcome outcome)
        {
            if (disposed || operation != route) return;
            route = Guid.Empty;
            Publish("guidance.finished", new JObject { ["operation"] = operation.ToString("N"), ["outcome"] = outcome.ToString(), ["clientId"] = clientId });
        }
        void Publish(string topic, JObject data) => Session.ScriptEvents.TryPublish(topic, data.ToString(Formatting.None), out _);
        EntityHandle Actor()
        {
            Require(!Session.Controls.IsBlocked(ControlMask.Interaction), "control_blocked", "Interaction is blocked.");
            Require(Scope.Bindings != null, "missing_binding", "Player role is required.");
            Check(Scope.Bindings.TryResolveRole("player", Scope, out var handle, out var error), error); return handle;
        }
        EntityHandle Handle(JObject args)
        {
            Require(ScriptEntityToken.TryDecode(Text(args, "entity"), out var handle) && Scope.Registry.IsValid(handle), "stale_entity", "Entity token is invalid, destroyed or belongs to another session run.");
            return handle;
        }
        T Capability<T>(JObject args) where T : class
        {
            Check(Scope.Registry.TryGetCapability<T>(Handle(args), out var capability, out var error), error); return capability;
        }
        static JObject Entity(EntityHandle handle) => new JObject { ["entity"] = ScriptEntityToken.Encode(handle), ["id"] = handle.Id.Value, ["scopeId"] = handle.ScopeId };
        static JObject Vector(Vector3 value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };
        static JObject Payload(JObject args)
        {
            if (args["payload"] == null) return new JObject();
            Require(args["payload"] is JObject, "invalid_argument", "payload must be a JSON object.");
            return (JObject)args["payload"];
        }
        static JObject ReadObject(string json)
        {
            Require(System.Text.Encoding.UTF8.GetByteCount(json) <= 65536, "invalid_argument", "Request exceeds 64 KiB.");
            using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
            {
                var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                Require(token is JObject && !reader.Read(), "invalid_json", "Arguments must be one JSON object.");
                return (JObject)token;
            }
        }
        static string Text(JObject args, string key)
        {
            Require(args[key]?.Type == JTokenType.String, "invalid_argument", key + " must be a string.");
            string value = (string)args[key];
            Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 512, "invalid_argument", key + " must contain 1-512 characters."); return value;
        }
        static string OptionalText(JObject args, string key, string fallback)
        {
            if (args[key] == null) return fallback;
            if (key != "label") return Text(args, key);
            Require(args[key].Type == JTokenType.String && ((string)args[key]).Length <= 512, "invalid_argument", "label must be a string of at most 512 characters.");
            return (string)args[key];
        }
        static bool Boolean(JObject args, string key, bool? fallback = null)
        {
            if (args[key] == null && fallback.HasValue) return fallback.Value;
            Require(args[key]?.Type == JTokenType.Boolean, "invalid_argument", key + " must be a boolean."); return (bool)args[key];
        }
        static float Number(JObject args, string key, float min, float max)
        {
            Require(args[key]?.Type == JTokenType.Float || args[key]?.Type == JTokenType.Integer, "invalid_argument", key + " must be a number.");
            double value = (double)args[key];
            Require(!double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max, "invalid_argument", key + " is outside the supported range."); return (float)value;
        }
        static int Integer(JObject args, string key, int min, int max)
        {
            Require(args[key]?.Type == JTokenType.Integer, "invalid_argument", key + " must be an integer."); return (int)Number(args, key, min, max);
        }
        static void Require(bool condition, string code, string error) { if (!condition) throw new ApiError(code, error); }
        static void Check(bool success, string error) { if (!success) throw new ApiError("operation_rejected", error ?? "Operation was rejected."); }
        static void Check(bool success, EntityError error) { if (!success) throw new ApiError(error.Code.ToString(), error.ToString()); }
        static string Success(JObject data) => new JObject { ["apiVersion"] = 1, ["ok"] = true, ["code"] = "ok", ["error"] = "", ["data"] = data }.ToString(Formatting.None);
        static string Failure(string code, string error) => new JObject { ["apiVersion"] = 1, ["ok"] = false, ["code"] = code, ["error"] = error, ["data"] = new JObject() }.ToString(Formatting.None);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ReleaseFlow();
            foreach (var inbox in inboxes.Values) { inbox.Subscription.Dispose(); inbox.Events.Clear(); }
            foreach (var timer in timers.Values) Session.Timers.Cancel(timer);
            foreach (var lease in leases.Values) lease.Dispose();
            if (guidance != null) { guidance.Finished -= RouteFinished; if (route != Guid.Empty && guidance.ActiveOperation == route) guidance.Cancel(); }
            route = Guid.Empty; queuedBytes = 0; inboxes.Clear(); timers.Clear(); leases.Clear(); requests.Clear(); owner.Forget(this);
        }
    }
}
