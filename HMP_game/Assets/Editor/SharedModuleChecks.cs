using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HMProtection.Core;
using HMProtection.Entities;
using HMProtection.EntityAdapters;
using HMProtection.Modules.Fire;
using HMProtection.Modules.Interaction;
using HMProtection.Navigation;
using HMProtection.Quiz;
using HMProtection.Sessions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Opt-in Play-mode verification for the isolated third-scene shared-module pilot.</summary>
[InitializeOnLoad]
public static class SharedModuleChecks
{
    private const string PilotScene = "Assets/Scenes/第三场景-通用模块试点.unity";
    private const string Request = "Library/SharedModuleChecks.request";
    private const string Report = "Library/SharedModuleChecks.txt";
    private const string StateKey = "SharedModuleChecks.Running";
    private static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    private static readonly List<string> results = new List<string>();
    private static double deadline;

    static SharedModuleChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(StateKey, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                deadline = EditorApplication.timeSinceStartup + 180d;
                work.Push(Check());
            }
            else if (state == PlayModeStateChange.ExitingPlayMode) work.Clear();
            else if (state == PlayModeStateChange.EnteredEditMode) SessionState.SetBool(StateKey, false);
        };
    }

    [MenuItem("Tools/Shared Modules/Check Third-Scene Pilot")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(PilotScene) == null) { Block("pilot scene does not exist; run SharedModuleSetup first."); return; }
        if (SceneManager.GetActiveScene().isDirty) { Block("save the active scene before checks."); return; }
        EditorSceneManager.OpenScene(PilotScene, OpenSceneMode.Single);
        SessionState.SetBool(StateKey, true);
        EditorApplication.isPlaying = true;
    }

    /// <summary>Batch-mode entry point. Completion writes Library/SharedModuleChecks.txt and exits.</summary>
    public static void BeginBatch() => Begin();
    public static void InstallAndBeginBatch()
    {
        SharedModuleSetup.CreateThirdScenePilotAndInstall();
        Begin();
    }

    private static void Tick()
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
            Need(EditorApplication.timeSinceStartup < deadline, "shared-module pilot check timed out.");
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

    private static IEnumerator Check()
    {
        yield return Delay(2f);
        var bridge = UnityEngine.Object.FindAnyObjectByType<EntityInteractionBridge>();
        var sessions = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();
        Need(bridge != null && bridge.IsReady && bridge.Scope != null, "EntityInteractionBridge scope is not ready.");
        Need(sessions != null && sessions.IsRunning, "LevelSessionHost is not running.");
        Need(UnityEngine.Object.FindAnyObjectByType<OfficeFireChoiceFlow>() == null
            && UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>() == null
            && UnityEngine.Object.FindAnyObjectByType<OfficeChoiceTarget>() == null,
            "Pilot contains Office-specific runtime components.");
        var scope = bridge.Scope;
        Need(scope.Registry.TryResolve("player.main", out var player, out var playerError), "player: " + playerError);
        Need(bridge.TryRolePose("spawn", "origin", out _, out var spawnError), "spawn: " + spawnError);
        Need(scope.Registry.TryResolve("fire.third.a", out var fireA, out var fireAError), "fire A: " + fireAError);
        Need(scope.Registry.TryResolve("fire.third.b", out var fireB, out var fireBError), "fire B: " + fireBError);
        Need(scope.Registry.TryGetCapability<IFireCapability>(fireA, out var firstFire, out fireAError), "fire A cap: " + fireAError);
        Need(scope.Registry.TryGetCapability<IFireCapability>(fireB, out var secondFire, out fireBError), "fire B cap: " + fireBError);
        var firstControllerAtStart = FindController("fire.third.a", scope);
        var secondControllerAtStart = FindController("fire.third.b", scope);
        Need(firstFire.CurrentState == FireState.Small && secondFire.CurrentState == FireState.Medium
            && firstControllerAtStart.HasFire && secondControllerAtStart.HasFire,
            "Definition A did not initialize two visible fire sources on normal Play startup.");
        Need((firstControllerAtStart.transform.position - secondControllerAtStart.transform.position).sqrMagnitude > .01f,
            "two copied fire sources occupy the same visible position.");
        var pilotBootstrapper = UnityEngine.Object.FindAnyObjectByType<LevelBootstrapper>();
        Need(pilotBootstrapper != null && (pilotBootstrapper.GetComponent<LevelFlowRunner>() == null || !pilotBootstrapper.GetComponent<LevelFlowRunner>().enabled)
            && (pilotBootstrapper.GetComponent<StageTimer>() == null || !pilotBootstrapper.GetComponent<StageTimer>().enabled),
            "legacy Scene 3 stage drivers are still enabled in the shared-module pilot.");
        results.Add("PASS ready session, entity scope, player/spawn roles and two fire capabilities");

        yield return VerifyInteractionCommands(scope, bridge, player);

        Need(firstFire.TrySetState(FireState.Small, out var firstSet), "set fire A: " + firstSet);
        Need(secondFire.TrySetState(FireState.Large, out var secondSet), "set fire B: " + secondSet);
        Need(firstFire.CurrentState == FireState.Small && secondFire.CurrentState == FireState.Large,
            "two fire entities did not retain independent states.");
        results.Add("PASS dual fire entities retain independent state through entity capabilities");
        yield return SharedFirePhysicsChecks.Run(bridge, results.Add);

        Need(sessions.Session.Timers.Start(1f, ClockDomain.Simulation, _ => { }, out var timer, out var timerError), "session timer: " + timerError);
        Need(sessions.Session.Timers.TryGetRemaining(timer, out var before), "timer missing before paused tick.");
        using (sessions.Acquire("shared-module-check", ControlMask.Simulation))
        {
            yield return Delay(.2f);
            var firstController = FindController("fire.third.a", scope);
            firstController.RefreshSimulationPause();
            Need(firstController.Model.IsSimulationPaused, "simulation control lease did not pause the fire model.");
            Need(sessions.Session.Timers.TryGetRemaining(timer, out var during) && Mathf.Approximately(before, during),
                "simulation control lease did not pause the session simulation timer.");
        }
        yield return Delay(.2f);
        FindController("fire.third.a", scope).RefreshSimulationPause();
        Need(!FindController("fire.third.a", scope).Model.IsSimulationPaused, "releasing simulation lease did not resume the fire model.");
        Need(sessions.Session.Timers.TryGetRemaining(timer, out var after) && after < before,
            "session simulation timer did not resume after the lease released.");
        sessions.Session.Timers.Cancel(timer);
        results.Add("PASS simulation control lease pauses and resumes fire and shared simulation timer");

        yield return VerifyDefinitionSwitchAndRepeatedScopeEntries(bridge, sessions);
        bridge = UnityEngine.Object.FindAnyObjectByType<EntityInteractionBridge>();
        scope = bridge.Scope;
        sessions = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();

        var disposable = new GameObject("shared-module-destroyed-entity");
        var disposableEntity = disposable.AddComponent<GameEntity>();
        disposableEntity.Configure("runtime.shared.destroyed", new[] { "runtime" }, disposable);
        Need(scope.Registry.TryRegister(disposableEntity, out var destroyedHandle, out var registerError), "dynamic entity registration: " + registerError);
        UnityEngine.Object.Destroy(disposable);
        yield return null;
        Need(!scope.Registry.TryGetEntity(destroyedHandle, out _, out _), "destroyed entity remained commandable through its old handle.");
        Need(!scope.Registry.IsValid(destroyedHandle), "destroyed entity old handle remained valid.");
        results.Add("PASS destroyed scoped entity is purged and its stale command handle is rejected");

        yield return VerifyNavigationDefinitionAndMenuReturn(scope, sessions);
    }

    private static IEnumerator VerifyDefinitionSwitchAndRepeatedScopeEntries(EntityInteractionBridge bridge, LevelSessionHost sessions)
    {
        var bootstrapper = UnityEngine.Object.FindAnyObjectByType<LevelBootstrapper>();
        Need(bootstrapper != null && bootstrapper.definition != null, "pilot has no LevelBootstrapper definition.");
        var first = bootstrapper.definition;
        var second = AssetDatabase.LoadAssetAtPath<LevelDefinition>("Assets/GameContent/LevelDefinitions/third_scene_shared_modules_b.asset");
        Need(second != null && second != first && second.scenePath == first.scenePath && second.levelId != first.levelId,
            "same-scene alternate definition is missing or invalid.");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var oldScope = bridge.Scope;
            EntityHandle oldHandle = default;
            EntityError oldError = default;
            Need(oldScope != null && oldScope.Registry.TryResolve("player.main", out oldHandle, out oldError),
                "pre-reentry player " + attempt + ": " + oldError);
            var next = attempt % 2 == 0 ? second : first;
            bridge.DisposeScope();
            yield return null;
            Need(!oldScope.Registry.IsValid(oldHandle), "scope re-entry " + attempt + " kept prior handle valid.");
            Need(!sessions.IsRunning, "scope re-entry " + attempt + " kept prior session running.");
            bootstrapper.definition = next;
            Need(bootstrapper.PrepareLevel(), "definition " + next.levelId + " could not reinitialize: " + bootstrapper.LastError);
            yield return null;
            bridge = UnityEngine.Object.FindAnyObjectByType<EntityInteractionBridge>();
            sessions = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();
            Need(bridge != null && bridge.IsReady && sessions != null && sessions.IsRunning, "scope re-entry " + attempt + " did not recreate ready bindings/session.");
            Need(bootstrapper.Entry != null && bootstrapper.Entry.id == next.levelId, "definition switch did not select " + next.levelId + ".");
            Need(bridge.Scope.Registry.TryResolve("player.main", out var replacement, out var replacementError),
                "replacement player " + attempt + ": " + replacementError);
            Need(!replacement.Equals(oldHandle), "scope re-entry " + attempt + " reused a stale player handle.");
        }
        results.Add("PASS two same-scene definitions switch level IDs and ten scope exits/re-entries invalidate every prior handle");
    }

    private static IEnumerator VerifyNavigationDefinitionAndMenuReturn(EntityScope oldScope, LevelSessionHost oldSession)
    {
        Need(oldScope.Registry.TryResolve("player.main", out var oldHandle, out var oldError), "pre-navigation player: " + oldError);
        var definition = AssetDatabase.LoadAssetAtPath<LevelDefinition>("Assets/GameContent/LevelDefinitions/third_scene_shared_modules_b.asset");
        Need(definition != null, "navigation definition B is missing.");
        var navigation = AppNavigationService.Instance;
        Need(navigation.TryLoadDefinition(definition, out var operation, out var navigationError), "definition navigation: " + navigationError);
        Need(!navigation.TryLoadSingle("Assets/Scenes/初始界面.unity", out _, out var overlapError),
            "overlapping navigation request was accepted: " + overlapError);
        yield return WaitForNavigation(operation, NavigationState.ReadyToActivate, "definition B readiness");
        Need(!operation.TryCancel() && !navigation.TryCancel(operation), "native scene load was incorrectly cancellable after it started.");
        operation.AllowActivation();
        yield return WaitForTerminal(operation, "definition B activation");
        Need(operation.State == NavigationState.Completed, "definition B navigation ended as " + operation.State + ": " + operation.Error);
        Need(!oldScope.Registry.IsValid(oldHandle), "definition navigation left the old scope handle valid.");
        Need(oldSession == null || !oldSession.IsRunning, "definition navigation left the old session running.");

        yield return Delay(1f);
        var freshBootstrapper = UnityEngine.Object.FindObjectsByType<LevelBootstrapper>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .FirstOrDefault(item => item.Entry != null && item.Entry.id == definition.levelId);
        Need(freshBootstrapper != null, "destination bootstrapper did not consume definition " + definition.levelId + ".");
        var freshBridge = UnityEngine.Object.FindAnyObjectByType<EntityInteractionBridge>();
        Need(freshBridge != null && freshBridge.IsReady, "definition B destination scope is not ready.");
        Need(FindController("fire.third.a", freshBridge.Scope).CurrentLevel == FireLevel.Large
            && FindController("fire.third.b", freshBridge.Scope).CurrentLevel == FireLevel.SmokeOnly,
            "Definition B did not apply its distinct initial fire configuration.");
        results.Add("PASS same-scene navigation consumes Definition B, rejects overlap, rejects post-native-load cancellation, and invalidates prior scope");

        Need(navigation.TryLoadSingle("Assets/Scenes/初始界面.unity", out var menuOperation, out navigationError), "menu navigation: " + navigationError);
        menuOperation.AllowActivation();
        yield return WaitForTerminal(menuOperation, "menu activation");
        Need(menuOperation.State == NavigationState.Completed, "menu navigation ended as " + menuOperation.State + ": " + menuOperation.Error);
        yield return null;
        Need(UnityEngine.Object.FindObjectsByType<LevelSessionHost>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .All(host => host == null || !host.IsRunning), "a LevelSessionHost remained running after menu return.");
        results.Add("PASS menu return completes navigation and leaves no running level session");
    }

    private static IEnumerator WaitForNavigation(NavigationOperation operation, NavigationState expected, string label)
    {
        while (operation != null && !operation.IsTerminal && operation.State != expected)
        {
            Need(EditorApplication.timeSinceStartup < deadline, label + " timed out.");
            yield return null;
        }
        Need(operation != null && operation.State == expected, label + " ended as " + (operation == null ? "null" : operation.State.ToString()) + ": " + operation?.Error);
    }

    private static IEnumerator WaitForTerminal(NavigationOperation operation, string label)
    {
        while (operation != null && !operation.IsTerminal)
        {
            Need(EditorApplication.timeSinceStartup < deadline, label + " timed out.");
            yield return null;
        }
        Need(operation != null && operation.IsTerminal, label + " did not reach a terminal state.");
    }

    private static IEnumerator VerifyInteractionCommands(EntityScope scope, EntityInteractionBridge bridge, EntityHandle player)
    {
        var door = scope.Registry.FindByTag("door").FirstOrDefault();
        var seat = scope.Registry.FindByTag("seat").FirstOrDefault();
        var pickup = scope.Registry.FindByTag("pickup").FirstOrDefault();
        Need(door.IsValid && seat.IsValid && pickup.IsValid, "door, seat and pickup entities must be registered.");

        Need(scope.Registry.TryGetCapability<IDoorCapability>(door, out var doorCapability, out var doorError), "door capability: " + doorError);
        Need(doorCapability.TrySetOpen(player, true, out var doorCommandError), "door command: " + doorCommandError);

        Need(scope.Registry.TryGetCapability<IPickupCapability>(pickup, out var pickupCapability, out var pickupError), "pickup capability: " + pickupError);
        Need(bridge.TryInteract(pickup, out var pickupCommandError), "pickup interaction: " + pickupCommandError);
        Need(pickupCapability.IsHeld, "pickup command did not set held state.");
        Need(pickupCapability.TryDrop(player, false, out pickupCommandError), "pickup drop: " + pickupCommandError);

        Need(scope.Registry.TryGetCapability<ISeatCapability>(seat, out var seatCapability, out var seatError), "seat capability: " + seatError);
        Need(bridge.TryInteract(seat, out var seatCommandError), "seat interaction: " + seatCommandError);
        yield return Delay(.6f);
        Need(seatCapability.IsOccupied, "seat command did not occupy the seat.");
        Need(seatCapability.TryLeave(player, out seatCommandError), "seat leave: " + seatCommandError);
        yield return Delay(.6f);
        Need(!seatCapability.IsOccupied, "seat leave command did not restore idle state.");
        results.Add("PASS door, pickup and seat commands execute through entity capabilities");
    }

    private static FireEffectController FindController(string id, EntityScope scope)
    {
        Need(scope.Registry.TryResolve(id, out var handle, out var error), "fire resolve: " + error);
        Need(scope.Registry.TryGetEntity(handle, out var entity, out error), "fire entity: " + error);
        var controller = entity.GetComponent<FireEffectController>();
        Need(controller != null, "fire entity has no controller.");
        return controller;
    }
    private static IEnumerator Delay(float seconds)
    {
        var until = Time.unscaledTime + seconds;
        while (Time.unscaledTime < until) yield return null;
    }
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Block(string reason)
    {
        File.WriteAllText(Report, "BLOCKED: " + reason);
        if (Application.isBatchMode) EditorApplication.Exit(1);
    }
    private static void Finish(string outcome)
    {
        work.Clear();
        File.WriteAllText(Report, outcome + "\n" + string.Join("\n", results));
        if (Application.isBatchMode) EditorApplication.Exit(outcome == "PASS" ? 0 : 1);
        else EditorApplication.isPlaying = false;
    }
}
