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
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

/// <summary>
/// Opt-in Play-mode regression for the isolated office entity pilot.  It drives the three
/// entity-routed office interactions through feedback, review, settlement and RestartLevel.
/// </summary>
[InitializeOnLoad]
public static class EntityIntegrationChecks
{
    const string Request = "Library/EntityIntegrationChecks.request";
    const string Report = "Library/EntityIntegrationChecks.txt";
    const string Session = "EntityIntegrationChecks";
    const string PilotScene = "Assets/Scenes/办公室场景-实体试点.unity";
    static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    static readonly List<string> results = new List<string>();
    static double deadline;
    static Keyboard keyboard, oldKeyboard;
    static InputSettings inputSettings;
    static InputSettings.BackgroundBehavior oldBackgroundBehavior;
    static InputSettings.EditorInputBehaviorInPlayMode oldEditorInputBehavior;

    static EntityIntegrationChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Session, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                results.Clear();
                deadline = EditorApplication.timeSinceStartup + 450;
                SetupInput();
                work.Push(Check());
            }
            if (state == PlayModeStateChange.ExitingPlayMode) { work.Clear(); CleanupInput(); }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.SetBool(Session, false);
                var previous = SessionState.GetString(Session + "Scene", "");
                if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous);
            }
        };
    }

    [MenuItem("Tools/Entity System/Check Office Pilot Baseline")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(PilotScene))
        {
            Block("office entity pilot does not exist. Run EntitySetup first.");
            return;
        }
        var active = SceneManager.GetActiveScene();
        if (active.isDirty)
        {
            Block("save the active scene before checks.");
            return;
        }
        SessionState.SetString(Session + "Scene", active.path);
        EditorSceneManager.OpenScene(PilotScene, OpenSceneMode.Single);
        SessionState.SetBool(Session, true);
        EditorApplication.isPlaying = true;
    }

    /// <summary>Batch entry.  The caller must keep the editor alive until the report is written.</summary>
    public static void BeginBatch() => Begin();

    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode && File.Exists(Request))
        {
            File.Delete(Request);
            SessionState.SetBool(Session + "Pending", true);
            AssetDatabase.Refresh();
            return;
        }
        if (!EditorApplication.isPlayingOrWillChangePlaymode && SessionState.GetBool(Session + "Pending", false))
        {
            SessionState.SetBool(Session + "Pending", false);
            Begin();
        }
        if (!EditorApplication.isPlaying || work.Count == 0) return;
        try
        {
            Need(EditorApplication.timeSinceStartup < deadline, "Entity pilot check timed out.");
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
        yield return Delay(2f);
        var bridge = UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>();
        Need(bridge != null, "OfficeEntityBindings is missing from the pilot.");
        Need(bridge.IsReady && bridge.Scope != null, "Office entity scope did not finish installation.");
        var scope = bridge.Scope;
        var ids = new[]
        {
            "player.main", "anchor.player.spawn", "fire.office.main",
            "target.office.printer", "target.office.window", "target.office.entrance", "target.office.power_strip"
        };
        var handles = new Dictionary<string, EntityHandle>();
        foreach (var id in ids)
        {
            Need(scope.Registry.TryResolve(id, out var handle, out var error), "Required entity " + id + ": " + error);
            handles.Add(id, handle);
        }
        Need(bridge.TryRole("player", out var player, out var playerError) && player.Equals(handles["player.main"]), "Player binding: " + playerError);
        Need(bridge.TryRolePose("spawn", "origin", out _, out var spawnError), "Spawn anchor: " + spawnError);
        Need(bridge.TryGetFire(out _, out var fireError), "Fire capability: " + fireError);
        foreach (var target in bridge.targets)
            Need(bridge.TryGetTargetPosition(target.targetId, out _, out _, out var targetError), "Target binding " + target.targetId + ": " + targetError);
        results.Add("PASS required IDs, roles, target anchors and fire capability resolve through the ready scope");

        var renamed = FindEntity(scope, handles["target.office.window"]);
        var oldName = renamed.gameObject.name;
        var oldParent = renamed.transform.parent;
        var temporaryParent = new GameObject("entity-check-temporary-parent").transform;
        renamed.gameObject.name = "renamed-by-entity-check";
        renamed.transform.SetParent(temporaryParent, true);
        Need(scope.Registry.TryResolve("target.office.window", out var afterMove, out var moveError) && afterMove.Equals(handles["target.office.window"]), "Rename/reparent changed identity: " + moveError);
        Need(bridge.TryGetTargetPosition("open_window", out _, out _, out var positionError), "Rename/reparent broke target binding: " + positionError);
        renamed.transform.SetParent(oldParent, true);
        renamed.gameObject.name = oldName;
        UnityEngine.Object.Destroy(temporaryParent.gameObject);
        results.Add("PASS entity ID and target binding survive a runtime rename and reparent");

        yield return VerifyTwoDynamicFireCapabilities(scope);

        yield return RunThreeQuestionFlow(bridge);

        // Start a fresh real scene run and cover both non-success terminal paths before the
        // existing ten-restart stress loop. These go through the same keyboard/entity route.
        bridge.runner.RestartLevel();
        yield return Until(() => UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>() != null
            && UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>() != bridge);
        bridge = UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>();
        yield return Until(() => bridge.IsReady && bridge.runner != null);
        yield return RunWrongAndTimeoutFlow(bridge);
        yield return VerifyVariableLessonSteps();

        for (var restart = 0; restart < 10; restart++)
        {
            var oldScope = bridge.Scope;
            Need(oldScope.Registry.TryResolve("player.main", out var oldHandle, out var oldResolve), "Pre-restart player: " + oldResolve);
            bridge.runner.RestartLevel();
            yield return Until(() => UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>() != null
                && UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>() != bridge);
            bridge = UnityEngine.Object.FindAnyObjectByType<OfficeEntityBindings>();
            yield return Until(() => bridge.IsReady && bridge.runner != null);
            Need(!oldScope.Registry.IsValid(oldHandle), "RestartLevel " + restart + " left the old entity handle valid.");
            Need(bridge.Scope.Registry.TryResolve("player.main", out var replacement, out var restartResolve), "Reloaded player: " + restartResolve);
            Need(!replacement.Equals(oldHandle), "RestartLevel " + restart + " reused a stale player handle.");
            Need(bridge.runner.Score.AskedCount == 0, "RestartLevel " + restart + " did not reset score.");
            Need(bridge.Scope.Registry.FindByTag("office_target").Count == 4, "RestartLevel " + restart + " did not rebuild four office targets.");
        }
        results.Add("PASS ten real RestartLevel scene reloads invalidate old handles, rebuild four targets and reset score");
        bridge.runner.StopLevel();
        yield return null;
        Need(!bridge.IsReady && (bridge.Scope == null || bridge.Scope.IsDisposed), "StopLevel did not release the active entity scope.");
        results.Add("PASS StopLevel releases the active entity scope");
    }

    static GameEntity FindEntity(EntityScope scope, EntityHandle handle)
    {
        Need(scope.Registry.TryGetEntity(handle, out var entity, out var error), "Handle lookup: " + error);
        return entity;
    }

    static IEnumerator RunThreeQuestionFlow(OfficeEntityBindings bridge)
    {
        var flow = bridge.flow;
        var owner = UnityEngine.Object.FindAnyObjectByType<OfficeChoiceInteraction>();
        var runner = bridge.runner;
        Need(flow != null && owner != null && runner != null, "Pilot is missing flow, interaction owner or runner.");
        yield return Until(() => runner.IsRunning && flow.IsQuestionActive);
        for (var number = 0; number < 3; number++)
        {
            var question = flow.CurrentQuestion;
            Need(question != null, "Question " + number + " has no data.");
            var choice = Array.FindIndex(question.options, option => option.id == question.correctOptionId);
            Need(choice >= 0, "Question " + question.id + " has no selectable correct option.");
            var option = question.options[choice];
            flow.presenter.Bubbles[choice].button.onClick.Invoke();
            yield return Until(() => !flow.IsQuestionActive && owner.ArmedOption == option.id);
            var targetId = string.IsNullOrEmpty(option.interactionTargetId) ? option.id : option.interactionTargetId;
            Need(bridge.TryGetTargetPosition(targetId, out var position, out _, out var routeError), "Entity route for " + option.id + ": " + routeError);
            Need(!float.IsNaN(position.x), "Entity route returned an invalid position.");
            var binding = bridge.targets.Single(item => item.targetId == targetId);
            Need(bridge.Scope.Registry.TryResolve(binding.entityId, out var target, out var targetError), "Interaction target resolve: " + targetError);
            yield return SubmitWithKeyboardE(bridge, target, position, option.id);
            yield return Until(() => owner.feedback.IsShowing && owner.Completed);
            Need(runner.Score.AskedCount == number + 1 && runner.Score.Answers[number].correct, "Score did not record correct entity interaction " + option.id);
            yield return Delay(2.1f);
            owner.feedback.continueButton.onClick.Invoke();
            yield return Until(() => owner.instructor.IsShowing);
            yield return Delay(.3f); owner.instructor.Advance();
            yield return Delay(.3f); owner.instructor.Advance();
            yield return Delay(.3f); owner.instructor.Advance();
            yield return Delay(.3f); owner.instructor.Advance();
            yield return Until(() => !owner.instructor.IsShowing);
            if (number < 2) yield return Until(() => flow.IsQuestionActive);
        }
        yield return Until(() => !runner.IsRunning);
        Need(runner.Score.AskedCount == 3 && runner.Score.CorrectCount == 3 && runner.Score.TotalQuestions == 3,
            "Three entity interactions did not reach a correct final settlement.");
        results.Add("PASS three entity-routed bubble selections and interactions record score, complete review, and reach settlement");
    }

    static IEnumerator RunWrongAndTimeoutFlow(OfficeEntityBindings bridge)
    {
        var flow = bridge.flow;
        var owner = UnityEngine.Object.FindAnyObjectByType<OfficeChoiceInteraction>();
        var runner = bridge.runner;
        Need(flow != null && owner != null && owner.timer != null && runner != null, "Wrong/timeout coverage is missing office flow components.");
        yield return Until(() => runner.IsRunning && flow.IsQuestionActive);
        var question = flow.CurrentQuestion;
        var wrongIndex = Array.FindIndex(question.options, option => option.id != question.correctOptionId);
        Need(wrongIndex >= 0, "First question has no incorrect option for negative-path coverage.");
        var wrong = question.options[wrongIndex];
        flow.presenter.Bubbles[wrongIndex].button.onClick.Invoke();
        yield return Until(() => !flow.IsQuestionActive && owner.ArmedOption == wrong.id);
        var targetId = string.IsNullOrEmpty(wrong.interactionTargetId) ? wrong.id : wrong.interactionTargetId;
        Need(bridge.TryGetTargetPosition(targetId, out var position, out _, out var routeError), "Wrong-option route: " + routeError);
        var binding = bridge.targets.Single(item => item.targetId == targetId);
        Need(bridge.Scope.Registry.TryResolve(binding.entityId, out var target, out var targetError), "Wrong-option target: " + targetError);
        yield return SubmitWithKeyboardE(bridge, target, position, wrong.id);
        yield return Until(() => owner.feedback.IsShowing && owner.Completed);
        Need(runner.Score.AskedCount == 1 && runner.Score.CorrectCount == 0 && !runner.Score.Answers[0].correct,
            "Incorrect entity interaction was scored as correct.");
        yield return DismissReview(owner);
        yield return Until(() => flow.IsQuestionActive);

        owner.timer.Begin(.15f);
        yield return Delay(.35f);
        yield return Until(() => owner.feedback.IsShowing && owner.Completed);
        Need(runner.Score.AskedCount == 2 && runner.Score.CorrectCount == 0 && runner.Score.Answers[1].timedOut,
            "Real timer expiry did not create one incorrect timeout record.");
        owner.timer.ExpireIfDue(); owner.timer.ExpireIfDue();
        yield return null;
        Need(runner.Score.AskedCount == 2, "Repeated ExpireIfDue added duplicate timeout records.");
        results.Add("PASS incorrect E interaction records no correct score; real timeout is terminal and exactly once");
        yield return DismissReview(owner);
    }

    static IEnumerator DismissReview(OfficeChoiceInteraction owner)
    {
        yield return Delay(2.1f);
        owner.feedback.continueButton.onClick.Invoke();
        yield return Until(() => owner.instructor.IsShowing);
        while (owner.instructor.IsShowing)
        {
            yield return Delay(.3f);
            owner.instructor.Advance();
        }
    }

    static IEnumerator VerifyVariableLessonSteps()
    {
        var objectUnderTest = new GameObject("integration-variable-instructor");
        var view = objectUnderTest.AddComponent<InstructorLessonView>();
        var closed = 0;
        view.Closed += () => closed++;
        try
        {
            var steps = new[]
            {
                new InstructorLessonStep { kind = "line", text = "one" },
                new InstructorLessonStep { kind = "line", text = "two" },
                new InstructorLessonStep { kind = "video", videoPath = "missing/integration.mp4" },
                new InstructorLessonStep { kind = "line", text = "three" },
                new InstructorLessonStep { kind = "line", text = "four" },
            };
            Need(view.ShowSteps(steps), "Variable instructor steps did not start.");
            var advances = 0;
            while (view.IsShowing && advances++ < 12)
            {
                yield return Delay(.35f);
                view.Advance();
            }
            Need(!view.IsShowing && closed == 1, "Variable instructor sequence did not close exactly once.");
            results.Add("PASS variable instructor steps include more than three lines and a missing video without losing close semantics");
        }
        finally { UnityEngine.Object.Destroy(objectUnderTest); }
    }

    static IEnumerator VerifyTwoDynamicFireCapabilities(EntityScope scope)
    {
        var firstObject = new GameObject("integration-fire-a");
        var secondObject = new GameObject("integration-fire-b");
        EntityHandle firstHandle = default, secondHandle = default;
        try
        {
            var firstController = firstObject.AddComponent<FireEffectController>();
            var secondController = secondObject.AddComponent<FireEffectController>();
            var firstAdapter = firstObject.AddComponent<FireCapabilityAdapter>();
            var secondAdapter = secondObject.AddComponent<FireCapabilityAdapter>();
            firstAdapter.Configure(firstController);
            secondAdapter.Configure(secondController);
            var firstEntity = firstObject.AddComponent<GameEntity>();
            var secondEntity = secondObject.AddComponent<GameEntity>();
            firstEntity.Configure("runtime.integration.fire.a", new[] { "fire", "integration" }, firstObject, new EntityCapability[] { firstAdapter });
            secondEntity.Configure("runtime.integration.fire.b", new[] { "fire", "integration" }, secondObject, new EntityCapability[] { secondAdapter });
            Need(scope.Registry.TryRegister(firstEntity, out firstHandle, out var firstError), "Register dynamic fire A: " + firstError);
            Need(scope.Registry.TryRegister(secondEntity, out secondHandle, out var secondError), "Register dynamic fire B: " + secondError);
            Need(scope.Registry.TryGetCapability<IFireCapability>(firstHandle, out var first, out firstError), "Resolve dynamic fire A: " + firstError);
            Need(scope.Registry.TryGetCapability<IFireCapability>(secondHandle, out var second, out secondError), "Resolve dynamic fire B: " + secondError);
            Need(first.TrySetState(FireState.Small, out var firstSetError), "Set fire A: " + firstSetError);
            Need(second.TrySetState(FireState.Large, out var secondSetError), "Set fire B: " + secondSetError);
            Need(first.CurrentState == FireState.Small && second.CurrentState == FireState.Large,
                "Two dynamic fire adapters did not retain independent state.");
            results.Add("PASS two independently registered fire adapters retain distinct fire states");
        }
        finally
        {
            if (firstHandle.IsValid) scope.Registry.TryUnregister(firstHandle, out _);
            if (secondHandle.IsValid) scope.Registry.TryUnregister(secondHandle, out _);
            UnityEngine.Object.Destroy(firstObject);
            UnityEngine.Object.Destroy(secondObject);
        }
        yield return null;
    }

    static IEnumerator Delay(float seconds)
    {
        var end = EditorApplication.timeSinceStartup + seconds;
        while (EditorApplication.timeSinceStartup < end) yield return null;
    }

    static IEnumerator Until(Func<bool> predicate)
    {
        while (!predicate()) yield return null;
    }

    static IEnumerator SubmitWithKeyboardE(OfficeEntityBindings bridge, EntityHandle target, Vector3 position, string optionId)
    {
        Need(bridge.player != null && bridge.actor != null && bridge.player.PlayerCamera != null, "Player/actor/camera is missing for entity E interaction.");
        var entity = FindEntity(bridge.Scope, target);
        var collider = entity.GetComponent<Collider>();
        Need(collider != null, "Entity " + target.Id + " has no target collider.");
        var controller = bridge.player.GetComponent<CharacterController>();
        Need(controller != null, "Player has no CharacterController for collision-aware approach movement.");
        controller.enabled = false;
        bridge.player.transform.position = new Vector3(position.x, bridge.player.transform.position.y, position.z);
        controller.enabled = true;
        yield return WalkFromRoutePointIntoInteractionRange(bridge.player, controller, collider, optionId, position);
        var direction = collider.bounds.center - bridge.player.PlayerCamera.transform.position;
        bridge.player.transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, 0f);
        bridge.player.SetPitch(-Mathf.Atan2(direction.y, new Vector2(direction.x, direction.z).magnitude) * Mathf.Rad2Deg);
        Physics.SyncTransforms();
        yield return null;
        Physics.SyncTransforms();
        var origin = bridge.player.PlayerCamera.transform.position;
        var directionForward = bridge.player.PlayerCamera.transform.forward;
        var raycast = Physics.Raycast(origin, directionForward, out var hit, 2.8f);
        var expectedChoice = entity.GetComponent<OfficeChoiceTarget>();
        EntityHandle hitEntity = default;
        EntityError hitError = default;
        var mapped = raycast && bridge.Scope.Registry.TryResolveHit(hit.collider, out hitEntity, out hitError);
        var legacyTarget = raycast ? hit.collider.GetComponentInParent<OfficeChoiceTarget>() : null;
        var hitDescription = !raycast
            ? "no hit"
            : "hitCollider=" + hit.collider.name + ", distance=" + hit.distance.ToString("F3")
              + ", legacyTarget=" + (legacyTarget != null ? legacyTarget.optionId : "<none>")
              + ", mapped=" + mapped + ", mappedEntity=" + (mapped ? hitEntity.Id.Value : "<none>")
              + ", mapError=" + (mapped ? "<none>" : hitError.ToString());
        Need(raycast && legacyTarget == expectedChoice && mapped && hitEntity.Equals(target),
            "Entity anchor teleport did not place an unobstructed E ray on " + optionId
            + ". expectedEntity=" + target.Id.Value + ", expectedCollider=" + collider.name
            + ", origin=" + origin + ", forward=" + directionForward + ", endpoint=" + (origin + directionForward * 2.8f)
            + ", targetCenter=" + collider.bounds.center + ", " + hitDescription);
        results.Add("PASS " + optionId + " routePoint=" + position + " interactionStand=" + bridge.player.transform.position
            + " cameraDistance=" + hit.distance.ToString("F3") + " after collision-aware approach");
        yield return Delay(.25f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.E));
        yield return Delay(.25f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
    }

    static IEnumerator WalkFromRoutePointIntoInteractionRange(body player, CharacterController controller, Collider target, string optionId, Vector3 routePoint)
    {
        const float step = .075f;
        const int maxSteps = 96;
        var stalledSteps = 0;
        for (var index = 0; index < maxSteps; index++)
        {
            var cameraPosition = player.PlayerCamera.transform.position;
            var toTarget = target.bounds.center - cameraPosition;
            var horizontal = new Vector3(toTarget.x, 0f, toTarget.z);
            var maxHorizontal = Mathf.Max(.45f, Mathf.Sqrt(Mathf.Max(.01f, 2.55f * 2.55f - toTarget.y * toTarget.y)));
            if (horizontal.magnitude <= maxHorizontal) yield break;
            var before = horizontal.magnitude;
            controller.Move(horizontal.normalized * step);
            yield return null;
            var afterTarget = target.bounds.center - player.PlayerCamera.transform.position;
            var after = new Vector2(afterTarget.x, afterTarget.z).magnitude;
            stalledSteps = after < before - .001f ? 0 : stalledSteps + 1;
            if (stalledSteps >= 4)
                throw new InvalidOperationException("Collision-aware approach stalled for " + optionId
                    + ". routePoint=" + routePoint + ", stand=" + player.transform.position
                    + ", camera=" + player.PlayerCamera.transform.position + ", horizontalDistance=" + after.ToString("F3")
                    + ", targetCenter=" + target.bounds.center + ", steps=" + (index + 1));
        }
        var finalTarget = target.bounds.center - player.PlayerCamera.transform.position;
        throw new InvalidOperationException("Collision-aware approach exceeded " + maxSteps + " steps for " + optionId
            + ". routePoint=" + routePoint + ", stand=" + player.transform.position
            + ", camera=" + player.PlayerCamera.transform.position + ", horizontalDistance="
            + new Vector2(finalTarget.x, finalTarget.z).magnitude.ToString("F3") + ", targetCenter=" + target.bounds.center);
    }

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

    static void Block(string reason)
    {
        File.WriteAllText(Report, "BLOCKED: " + reason);
        if (Application.isBatchMode) EditorApplication.Exit(1);
    }

    static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    static void Finish(string outcome)
    {
        work.Clear();
        File.WriteAllText(Report, outcome + "\n" + string.Join("\n", results));
        CleanupInput();
        if (Application.isBatchMode) EditorApplication.Exit(outcome == "PASS" ? 0 : 1);
        else EditorApplication.isPlaying = false;
    }
}
