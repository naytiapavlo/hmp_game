using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HMProtection.Core;
using HMProtection.EntityAdapters;
using HMProtection.LevelPackages;
using HMProtection.Navigation;
using HMProtection.Scripting;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Opt-in real-scene verification for the MoonSharp level runner and Component API boundary.</summary>
[InitializeOnLoad]
public static class LuaRuntimeIntegrationChecks
{
    const string SceneA = "Assets/Levels/third_scene_shared_modules_a/Scenes/third_scene_shared_modules_a.unity";
    const string DefinitionB = "Assets/Levels/third_scene_shared_modules_b/Runtime/third_scene_shared_modules_b.asset";
    const string Request = "Library/LuaRuntimeIntegrationChecks.request";
    const string Report = "Library/LuaRuntimeIntegrationChecks.txt";
    const string State = "LuaRuntimeIntegrationChecks.Running";
    static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    static readonly List<string> results = new List<string>();
    static double deadline;

    static LuaRuntimeIntegrationChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(State, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                deadline = EditorApplication.timeSinceStartup + 120d;
                work.Push(Check());
            }
            if (state == PlayModeStateChange.ExitingPlayMode) work.Clear();
            if (state == PlayModeStateChange.EnteredEditMode) SessionState.SetBool(State, false);
        };
    }

    [MenuItem("Tools/Scripting/Check Lua Runtime")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        results.Clear();
        try
        {
            LevelPackageTools.RefreshGeneratedDefinitions();
            LevelPackageTools.ValidateAll();
            Need(AssetDatabase.LoadAssetAtPath<SceneAsset>(SceneA) != null, "Lua package A scene is missing.");
            if (SceneManager.GetActiveScene().isDirty) throw new InvalidOperationException("Save the active scene before checks.");
            EditorSceneManager.OpenScene(SceneA, OpenSceneMode.Single);
            SessionState.SetBool(State, true);
            EditorApplication.isPlaying = true;
        }
        catch (Exception exception) { results.Add(exception.ToString()); Finish("FAIL"); }
    }

    public static void BeginBatch() => Begin();

    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode && File.Exists(Request)) { File.Delete(Request); Begin(); return; }
        if (!EditorApplication.isPlaying || work.Count == 0) return;
        try
        {
            Need(EditorApplication.timeSinceStartup < deadline, "Lua runtime integration check timed out.");
            var current = work.Peek();
            if (!current.MoveNext()) { work.Pop(); if (work.Count == 0) Finish("PASS"); }
            else if (current.Current is IEnumerator nested) work.Push(nested);
        }
        catch (Exception exception) { results.Add(exception.ToString()); Finish("FAIL"); }
    }

    static IEnumerator Check()
    {
        yield return Delay(.25f);
        var boot = FindBoot();
        var host = FindHost();
        Need(boot.Ready && boot.definition != null && boot.definition.scriptEnabled, "Package A did not enable Lua.");
        Need(host.Lua != null && host.Lua.IsRunning, "Package A did not start its real Lua runner: " + host.Lua?.LastError);
        int packageUpdates = host.Lua.UpdateCount;
        yield return Delay(.12f);
        Need(host.Lua.IsRunning && host.Lua.UpdateCount > packageUpdates, "Package A Lua runner did not tick.");
        results.Add("PASS packaged A starts a real Lua VM and advances update count");

        var observerOne = host.Scripts.CreateClient("lua-runtime-observer-one");
        var observerTwo = host.Scripts.CreateClient("lua-runtime-observer-two");
        var publisher = host.Scripts.CreateClient("lua-runtime-publisher");
        var temporaryDefinitions = new List<LevelDefinition>();
        LevelLuaRunner temporary = null;
        try
        {
            string pong = Subscription(observerOne, "script.runtime.pong");
            string timer = Subscription(observerTwo, "script.runtime.timer");
            var definition = TemporaryDefinition(boot.definition);
            temporaryDefinitions.Add(definition);
            Need(LevelLuaRunner.TryCreate(host, definition, out temporary, out var createError), "Temporary real-scene Lua runner failed: " + createError);
            Need(host.IsBlocked(HMProtection.Sessions.ControlMask.ToolUse), "Lua control lease was not acquired.");
            Ok(publisher, "events.emit", "{\"topic\":\"script.ping\",\"payload\":{\"from\":\"C#\"}}");
            JObject pongEvent = null;
            while (pongEvent == null)
            {
                Need(temporary.Tick(Time.deltaTime, Time.unscaledDeltaTime, out var temporaryError), "Temporary Lua runner faulted: " + temporaryError);
                pongEvent = Poll(observerOne, pong).FirstOrDefault();
                yield return null;
            }
            var pongPayload = (JObject)pongEvent["payload"];
            Need(pongPayload["text"]?.Type == JTokenType.String && (string)pongPayload["text"] == "中文", "Lua JSON decode did not preserve Chinese text.");
            Need(pongPayload["empty"]?.Type == JTokenType.Array && !pongPayload["empty"].HasValues, "Lua json.array() did not encode an empty JSON array.");
            Need(pongPayload["max"]?.Type == JTokenType.Integer && (long)pongPayload["max"] == 64L, "Lua json.encode({max=64}) did not preserve an integer token.");
            JObject timerEvent = null;
            while (timerEvent == null)
            {
                Need(temporary.Tick(Time.deltaTime, Time.unscaledDeltaTime, out var temporaryError), "Temporary Lua runner faulted: " + temporaryError);
                timerEvent = Poll(observerTwo, timer).FirstOrDefault();
                yield return null;
            }
            results.Add("PASS real component_api:update receives C# event, JSON round-trips Chinese/empty-array/integer, and session timer crosses Lua boundary");

            temporary.Dispose();
            temporary = null;
            Need(!host.IsBlocked(HMProtection.Sessions.ControlMask.ToolUse), "Lua Dispose did not release its control lease.");
            results.Add("PASS Lua Dispose releases script-created control resources");

            yield return VerifyFaultIsolation(host, boot.definition, temporaryDefinitions);

            var oldLua = host.Lua;
            var oldClient = host.Scripts.CreateClient("lua-runtime-old-session");
            string oldToken = Token(Ok(oldClient, "entity.resolve", "{\"id\":\"fire.third.a\"}"));
            var definitionB = AssetDatabase.LoadAssetAtPath<LevelDefinition>(DefinitionB);
            Need(AppNavigationService.Instance.TryLoadDefinition(definitionB, out var navigation, out var navigationError), "Package B navigation failed: " + navigationError);
            navigation.AllowActivation();
            yield return WaitForTerminal(navigation);
            yield return Until(() =>
            {
                var candidate = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();
                return FindBoot().Ready && candidate != null && candidate.Lua != null && candidate.Lua.IsRunning && candidate.Lua.UpdateCount > 0;
            }, "Package B bootstrapper/Lua runner was not ready and ticked.");
            var hostB = FindHost();
            Need(hostB.Lua != null && hostB.Lua.IsRunning && hostB.Lua.UpdateCount > 0, "Package B Lua runner did not start/tick.");
            Need(!oldLua.IsRunning, "Package A Lua runner survived a definition navigation.");
            Fail(oldClient, "session.info", "{}", "session_stopped");
            var fresh = hostB.Scripts.CreateClient("lua-runtime-new-session");
            try { Fail(fresh, "entity.active.get", Entity(oldToken), "stale_entity"); }
            finally { fresh.Dispose(); oldClient.Dispose(); }
            results.Add("PASS navigation disposes old Lua/API callbacks and starts package B Lua runner");
            var runnerB = hostB.Lua;
            FindBoot().ReleaseEntities();
            yield return null;
            Need(!runnerB.IsRunning, "ReleaseEntities left package B Lua runner active.");
            results.Add("PASS ReleaseEntities disposes the package B Lua runner");
        }
        finally
        {
            temporary?.Dispose();
            observerOne.Dispose(); observerTwo.Dispose(); publisher.Dispose();
            foreach (var definition in temporaryDefinitions)
                if (definition != null) UnityEngine.Object.Destroy(definition);
        }
    }

    static IEnumerator VerifyFaultIsolation(LevelSessionHost host, LevelDefinition template, List<LevelDefinition> temporaryDefinitions)
    {
        var startup = TemporaryDefinition(template);
        temporaryDefinitions.Add(startup);
        startup.luaSource = "local api=require('component_api') return { new = function(host, json) local c=api.new(host,json); local lease=c:acquire_controls({'ToolUse'}); assert(lease.ok); local timer=c:start_timer(10,'presentation',{}); assert(timer.ok); error('startup-boom') end, update = function(self,d,u) end, stop = function(self) end }";
        Need(!LevelLuaRunner.TryCreate(host, startup, out _, out var startupError) && !string.IsNullOrWhiteSpace(startupError), "Lua startup exception was accepted.");
        Need(!host.IsBlocked(HMProtection.Sessions.ControlMask.ToolUse), "Lua startup exception leaked a control lease.");
        var update = TemporaryDefinition(template);
        temporaryDefinitions.Add(update);
        update.luaSource = "local api=require('component_api') return { new=function(host,json) local c=api.new(host,json); local l=c:acquire_controls({'ToolUse'}); assert(l.ok); return {api=c} end, update=function(self,d,u) error('update-boom') end, stop=function(self) self.api:dispose() end }";
        Need(LevelLuaRunner.TryCreate(host, update, out var runner, out var updateError), "Fault-isolation Lua VM did not start: " + updateError);
        Need(host.IsBlocked(HMProtection.Sessions.ControlMask.ToolUse), "Fault-isolation Lua did not acquire a lease.");
        Need(!runner.Tick(.01f, .01f, out var tickError) && !string.IsNullOrWhiteSpace(tickError), "Lua update exception was accepted.");
        Need(!runner.IsRunning && !host.IsBlocked(HMProtection.Sessions.ControlMask.ToolUse), "Lua update fault leaked a runner or control lease.");
        var stop = TemporaryDefinition(template);
        temporaryDefinitions.Add(stop);
        stop.luaSource = "local api=require('component_api') return { new=function(host,json) local c=api.new(host,json); local l=c:acquire_controls({'ToolUse'}); assert(l.ok); return {api=c} end, update=function(self,d,u) end, stop=function(self) self.api:dispose(); error('stop-boom') end }";
        Need(LevelLuaRunner.TryCreate(host, stop, out var stopRunner, out var stopError), "Stop-fault Lua VM did not start: " + stopError);
        Need(host.IsBlocked(HMProtection.Sessions.ControlMask.ToolUse), "Stop-fault Lua did not acquire a lease.");
        stopRunner.Dispose();
        Need(!string.IsNullOrWhiteSpace(stopRunner.LastError) && !host.IsBlocked(HMProtection.Sessions.ControlMask.ToolUse), "Lua stop exception did not report and release resources.");
        results.Add("PASS Lua startup/update/stop failures dispose their Component API resources");
        yield break;
    }

    static LevelDefinition TemporaryDefinition(LevelDefinition template)
    {
        var definition = ScriptableObject.CreateInstance<LevelDefinition>();
        definition.levelId = "lua-runtime-temporary";
        definition.scriptEnabled = true;
        definition.scriptEntry = "Scripts/runtime_test.lua";
        definition.luaApiSource = template.luaApiSource;
        definition.luaSource = "local api=require('component_api') local module={} function module.new(host,json) local c=api.new(host,json); local fire=c:resolve_role('primaryFire'); assert(fire.ok); local self={api=c,fire=fire.data.entity}; local ping=c:on('script.ping',function(e) local value=json.decode('{\\\"text\\\":\\\"中文\\\",\\\"empty\\\":[]}'); local r=c:emit('script.runtime.pong',{text=value.text,empty=value.empty,max=64,ping=e.payload}); assert(r.ok) end); assert(ping.ok); local sub=c:on('timer.elapsed',function(e) local r=c:emit('script.runtime.timer',{text='计时器',empty=json.array(),payload=e.payload}); assert(r.ok) end); assert(sub.ok); local lease=c:acquire_controls({'ToolUse'}); assert(lease.ok); local timer=c:start_timer(.05,'presentation',{from='lua'}); assert(timer.ok); return self end function module.update(self,d,u) local r=self.api:update(); assert(r.ok); local visual=self.api:set_fire_visual(self.fire,1,1,1); assert(visual.ok) end function module.stop(self) self.api:dispose() end return module";
        return definition;
    }

    static string Subscription(LuaComponentApi api, string topic) => (string)Ok(api, "events.subscribe", "{\"topic\":\"" + topic + "\"}")["subscription"];
    static IEnumerable<JObject> Poll(LuaComponentApi api, string subscription)
    {
        var events = (JArray)Ok(api, "events.poll", "{\"subscription\":\"" + subscription + "\",\"max\":64}")["events"];
        return events.Cast<JObject>().ToArray();
    }
    static IEnumerator Until(Func<bool> condition, string error) { while (!condition()) yield return null; if (!condition()) throw new InvalidOperationException(error); }
    static IEnumerator Delay(float seconds) { float until = Time.unscaledTime + seconds; while (Time.unscaledTime < until) yield return null; }
    static IEnumerator WaitForTerminal(NavigationOperation operation) { while (!operation.IsTerminal) yield return null; Need(operation.State == NavigationState.Completed, "Navigation failed: " + operation.State + " " + operation.Error); }
    static LevelBootstrapper FindBoot() { var value = UnityEngine.Object.FindAnyObjectByType<LevelBootstrapper>(); Need(value != null, "No active LevelBootstrapper."); return value; }
    static LevelSessionHost FindHost() { var value = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>(); Need(value != null && value.IsRunning && value.Scripts != null, "No active session host."); return value; }
    static JObject Ok(LuaComponentApi api, string method, string args) { var value = JObject.Parse(api.Call(method, args)); Need((bool)value["ok"], method + " failed: " + value["code"] + " " + value["error"]); return (JObject)value["data"]; }
    static void Fail(LuaComponentApi api, string method, string args, string code) { var value = JObject.Parse(api.Call(method, args)); Need(!(bool)value["ok"] && (string)value["code"] == code, method + " expected " + code + ", got " + value); }
    static string Token(JObject value) => (string)value["entity"];
    static string Entity(string token) => "{\"entity\":\"" + token + "\"}";
    static void Need(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    static void Finish(string state) { work.Clear(); File.WriteAllText(Report, state + "\n" + string.Join("\n", results)); if (Application.isBatchMode) EditorApplication.Exit(state == "PASS" ? 0 : 1); else if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; }
}
