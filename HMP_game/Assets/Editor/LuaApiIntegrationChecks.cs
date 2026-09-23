using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HMProtection.EntityAdapters;
using HMProtection.Scripting;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Opt-in Play-mode integration check for the string/JSON scripting facade.</summary>
[InitializeOnLoad]
public static class LuaApiIntegrationChecks
{
    const string ScenePath = "Assets/Scenes/第三场景-通用模块试点.unity";
    const string Request = "Library/LuaApiIntegrationChecks.request";
    const string Report = "Library/LuaApiIntegrationChecks.txt";
    const string State = "LuaApiIntegrationChecks.Running";
    static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    static readonly List<string> results = new List<string>();
    static double deadline;

    static LuaApiIntegrationChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(State, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) { deadline = EditorApplication.timeSinceStartup + 90; work.Push(Check()); }
            if (state == PlayModeStateChange.ExitingPlayMode) work.Clear();
            if (state == PlayModeStateChange.EnteredEditMode) SessionState.SetBool(State, false);
        };
    }

    [MenuItem("Tools/Scripting/Check Lua Component API")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null) { Block("shared-module pilot scene is missing."); return; }
        if (SceneManager.GetActiveScene().isDirty) { Block("save the active scene before checks."); return; }
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        SessionState.SetBool(State, true); EditorApplication.isPlaying = true;
    }
    public static void BeginBatch() => Begin();

    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode && File.Exists(Request)) { File.Delete(Request); Begin(); return; }
        if (!EditorApplication.isPlaying || work.Count == 0) return;
        try
        {
            Need(EditorApplication.timeSinceStartup < deadline, "Lua API check timed out.");
            var current = work.Peek();
            if (!current.MoveNext()) { work.Pop(); if (work.Count == 0) Finish("PASS"); }
            else if (current.Current is IEnumerator nested) work.Push(nested);
        }
        catch (Exception exception) { results.Add(exception.ToString()); Finish("FAIL"); }
    }

    static IEnumerator Check()
    {
        yield return Delay(2f);
        var host = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();
        var bridge = UnityEngine.Object.FindAnyObjectByType<EntityInteractionBridge>();
        Need(host != null && host.IsRunning && host.Scripts != null && bridge != null && bridge.IsReady, "script host/bridge is not ready.");
        var first = host.Scripts.CreateClient("integration.a");
        var second = host.Scripts.CreateClient("integration.b");
        try
        {
            Need((string)Ok(first, "session.info", "{}")["scopeId"] != null, "session.info did not return scope data.");
            string fire = Token(Ok(first, "entity.resolve", "{\"id\":\"fire.third.a\"}"));
            string player = Token(Ok(first, "role.resolve", "{\"role\":\"player\"}"));
            Need(Ok(first, "entity.find", "{\"tag\":\"fire\"}")["entities"].Count() >= 2, "fire tag query returned fewer than two entities.");
            Need(Ok(first, "entity.anchor", "{\"entity\":\"" + fire + "\",\"slot\":\"origin\"}")["position"] != null, "fire anchor query failed.");
            string originalFireState = (string)Ok(first, "fire.get", Entity(fire))["state"];
            string requestedFireState = originalFireState == "Small" ? "Medium" : "Small";
            string sub = (string)Ok(first, "events.subscribe", "{\"topic\":\"fire.state_changed\"}")["subscription"];
            Ok(first, "fire.set", "{\"entity\":\"" + fire + "\",\"state\":\"" + requestedFireState + "\"}");
            yield return null; yield return null;
            Need(Ok(first, "events.poll", "{\"subscription\":\"" + sub + "\"}")["events"].Any(), "fire.set did not emit fire.state_changed.");
            Fail(first, "events.emit", "{\"topic\":\"fire.state_changed\",\"payload\":{}}", "reserved_topic");
            Fail(first, "entity.resolve", "{bad", "invalid_json");
            Fail(first, "fire.set", "{\"entity\":\"" + fire + "\",\"state\":\"Huge\"}", "invalid_argument");

            string door = FirstToken(first, "door"), pickup = FirstToken(first, "pickup"), seat = FirstToken(first, "seat");
            string doorSub = (string)Ok(first, "events.subscribe", "{\"topic\":\"door.state_changed\"}")["subscription"];
            bool doorOpen = (bool)Ok(first, "door.get", Entity(door))["open"];
            Ok(first, "door.set", "{\"entity\":\"" + door + "\",\"open\":" + (!doorOpen).ToString().ToLowerInvariant() + "}");
            yield return Delay(.15f);
            Need(Ok(first, "events.poll", "{\"subscription\":\"" + doorSub + "\"}")["events"].Any(), "door.set did not emit door.state_changed.");
            Need(Ok(first, "pickup.get", Entity(pickup))["held"] != null, "pickup API unavailable.");
            Need(Ok(first, "seat.get", Entity(seat))["occupied"] != null, "seat API unavailable.");
            string request = "integration-request";
            Ok(first, "interaction.invoke", "{\"entity\":\"" + pickup + "\",\"requestId\":\"" + request + "\"}");
            Fail(first, "interaction.invoke", "{\"entity\":\"" + pickup + "\",\"requestId\":\"" + request + "\"}", "duplicate_request");

            string timerSub = (string)Ok(first, "events.subscribe", "{\"topic\":\"timer.elapsed\"}")["subscription"];
            string otherTimerSub = (string)Ok(second, "events.subscribe", "{\"topic\":\"timer.elapsed\"}")["subscription"];
            string timer = (string)Ok(first, "timer.start", "{\"seconds\":0.05,\"domain\":\"presentation\",\"payload\":{\"x\":1}}")["timer"];
            yield return Delay(.15f);
            Need(Ok(first, "events.poll", "{\"subscription\":\"" + timerSub + "\"}")["events"].Any(), "owned presentation timer did not elapse.");
            Need(!Ok(second, "events.poll", "{\"subscription\":\"" + otherTimerSub + "\"}")["events"].Any(), "second client received first client's timer event.");
            Fail(second, "events.poll", "{\"subscription\":\"" + timerSub + "\"}", "unknown_subscription");
            string cancellable = (string)Ok(first, "timer.start", "{\"seconds\":10,\"domain\":\"presentation\"}")["timer"];
            Fail(second, "timer.cancel", "{\"timer\":\"" + cancellable + "\"}", "unknown_timer");
            Ok(first, "timer.cancel", "{\"timer\":\"" + cancellable + "\"}");
            Fail(first, "timer.cancel", "{\"timer\":\"" + cancellable + "\"}", "unknown_timer");
            string lease = (string)Ok(first, "control.acquire", "{\"masks\":[\"Simulation\"]}")["lease"];
            Need(host.IsBlocked(HMProtection.Sessions.ControlMask.Simulation), "first client lease was not active.");
            Fail(second, "control.release", "{\"lease\":\"" + lease + "\"}", "unknown_lease");
            Ok(second, "control.acquire", "{\"masks\":[\"ToolUse\"]}");
            string customSub = (string)Ok(first, "events.subscribe", "{\"topic\":\"script.integration\"}")["subscription"];
            Ok(first, "events.emit", "{\"topic\":\"script.integration\",\"payload\":{\"ok\":true}}");
            yield return null;
            Need(Ok(first, "events.poll", "{\"subscription\":\"" + customSub + "\"}")["events"].Any(), "custom script event was not delivered.");
            Ok(first, "events.unsubscribe", "{\"subscription\":\"" + customSub + "\"}");
            Fail(first, "events.poll", "{\"subscription\":\"" + customSub + "\"}", "unknown_subscription");
            Ok(first, "client.dispose", "{}");
            Need(!host.IsBlocked(HMProtection.Sessions.ControlMask.Simulation) && host.IsBlocked(HMProtection.Sessions.ControlMask.ToolUse), "client.dispose did not isolate leases.");
            Fail(first, "session.info", "{}", "session_stopped");
            results.Add("PASS JSON facade queries, native fire event relay, validation, API availability, request de-duplication and per-client timer/lease ownership");

            string old = player;
            bridge.DisposeScope(); yield return null;
            var boot = UnityEngine.Object.FindAnyObjectByType<HMProtection.Core.LevelBootstrapper>();
            Need(boot != null && boot.PrepareLevel(), "scope rebuild failed."); yield return null;
            var freshHost = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();
            Need(freshHost != null && freshHost.Scripts != null, "rebuilt session has no script service.");
            var fresh = freshHost.Scripts.CreateClient("integration.fresh");
            try { Fail(fresh, "entity.active.get", Entity(old), "stale_entity"); }
            finally { fresh.Dispose(); }
            results.Add("PASS scope stop invalidates existing clients before same-ID rebuild; delayed callbacks cannot reach stopped clients");
        }
        finally { first.Dispose(); second.Dispose(); }
    }

    static JObject Ok(LuaComponentApi api, string method, string args) { var value = JObject.Parse(api.Call(method, args)); Need((bool)value["ok"], method + " failed: " + value["code"] + " " + value["error"]); return (JObject)value["data"]; }
    static void Fail(LuaComponentApi api, string method, string args, string code) { var value = JObject.Parse(api.Call(method, args)); Need(!(bool)value["ok"] && (string)value["code"] == code, method + " expected " + code + ", got " + value); }
    static string Token(JObject value) => (string)value["entity"];
    static string Entity(string token) => "{\"entity\":\"" + token + "\"}";
    static string FirstToken(LuaComponentApi api, string tag) => Token((JObject)Ok(api, "entity.find", "{\"tag\":\"" + tag + "\"}")["entities"].First);
    static IEnumerator Delay(float seconds) { float until = Time.unscaledTime + seconds; while (Time.unscaledTime < until) yield return null; }
    static void Need(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    static void Block(string reason) { File.WriteAllText(Report, "BLOCKED: " + reason); if (Application.isBatchMode) EditorApplication.Exit(1); }
    static void Finish(string state) { work.Clear(); File.WriteAllText(Report, state + "\n" + string.Join("\n", results)); if (Application.isBatchMode) EditorApplication.Exit(state == "PASS" ? 0 : 1); else EditorApplication.isPlaying = false; }
}
