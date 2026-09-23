using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HMProtection.Core;
using HMProtection.EntityAdapters;
using HMProtection.LevelPackages;
using HMProtection.Modules.Fire;
using HMProtection.Navigation;
using HMProtection.Scripting;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Opt-in end-to-end validation for packaged levels and definition-driven navigation.</summary>
[InitializeOnLoad]
public static class LevelPackageIntegrationChecks
{
    const string FirstScene = "Assets/Levels/third_scene_shared_modules_a/Scenes/third_scene_shared_modules_a.unity";
    const string SecondDefinition = "Assets/Levels/third_scene_shared_modules_b/Runtime/third_scene_shared_modules_b.asset";
    const string OfficeDefinition = "Assets/Levels/level_1_initial_fire/Runtime/level_1_initial_fire.asset";
    const string Request = "Library/LevelPackageIntegrationChecks.request";
    const string Report = "Library/LevelPackageIntegrationChecks.txt";
    const string State = "LevelPackageIntegrationChecks.Running";
    const double TimeoutSeconds = 120d;

    static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    static readonly List<string> results = new List<string>();
    static double deadline;

    static LevelPackageIntegrationChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(State, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
                work.Push(Check());
            }
            if (state == PlayModeStateChange.ExitingPlayMode) work.Clear();
            if (state == PlayModeStateChange.EnteredEditMode) SessionState.SetBool(State, false);
        };
    }

    [MenuItem("Tools/Levels/Check Level Packages")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        results.Clear();
        try
        {
            LevelPackageTools.ValidateAll();
            Need(AssetDatabase.LoadAssetAtPath<SceneAsset>(FirstScene) != null, "Package A scene is missing.");
            Need(AssetDatabase.LoadAssetAtPath<LevelDefinition>(SecondDefinition) != null, "Package B definition is missing.");
            Need(AssetDatabase.LoadAssetAtPath<LevelDefinition>(OfficeDefinition) != null, "Office package definition is missing.");
            if (SceneManager.GetActiveScene().isDirty) throw new InvalidOperationException("Save the active scene before checks.");
            EditorSceneManager.OpenScene(FirstScene, OpenSceneMode.Single);
            SessionState.SetBool(State, true);
            EditorApplication.isPlaying = true;
        }
        catch (Exception exception)
        {
            results.Add(exception.ToString());
            Finish("FAIL");
        }
    }

    public static void BeginBatch() => Begin();

    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode && File.Exists(Request))
        {
            File.Delete(Request);
            Begin();
            return;
        }
        if (!EditorApplication.isPlaying || work.Count == 0) return;
        try
        {
            Need(EditorApplication.timeSinceStartup < deadline, "Level package integration check timed out.");
            var current = work.Peek();
            if (!current.MoveNext())
            {
                work.Pop();
                if (work.Count == 0) Finish("PASS");
            }
            else if (current.Current is IEnumerator nested) work.Push(nested);
        }
        catch (Exception exception)
        {
            results.Add(exception.ToString());
            Finish("FAIL");
        }
    }

    static IEnumerator Check()
    {
        yield return Delay(1f);
        var bootA = FindBoot();
        Need(bootA.Ready && bootA.definition != null, "Package A bootstrapper is not ready.");
        AssertPackage(bootA, "third_scene_shared_modules_a");
        bootA.definition.initialFires = Array.Empty<LevelDefinition.FireInitialState>();
        Need(bootA.definition.Validate(out var jsonError), "Runtime JSON refresh failed: " + jsonError);
        Need(bootA.definition.initialFires.Length == 2, "JSON did not override stale generated initial fire fields.");
        results.Add("PASS runtime definition re-reads level.json instead of relying on generated field snapshots");
        AssertFireStates(bootA, "Small", "Medium");
        var oldHost = FindHost();
        Need(oldHost.IsRunning && oldHost.Scripts != null, "Package A script host is unavailable.");
        var oldApi = oldHost.Scripts.CreateClient("level-package-a");
        string oldToken = Token(Ok(oldApi, "entity.resolve", "{\"id\":\"fire.third.a\"}"));
        Need((string)Ok(oldApi, "fire.get", Entity(oldToken))["state"] == "Small", "Lua fire.get did not expose package A state.");
        results.Add("PASS package A definition, initial fire state and Lua facade");

        var definitionB = AssetDatabase.LoadAssetAtPath<LevelDefinition>(SecondDefinition);
        Need(AppNavigationService.Instance.TryLoadDefinition(definitionB, out var transitionB, out var transitionError), "Package B navigation rejected: " + transitionError);
        transitionB.AllowActivation();
        yield return WaitForTerminal(transitionB, "package B navigation");
        Need(transitionB.State == NavigationState.Completed, "Package B navigation ended as " + transitionB.State + ": " + transitionB.Error);
        yield return UntilReady("Package B bootstrapper");
        var bootB = FindBoot();
        Need(bootB.Ready && bootB.definition == definitionB, "Package B definition was not consumed by destination bootstrapper.");
        AssertPackage(bootB, "third_scene_shared_modules_b");
        AssertFireStates(bootB, "Large", "SmokeOnly");
        Fail(oldApi, "session.info", "{}", "session_stopped");
        var hostB = FindHost();
        var apiB = hostB.Scripts.CreateClient("level-package-b");
        try { Fail(apiB, "entity.active.get", Entity(oldToken), "stale_entity"); }
        finally { apiB.Dispose(); oldApi.Dispose(); }
        results.Add("PASS definition navigation installs B, invalidates old session/API/token and applies Large/SmokeOnly");

        var office = AssetDatabase.LoadAssetAtPath<LevelDefinition>(OfficeDefinition);
        Need(AppNavigationService.Instance.TryLoadDefinition(office, out var officeTransition, out var officeError), "Office package navigation rejected: " + officeError);
        officeTransition.AllowActivation();
        yield return WaitForTerminal(officeTransition, "office package navigation");
        Need(officeTransition.State == NavigationState.Completed, "Office package navigation ended as " + officeTransition.State + ": " + officeTransition.Error);
        yield return UntilReady("Office package bootstrapper");
        var officeBoot = FindBoot();
        Need(officeBoot.Ready && officeBoot.definition == office, "Office definition was not consumed by destination bootstrapper.");
        AssertPackage(officeBoot, "level_1_initial_fire");
        var officeRunner = officeBoot.GetComponent<LevelFlowRunner>();
        Need(officeRunner != null && officeRunner.ScriptDriven && officeRunner.Config.stages.Length > 0, "Office Lua did not prepare its stage services.");
        Need(AssetDatabase.GetAssetPath(officeBoot.definition.questionCatalog).StartsWith("Assets/Levels/level_1_initial_fire/", StringComparison.Ordinal), "Office question catalog is not packaged with the office level.");
        Need(!officeBoot.definition.runLegacyStages && officeBoot.definition.scriptConfig != null, "Office must use its package script config.");
        Need(!string.IsNullOrWhiteSpace(officeBoot.definition.luaSource), "Office packaged Lua source is empty.");
        Need(officeBoot.definition.scriptEnabled && FindHost().Lua.IsRunning, "Office package Lua must run.");
        var officeApi = FindHost().Scripts.CreateClient("level-package-office");
        try
        {
            string officeFire = Token(Ok(officeApi, "role.resolve", "{\"role\":\"primaryFire\"}"));
            Need(!string.IsNullOrWhiteSpace((string)Ok(officeApi, "fire.get", Entity(officeFire))["state"]), "Office fire role is not script-addressable.");
        }
        finally { officeApi.Dispose(); }
        results.Add("PASS office package script config, bundled question catalog, Lua flow and fire role");

        officeBoot.ReleaseEntities();
        yield return null;
        var finalHost = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();
        Need(finalHost == null || !finalHost.IsRunning, "ReleaseEntities left the office session running.");
        results.Add("PASS package shutdown releases the final session");
    }

    static void AssertPackage(LevelBootstrapper boot, string id)
    {
        Need(boot.definition.packageConfig != null, id + " has no package config asset.");
        Need(boot.definition.levelId == id, "Expected package ID " + id + ", got " + boot.definition.levelId + ".");
        Need(!string.IsNullOrWhiteSpace(boot.definition.packageRoot), id + " package root is empty.");
    }

    static void AssertFireStates(LevelBootstrapper boot, string first, string second)
    {
        var host = FindHost();
        var api = host.Scripts.CreateClient("level-package-state-" + boot.definition.levelId);
        try
        {
            string a = Token(Ok(api, "entity.resolve", "{\"id\":\"fire.third.a\"}"));
            string b = Token(Ok(api, "entity.resolve", "{\"id\":\"fire.third.b\"}"));
            Need((string)Ok(api, "fire.get", Entity(a))["state"] == first, "fire.third.a expected " + first + ".");
            Need((string)Ok(api, "fire.get", Entity(b))["state"] == second, "fire.third.b expected " + second + ".");
        }
        finally { api.Dispose(); }
    }

    static LevelBootstrapper FindBoot()
    {
        var boot = UnityEngine.Object.FindAnyObjectByType<LevelBootstrapper>();
        Need(boot != null, "No LevelBootstrapper is active.");
        return boot;
    }

    static LevelSessionHost FindHost()
    {
        var host = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();
        Need(host != null && host.IsRunning && host.Scripts != null, "No running LevelSessionHost is active.");
        return host;
    }

    static IEnumerator WaitForTerminal(NavigationOperation operation, string label)
    {
        while (!operation.IsTerminal) yield return null;
        Need(operation.State == NavigationState.Completed, label + " failed: " + operation.State + " " + operation.Error);
    }

    static IEnumerator UntilReady(string label)
    {
        while (true)
        {
            var boot = UnityEngine.Object.FindAnyObjectByType<LevelBootstrapper>();
            if (boot != null && boot.Ready) yield break;
            yield return null;
        }
    }

    static IEnumerator Delay(float seconds)
    {
        float until = Time.unscaledTime + seconds;
        while (Time.unscaledTime < until) yield return null;
    }

    static JObject Ok(LuaComponentApi api, string method, string args)
    {
        var value = JObject.Parse(api.Call(method, args));
        Need((bool)value["ok"], method + " failed: " + value["code"] + " " + value["error"]);
        return (JObject)value["data"];
    }

    static void Fail(LuaComponentApi api, string method, string args, string code)
    {
        var value = JObject.Parse(api.Call(method, args));
        Need(!(bool)value["ok"] && (string)value["code"] == code, method + " expected " + code + ", got " + value);
    }

    static string Token(JObject value) => (string)value["entity"];
    static string Entity(string token) => "{\"entity\":\"" + token + "\"}";
    static void Need(bool value, string error) { if (!value) throw new InvalidOperationException(error); }

    static void Finish(string state)
    {
        work.Clear();
        File.WriteAllText(Report, state + "\n" + string.Join("\n", results));
        if (Application.isBatchMode) EditorApplication.Exit(state == "PASS" ? 0 : 1);
        else if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
    }
}
