using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HMProtection.Core;
using HMProtection.Entities;
using HMProtection.EntityAdapters;
using HMProtection.Modules.Fire;
using HMProtection.Quiz;
using HMProtection.Scripting;
using HMProtection.Sessions;
using HMProtection.UI;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

/// <summary>
/// Full migration regression for the finished initial-fire lesson. It enters through the
/// serialized main-menu route and only answers through the real choice buttons and E input.
/// The lesson itself must be sequenced by the package Lua script.
/// </summary>
[InitializeOnLoad]
public static class OfficeLuaMigrationChecks
{
    const string MenuScene = "Assets/Scenes/初始界面.unity";
    const string OfficeScene = "Assets/Levels/level_1_initial_fire/Scenes/level_1_initial_fire.unity";
    const string Request = "Library/OfficeLuaMigrationChecks.request";
    const string Report = "Library/OfficeLuaMigrationChecks.txt";
    const string State = "OfficeLuaMigrationChecks.Running";
    const double TimeoutSeconds = 600d;

    static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    static readonly List<string> results = new List<string>();
    static double deadline;
    static string previousScene;
    static Keyboard keyboard, oldKeyboard;
    static InputSettings inputSettings;
    static InputSettings.BackgroundBehavior oldBackgroundBehavior;
    static InputSettings.EditorInputBehaviorInPlayMode oldEditorInputBehavior;

    static OfficeLuaMigrationChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(State, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
                SetupInput();
                work.Push(Check());
            }
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                work.Clear();
                CleanupInput();
            }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.SetBool(State, false);
                if (!string.IsNullOrEmpty(previousScene)) EditorSceneManager.OpenScene(previousScene);
            }
        };
    }

    [MenuItem("Tools/Levels/Check Initial Fire Lua Migration")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(MenuScene) == null)
        {
            Block("Main menu scene is missing: " + MenuScene);
            return;
        }
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(OfficeScene) == null)
        {
            Block("Packaged initial-fire scene is missing: " + OfficeScene);
            return;
        }
        var active = SceneManager.GetActiveScene();
        if (active.isDirty)
        {
            Block("Save the active scene before checks.");
            return;
        }
        previousScene = active.path;
        results.Clear();
        EditorSceneManager.OpenScene(MenuScene, OpenSceneMode.Single);
        SessionState.SetBool(State, true);
        EditorApplication.isPlaying = true;
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
            var activeBoot = UnityEngine.Object.FindAnyObjectByType<LevelBootstrapper>();
            Need(activeBoot == null || string.IsNullOrEmpty(activeBoot.LastError), "Bootstrap failed: " + activeBoot?.LastError);
            var activeHost = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();
            Need(activeHost == null || activeHost.Lua == null || string.IsNullOrEmpty(activeHost.Lua.LastError), "Lua failed: " + activeHost?.Lua?.LastError);
            Need(EditorApplication.timeSinceStartup < deadline, "Initial-fire Lua migration check timed out.");
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
        yield return EnterFromMainMenu();

        yield return Until(() => SceneManager.GetActiveScene().path == OfficeScene);
        var boot = UnityEngine.Object.FindAnyObjectByType<LevelBootstrapper>();
        yield return Until(() => boot != null && boot.Ready);
        var bridge = UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>();
        var host = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();
        var runner = bridge != null ? bridge.runner : null;
        Need(boot.definition != null && boot.definition.levelId == "level_1_initial_fire", "Initial-fire package definition was not consumed.");
        Need(boot.definition.scriptEnabled && !boot.definition.runLegacyStages, "Initial-fire package must enable Lua and disable legacy stages.");
        Need(bridge != null && bridge.IsReady && host != null && host.IsRunning && host.Lua != null && host.Lua.IsRunning,
            "Initial-fire entity scope, session host or Lua VM is not running.");
        Need(runner != null && ReadScriptDriven(runner), "Initial-fire LevelFlowRunner is not Lua-driven.");
        Need(bridge.TryGetFire(out _, out var fireError), "Initial-fire fire entity binding failed: " + fireError);
        Need(bridge.targets != null && bridge.targets.Length == 4, "Initial-fire entity target bindings are incomplete.");
        var enteredStages = new List<string>();
        Action<StageDto> captureStage = stage =>
        {
            if (stage != null && (enteredStages.Count == 0 || enteredStages[enteredStages.Count - 1] != stage.id))
                enteredStages.Add(stage.id);
        };
        runner.StageEntered += captureStage;
        captureStage(runner.CurrentStage);
        var fireController = PrimaryFireController(bridge);
        var fireStates = new List<FireState> { fireController.Model.CurrentState };
        Action<FireState> captureFire = state => fireStates.Add(state);
        fireController.Model.StateChanged += captureFire;
        var observer = host.Scripts.CreateClient("office-lua-migration.observer");
        try
        {
            Fail(observer, "flow.stage", "{\"index\":0}", "flow_not_owned");
            Fail(observer, "flow.prepare", "{}", "flow_owned");
        }
        finally { observer.Dispose(); }
        yield return null;
        Need(host.Lua != null && host.Lua.IsRunning, "Non-owner flow API failures interfered with the running Lua owner.");
        results.Add("PASS serialized menu selection loads the packaged initial-fire scene with Lua enabled, legacy stages disabled, and a ready entity scope");
        results.Add("PASS a non-owner API client cannot prepare or advance the Lua-owned flow and does not disturb its owner");

        yield return Until(() => host.Lua.UpdateCount > 0 && bridge.flow != null && bridge.flow.IsQuestionActive);
        runner.SetPaused(true);
        yield return null;
        Need(runner.IsPaused && host.IsBlocked(ControlMask.Simulation), "Lua-driven lesson pause did not block simulation.");
        runner.SetPaused(false);
        yield return Until(() => !runner.IsPaused && !host.IsBlocked(ControlMask.Simulation));
        results.Add("PASS Lua-driven lesson pause and resume retain the session control contract");

        yield return RunCorrectLesson(bridge, host, runner);
        var settlement = UnityEngine.Object.FindAnyObjectByType<TrainingSettlementView>();
        yield return Until(() => settlement != null && settlement.IsShowing && !runner.IsRunning && !QuizLoadingOverlay.IsVisible);
        Need(runner.Score.AskedCount == 3 && runner.Score.CorrectCount == 3 && runner.Score.TotalQuestions == 3,
            "Three correct real interactions did not reach the expected settlement score.");
        Need(runner.Score.Passed, "Perfect first run did not pass the configured lesson threshold.");
        var expectedStages = runner.Config.stages.Select(stage => stage.id).ToArray();
        Need(enteredStages.SequenceEqual(expectedStages), "Lua stage order differs from flow.json. Expected "
            + string.Join(",", expectedStages) + " but got " + string.Join(",", enteredStages));
        Need(fireStates.Contains(FireState.SmokeOnly) && fireStates.Contains(FireState.Small) && fireStates.Contains(FireState.None),
            "Successful Lua lesson did not produce SmokeOnly → Small → None fire states.");
        results.Add("PASS all 11 Lua stages run in flow.json order; three real E interactions pass and reach a settled, completed success report");
        results.Add("PASS successful flow drives smoke, small-fire and extinguished states through the bound fire entity");

        ScreenCapture.CaptureScreenshot("Library/OfficeLuaSettlement.png");
        yield return Delay(.3f);
        var oldClient = host.Scripts.CreateClient("office-lua-migration.old");
        string oldPlayer = Token(Ok(oldClient, "role.resolve", "{\"role\":\"player\"}"));
        var oldLua = host.Lua;
        yield return Delay(.5f); // Wait for the settlement entrance; its own button ignores early clicks.
        settlement.RetryButton.onClick.Invoke();
        runner.StageEntered -= captureStage;
        fireController.Model.StateChanged -= captureFire;
        yield return Until(() => UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>() != null
            && UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>() != bridge);
        bridge = UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>();
        host = UnityEngine.Object.FindAnyObjectByType<LevelSessionHost>();
        runner = bridge != null ? bridge.runner : null;
        yield return Until(() => bridge != null && bridge.IsReady && host != null && host.Lua != null && host.Lua.IsRunning
            && runner != null && ReadScriptDriven(runner) && bridge.flow != null && bridge.flow.IsQuestionActive);
        Need(!oldLua.IsRunning, "Retry left the previous Lua VM running.");
        Fail(oldClient, "session.info", "{}", "session_stopped");
        var fresh = host.Scripts.CreateClient("office-lua-migration.fresh");
        try { Fail(fresh, "entity.active.get", Entity(oldPlayer), "stale_entity"); }
        finally { fresh.Dispose(); oldClient.Dispose(); }
        results.Add("PASS the real settlement Retry button constructs a new Lua VM and rejects both the old API client and old entity token");

        var negativeFireController = PrimaryFireController(bridge);
        var negativeFireStates = new List<FireState> { negativeFireController.Model.CurrentState };
        Action<FireState> captureNegativeFire = state => negativeFireStates.Add(state);
        negativeFireController.Model.StateChanged += captureNegativeFire;
        yield return RunWrongAndTimeout(bridge, runner, negativeFireStates);
        settlement = UnityEngine.Object.FindAnyObjectByType<TrainingSettlementView>();
        yield return Until(() => settlement != null && settlement.IsShowing && !runner.IsRunning && !QuizLoadingOverlay.IsVisible);
        Need(runner.Score.AskedCount == 3 && runner.Score.CorrectCount == 1 && !runner.Score.Passed,
            "Failure run should settle at one correct answer out of three.");
        Need(negativeFireStates.Contains(FireState.Large), "Incorrect result did not expand the bound fire to Large.");
        results.Add("PASS incorrect E interaction, real timeout, and third-question E interaction reach the failed settlement with Large-fire feedback");

        var exitClient = host.Scripts.CreateClient("office-lua-migration.exit");
        var exitLua = host.Lua;
        yield return Delay(.5f);
        settlement.MenuButton.onClick.Invoke();
        negativeFireController.Model.StateChanged -= captureNegativeFire;
        yield return Until(() => SceneManager.GetActiveScene().path == MenuScene);
        Need(!exitLua.IsRunning, "Main-menu exit left the migrated Lua VM running.");
        Fail(exitClient, "session.info", "{}", "session_stopped");
        exitClient.Dispose();
        results.Add("PASS the real settlement Main Menu button releases the entity scope, component API, and Lua VM before returning to the menu");
    }

    static IEnumerator EnterFromMainMenu()
    {
        var menu = UnityEngine.Object.FindAnyObjectByType<MainMenuView>();
        Need(menu != null, "Main menu was not opened for migration entry validation.");
        var start = menu.GetComponentsInChildren<UnityEngine.UI.Button>(true).FirstOrDefault(button => button.name == "StartTraining");
        Need(start != null, "Main menu StartTraining button is missing.");
        start.onClick.Invoke();
        yield return Until(() =>
        {
            var select = UnityEngine.Object.FindAnyObjectByType<SceneSelectUI>();
            return select != null && select.windowContent != null && select.windowContent.activeInHierarchy;
        });
        var selection = UnityEngine.Object.FindAnyObjectByType<SceneSelectUI>();
        Need(selection.officeDefinition != null && selection.officeDefinition.scenePath == OfficeScene,
            "Main menu office selection is not bound to the packaged initial-fire definition.");
        selection.officeCard.onClick.Invoke();
        Need(selection.startButton.interactable, "Office selection did not enable Start Training.");
        selection.startButton.onClick.Invoke();
        results.Add("PASS main menu → office card → Start Training follows serialized production entry events");
    }

    static IEnumerator RunCorrectLesson(OfficeEntityBindings bridge, LevelSessionHost host, LevelFlowRunner runner)
    {
        var flow = bridge.flow;
        var owner = UnityEngine.Object.FindAnyObjectByType<OfficeChoiceInteraction>();
        Need(flow != null && owner != null && owner.feedback != null && owner.instructor != null, "Initial-fire interaction presentation is incomplete.");
        for (var index = 0; index < 3; index++)
        {
            yield return Until(() => flow.IsQuestionActive);
            var question = flow.CurrentQuestion;
            Need(question != null, "Lua did not present question " + (index + 1) + ".");
            var choice = Array.FindIndex(question.options, option => option.id == question.correctOptionId);
            Need(choice >= 0, "Question " + question.id + " lacks a selectable correct option.");
            var option = question.options[choice];
            flow.presenter.Bubbles[choice].button.onClick.Invoke();
            yield return Until(() => !flow.IsQuestionActive && owner.ArmedOption == option.id);
            yield return SubmitWithKeyboardE(bridge, option.id);
            yield return Until(() => owner.feedback.IsShowing && owner.Completed);
            Need(runner.Score.AskedCount == index + 1 && runner.Score.Answers[index].correct,
                "Correct E interaction was not recorded for " + option.id + ".");
            yield return DismissReview(owner);
            if (index < 2) yield return Until(() => flow.IsQuestionActive && host.Lua != null && host.Lua.IsRunning);
        }
    }

    static IEnumerator RunWrongAndTimeout(OfficeEntityBindings bridge, LevelFlowRunner runner, List<FireState> fireStates)
    {
        var flow = bridge.flow;
        var owner = UnityEngine.Object.FindAnyObjectByType<OfficeChoiceInteraction>();
        Need(flow != null && owner != null && owner.timer != null, "Negative-path test needs the real office timer and interaction owner.");
        yield return Until(() => flow.IsQuestionActive);
        var question = flow.CurrentQuestion;
        int wrongIndex = Array.FindIndex(question.options, option => option.id != question.correctOptionId);
        Need(wrongIndex >= 0, "Question has no incorrect option for migration coverage.");
        var wrong = question.options[wrongIndex];
        flow.presenter.Bubbles[wrongIndex].button.onClick.Invoke();
        yield return Until(() => !flow.IsQuestionActive && owner.ArmedOption == wrong.id);
        yield return SubmitWithKeyboardE(bridge, wrong.id);
        yield return Until(() => owner.feedback.IsShowing && owner.Completed);
        Need(runner.Score.AskedCount == 1 && runner.Score.CorrectCount == 0 && !runner.Score.Answers[0].correct,
            "Incorrect E interaction was not recorded as incorrect.");
        yield return DismissReview(owner);
        yield return Until(() => fireStates.Contains(FireState.Large));

        yield return Until(() => flow.IsQuestionActive);
        owner.timer.Begin(.15f);
        yield return Delay(.35f);
        yield return Until(() => owner.feedback.IsShowing && owner.Completed);
        Need(runner.Score.AskedCount == 2 && !runner.Score.Answers[1].correct && runner.Score.Answers[1].timedOut,
            "Real timer expiry did not record exactly one incorrect timeout.");
        owner.timer.ExpireIfDue();
        owner.timer.ExpireIfDue();
        yield return null;
        Need(runner.Score.AskedCount == 2, "Repeated timer expiry recorded duplicate answers.");
        yield return DismissReview(owner);

        yield return Until(() => flow.IsQuestionActive);
        question = flow.CurrentQuestion;
        int correctIndex = Array.FindIndex(question.options, option => option.id == question.correctOptionId);
        Need(correctIndex >= 0, "Third question has no correct option for failed-settlement coverage.");
        var correct = question.options[correctIndex];
        flow.presenter.Bubbles[correctIndex].button.onClick.Invoke();
        yield return Until(() => !flow.IsQuestionActive && owner.ArmedOption == correct.id);
        yield return SubmitWithKeyboardE(bridge, correct.id);
        yield return Until(() => owner.feedback.IsShowing && owner.Completed);
        Need(runner.Score.AskedCount == 3 && runner.Score.CorrectCount == 1 && runner.Score.Answers[2].correct,
            "Third E interaction did not record the expected correct answer.");
        yield return DismissReview(owner);
    }

    static IEnumerator DismissReview(OfficeChoiceInteraction owner)
    {
        yield return Delay(2.1f);
        owner.feedback.continueButton.onClick.Invoke();
        yield return Until(() => owner.instructor.IsShowing);
        while (owner.instructor.IsShowing)
        {
            yield return Delay(.25f);
            owner.instructor.Advance();
        }
    }

    static IEnumerator SubmitWithKeyboardE(OfficeEntityBindings bridge, string optionId)
    {
        var option = bridge.flow != null && bridge.flow.CurrentQuestion != null
            ? bridge.flow.CurrentQuestion.options.FirstOrDefault(candidate => candidate.id == optionId) : null;
        Need(option != null, "Current question has no option " + optionId + ".");
        var targetId = string.IsNullOrEmpty(option.interactionTargetId) ? optionId : option.interactionTargetId;
        Need(bridge.TryGetTargetPosition(targetId, out var position, out _, out var routeError), "Entity route for " + optionId + ": " + routeError);
        var binding = bridge.targets.Single(item => item.targetId == targetId);
        Need(bridge.Scope.Registry.TryResolve(binding.entityId, out var target, out var targetError), "Interaction target: " + targetError);
        Need(bridge.Scope.Registry.TryGetEntity(target, out var entity, out var entityError), "Interaction entity: " + entityError);
        var collider = entity.GetComponent<Collider>();
        Need(collider != null && bridge.player != null && bridge.player.PlayerCamera != null, "Interaction target/player/camera is missing.");
        var controller = bridge.player.GetComponent<CharacterController>();
        Need(controller != null, "Player CharacterController is missing.");
        controller.enabled = false;
        bridge.player.transform.position = new Vector3(position.x, bridge.player.transform.position.y, position.z);
        controller.enabled = true;
        yield return WalkIntoRange(bridge.player, controller, collider, optionId);
        var direction = collider.bounds.center - bridge.player.PlayerCamera.transform.position;
        bridge.player.transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, 0f);
        bridge.player.SetPitch(-Mathf.Atan2(direction.y, new Vector2(direction.x, direction.z).magnitude) * Mathf.Rad2Deg);
        Physics.SyncTransforms();
        yield return null;
        var raycast = Physics.Raycast(bridge.player.PlayerCamera.transform.position, bridge.player.PlayerCamera.transform.forward, out var hit, 2.8f);
        EntityHandle hitEntity = default;
        var mapped = raycast && bridge.Scope.Registry.TryResolveHit(hit.collider, out hitEntity, out _);
        Need(raycast && mapped && hitEntity.Equals(target), "E ray did not resolve the expected entity target " + optionId + " (" + targetId + ").");
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.E));
        yield return Delay(.25f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
    }

    static IEnumerator WalkIntoRange(body player, CharacterController controller, Collider target, string optionId)
    {
        for (var index = 0; index < 96; index++)
        {
            var toTarget = target.bounds.center - player.PlayerCamera.transform.position;
            var horizontal = new Vector3(toTarget.x, 0f, toTarget.z);
            var allowed = Mathf.Max(.45f, Mathf.Sqrt(Mathf.Max(.01f, 2.55f * 2.55f - toTarget.y * toTarget.y)));
            if (horizontal.magnitude <= allowed) yield break;
            controller.Move(horizontal.normalized * .075f);
            yield return null;
        }
        throw new InvalidOperationException("Collision-aware approach exceeded range for " + optionId + ".");
    }

    static bool ReadScriptDriven(LevelFlowRunner runner)
    {
        var property = typeof(LevelFlowRunner).GetProperty("ScriptDriven");
        Need(property != null && property.PropertyType == typeof(bool), "LevelFlowRunner must expose bool ScriptDriven for Lua migration.");
        return (bool)property.GetValue(runner);
    }

    static FireEffectController PrimaryFireController(OfficeEntityBindings bridge)
    {
        Need(bridge.TryRole("primaryFire", out var handle, out var roleError), "Primary fire role: " + roleError);
        Need(bridge.Scope.Registry.TryGetEntity(handle, out var entity, out var entityError), "Primary fire entity: " + entityError);
        var controller = entity.GetComponent<FireEffectController>();
        Need(controller != null, "Primary fire entity has no FireEffectController.");
        return controller;
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
    static IEnumerator Delay(float seconds) { var end = Time.unscaledTime + seconds; while (Time.unscaledTime < end) yield return null; }
    static IEnumerator Until(Func<bool> predicate) { while (!predicate()) yield return null; }

    static void SetupInput()
    {
        inputSettings = InputSystem.settings;
        oldBackgroundBehavior = inputSettings.backgroundBehavior;
        oldEditorInputBehavior = inputSettings.editorInputBehaviorInPlayMode;
        inputSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        inputSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        oldKeyboard = Keyboard.current;
        keyboard = InputSystem.AddDevice<Keyboard>();
        keyboard.MakeCurrent();
    }

    static void CleanupInput()
    {
        if (keyboard != null) { InputSystem.RemoveDevice(keyboard); keyboard = null; oldKeyboard?.MakeCurrent(); }
        if (inputSettings != null)
        {
            inputSettings.backgroundBehavior = oldBackgroundBehavior;
            inputSettings.editorInputBehaviorInPlayMode = oldEditorInputBehavior;
            inputSettings = null;
        }
    }

    static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Block(string reason) { File.WriteAllText(Report, "BLOCKED: " + reason); if (Application.isBatchMode) EditorApplication.Exit(1); }
    static void Finish(string outcome)
    {
        work.Clear();
        File.WriteAllText(Report, outcome + "\n" + string.Join("\n", results));
        CleanupInput();
        if (Application.isBatchMode) EditorApplication.Exit(outcome == "PASS" ? 0 : 1);
        else EditorApplication.isPlaying = false;
    }
}
