using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HMProtection.Core;
using HMProtection.Quiz;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

/// <summary>Opt-in checks of the two new lessons and the full three-question sequence.</summary>
[InitializeOnLoad]
public static class OfficeLessonSequenceChecks
{
    const string Request = "Library/OfficeLessonSequenceChecks.request", Report = "Library/OfficeLessonSequenceChecks.txt";
    const string Session = "OfficeLessonSequenceChecks";
    static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    static readonly List<string> results = new List<string>();
    static double deadline;
    static bool oldBackground;
    static Keyboard keyboard, oldKeyboard;
    static InputSettings settings;
    static InputSettings.BackgroundBehavior oldInputBackground;
    static InputSettings.EditorInputBehaviorInPlayMode oldEditorInput;
    static OfficeChoiceInteraction owner;
    static OfficeFireChoiceFlow flow;
    static LevelFlowRunner runner;
    static LevelBootstrapper boot;
    static body player;
    static Interactor actor;

    static OfficeLessonSequenceChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Session, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                results.Clear(); deadline = EditorApplication.timeSinceStartup + 180;
                oldBackground = Application.runInBackground; Application.runInBackground = true;
                settings = InputSystem.settings; oldInputBackground = settings.backgroundBehavior; oldEditorInput = settings.editorInputBehaviorInPlayMode;
                settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                oldKeyboard = Keyboard.current; keyboard = InputSystem.AddDevice<Keyboard>(); keyboard.MakeCurrent();
                work.Push(Check());
            }
            if (state == PlayModeStateChange.ExitingPlayMode) { work.Clear(); Cleanup(); }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.SetBool(Session, false);
                string scene = SessionState.GetString(Session + "Scene", "");
                if (!string.IsNullOrEmpty(scene)) EditorSceneManager.OpenScene(scene);
            }
        };
    }
    [MenuItem("Tools/Scene Choices/Check Three Office Lessons")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = SceneManager.GetActiveScene();
        if (scene.isDirty) { File.WriteAllText(Report, "BLOCKED: save the current scene before checks."); return; }
        SessionState.SetString(Session + "Scene", scene.path);
        EditorSceneManager.OpenScene("Assets/Scenes/办公室场景.unity");
        SessionState.SetBool(Session, true); EditorApplication.isPlaying = true;
    }
    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode && File.Exists(Request))
        { File.Delete(Request); SessionState.SetBool(Session + "Pending", true); AssetDatabase.Refresh(); return; }
        if (!EditorApplication.isPlayingOrWillChangePlaymode && SessionState.GetBool(Session + "Pending", false))
        { SessionState.SetBool(Session + "Pending", false); Begin(); }
        if (!EditorApplication.isPlaying || work.Count == 0) return;
        try
        {
            Need(EditorApplication.timeSinceStartup < deadline, "Check deadline exceeded");
            var current = work.Peek();
            if (!current.MoveNext()) { work.Pop(); if (work.Count == 0) Finish("PASS"); }
            else if (current.Current is IEnumerator child) work.Push(child);
        }
        catch (Exception e) { results.Add(e.ToString()); Finish("FAIL"); }
    }
    static IEnumerator Delay(float seconds)
    { double end = EditorApplication.timeSinceStartup + seconds; while (EditorApplication.timeSinceStartup < end) yield return null; }
    static IEnumerator Until(Func<bool> predicate) { while (!predicate()) yield return null; }
    static void Need(bool value, string message) { if (!value) throw new Exception(message); }

    static IEnumerator SelectAndInteract(int optionIndex)
    {
        var option = flow.CurrentQuestion.options[optionIndex];
        flow.presenter.Bubbles[optionIndex].button.onClick.Invoke();
        yield return Until(() => !flow.IsQuestionActive);
        Need(owner.ArmedOption == option.id && owner.ArmedTargetId == option.interactionTargetId, "Per-question option maps to physical target");
        Need(owner.targets.Count(t => t.GetComponent<Collider>().enabled) == 1, "Only selected target active");
        Need(owner.timer.IsRunning && flow.LastDestination == flow.destinations[optionIndex].approach.position, "Route destination and uninterrupted timer");
        var target = owner.targets.Single(t => t.optionId == option.interactionTargetId);
        Need(target.GetInteractPrompt(actor) == option.interactionPrompt, "Current question E prompt");
        var controller = player.GetComponent<CharacterController>(); controller.enabled = false;
        var pos = flow.destinations[optionIndex].approach.position;
        player.transform.position = new Vector3(pos.x, player.transform.position.y, pos.z); controller.enabled = true;
        var dir = target.GetComponent<Collider>().bounds.center - player.PlayerCamera.transform.position;
        player.transform.rotation = Quaternion.Euler(0, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, 0);
        player.SetPitch(-Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg);
        Physics.SyncTransforms();
        Need(Physics.Raycast(player.PlayerCamera.transform.position, player.PlayerCamera.transform.forward, out var hit, 2.8f)
            && hit.collider.GetComponentInParent<OfficeChoiceTarget>() == target, "Unblocked E ray: " + option.id);
        yield return Delay(.25f);
        Need(!owner.Completed && owner.timer.IsRunning, "Arrival does not complete the answer");
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.E)); yield return Delay(.25f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        Need(owner.feedback.IsShowing && owner.Completed, "Input System E submits " + option.id);
        Need(owner.feedback.LastCorrect == (option.id == flow.CurrentQuestion.correctOptionId), "JSON controls answer outcome");
        Need(!owner.timer.IsRunning && !owner.timer.HasFired && !owner.timer.canvas.gameObject.activeSelf, "Interaction stops timer");
    }
    static IEnumerator Review(bool correct, bool capture = false)
    {
        var lesson = flow.CurrentQuestion.instructor;
        var lines = correct ? lesson.correct : lesson.incorrect;
        var view = owner.instructor;
        yield return Delay(2.1f);
        Need(owner.feedback.continueButton.gameObject.activeInHierarchy, "Feedback animation completed");
        owner.feedback.continueButton.onClick.Invoke(); yield return Delay(.25f);
        Need(view.IsShowing && view.DialogueText.text == lines.first && !player.enabled && !actor.enabled, "Review line 1 and control lock");
        if (capture) { ScreenCapture.CaptureScreenshot("Library/" + flow.questionId + "-review.png"); yield return Delay(.25f); }
        string stage = runner.CurrentStageId;
        if (runner.IsRunning) Need(runner.IsPaused && runner.IsFrozen, "Runner freezes during review");
        view.Advance(); yield return Delay(.25f);
        Need(view.Step == InstructorLessonView.LessonStep.Second && view.DialogueText.text == lines.second && !view.Player.isPlaying, "Line 2 before video");
        view.Advance(); yield return Delay(.35f);
        Need(view.Step == InstructorLessonView.LessonStep.Video && !string.IsNullOrEmpty(view.LastVideoError), "Separate missing-video slot is recoverable");
        view.Advance(); yield return Delay(.25f);
        Need(view.Step == InstructorLessonView.LessonStep.Third && view.DialogueText.text == lines.third, "Line 3 after video slot");
        if (runner.IsRunning) Need(runner.CurrentStageId == stage && runner.IsPaused, "No next question before final continue");
        view.Advance(); yield return Until(() => !view.IsShowing);
        Need(!owner.IsPresentationActive && player.enabled && actor.enabled, "Review restores controls");
    }
    static IEnumerator Check()
    {
        yield return Delay(2);
        owner = UnityEngine.Object.FindAnyObjectByType<OfficeChoiceInteraction>(); flow = owner.flow;
        runner = UnityEngine.Object.FindAnyObjectByType<LevelFlowRunner>(); boot = runner.GetComponent<LevelBootstrapper>();
        player = UnityEngine.Object.FindAnyObjectByType<body>(); actor = player.GetComponent<Interactor>();
        runner.StopLevel(); flow.CancelQuestion();
        Need(SceneChoiceCatalog.TryLoad(out var catalog, out var error), error);
        Need(!SceneChoiceCatalog.TryParse(File.ReadAllText(SceneChoiceCatalog.DefaultPath).Replace("\"correctOptionId\": \"remove_damaged_strip\"", "\"correctOptionId\": \"missing\""), out _, out _), "Invalid answer id rejected");
        var lessonIds = new[] { "office_fire_first_action", "office_fire_no_extinguisher", "office_damaged_strip" };
        foreach (string id in lessonIds.Skip(1))
        {
            for (int index = 0; index < 4; index++)
            {
                boot.TeleportPlayerToSpawn(); runner.ApplyFire(id == lessonIds[2] ? "fire_out" : "fire_initial", "test scenario");
                flow.questionId = id; flow.BeginQuestion(); yield return Until(() => flow.IsQuestionActive);
                Need(flow.CurrentQuestion.id == id && owner.timer.durationSeconds == 45f, "Configured 45s question");
                Need(flow.presenter.GetLayoutWarnings().Count == 0, "Bubble layout: " + string.Join("; ", flow.presenter.GetLayoutWarnings()));
                if (index == 0)
                { yield return Delay(.25f); ScreenCapture.CaptureScreenshot("Library/" + id + "-overview.png"); yield return Delay(.25f); }
                yield return SelectAndInteract(index);
                bool correct = flow.CurrentQuestion.options[index].id == flow.CurrentQuestion.correctOptionId;
                yield return Review(correct, correct);
                results.Add("PASS " + id + " / " + flow.CurrentQuestion.options[index].id + ": E -> " + (correct ? "Correct" : "Incorrect") + " -> 3 lines/video slot -> restored");
            }
        }
        // Real stage sequence: all three complete, and a restart clears previous scores and target bindings.
        var config = JsonUtility.FromJson<LevelConfigDto>(JsonUtility.ToJson(boot.Config));
        foreach (var stage in config.stages)
            if (stage.kind == "cutscene" || stage.kind == "result" || stage.kind == "settlement") stage.duration = .2f;
        runner.Begin(boot.Entry, config);
        for (int i = 0; i < lessonIds.Length; i++)
        {
            int questionIndex = i;
            yield return Until(() => flow.IsQuestionActive && flow.questionId == lessonIds[questionIndex]);
            Need(runner.Score.AskedCount == i && !owner.Completed && owner.timer.IsRunning && !owner.timer.HasFired, "Fresh sequential question");
            if (i == 2) Need(runner.CurrentFireLevel == FireLevel.None, "Post-fire premise has no active fire");
            int choice = Array.FindIndex(flow.CurrentQuestion.options, o => o.id == flow.CurrentQuestion.correctOptionId);
            yield return SelectAndInteract(choice);
            Need(runner.Score.AskedCount == i + 1 && runner.Score.Answers[i].questionId == lessonIds[i] && runner.Score.Answers[i].correct, "One score record per actual question");
            yield return Review(true);
        }
        yield return Until(() => !runner.IsRunning);
        Need(runner.Score.AskedCount == 3 && runner.Score.CorrectCount == 3 && runner.Score.TotalQuestions == 3, "All three questions reach settlement");
        results.Add("PASS real 1 -> 2 -> 3 sequence, JSON target rebinding, score identities, frozen reviews and final settlement");

        // Consecutive timeouts in the added stages must also permit progression without stale correct answers.
        config.stages = config.stages.SkipWhile(s => s.questionId != lessonIds[1]).ToArray();
        owner.timer.useQuestionTimeLimit = false; owner.timer.durationSeconds = .8f;
        runner.Begin(boot.Entry, config);
        yield return Until(() => owner.feedback.IsShowing);
        Need(flow.questionId == lessonIds[1] && !owner.feedback.LastCorrect && runner.Score.Answers[0].timedOut, "Second question overview timeout is incorrect");
        yield return Review(false);
        yield return Until(() => flow.IsQuestionActive && flow.questionId == lessonIds[2]);
        flow.presenter.Bubbles[3].button.onClick.Invoke(); yield return Until(() => owner.feedback.IsShowing);
        Need(!owner.feedback.LastCorrect && runner.Score.Answers[1].timedOut && runner.Score.Answers[1].optionId == "remove_damaged_strip", "Third question walk timeout overrides selected correct option");
        yield return Review(false); yield return Until(() => !runner.IsRunning);
        Need(runner.Score.AskedCount == 2 && runner.Score.CorrectCount == 0, "No stale scores after restart/timeouts");
        results.Add("PASS new-question overview/walk timeouts, incorrect review branches, restart and continued settlement");
    }
    static void Cleanup()
    {
        if (keyboard != null) { InputSystem.RemoveDevice(keyboard); keyboard = null; oldKeyboard?.MakeCurrent(); }
        if (settings != null) { settings.backgroundBehavior = oldInputBackground; settings.editorInputBehaviorInPlayMode = oldEditorInput; settings = null; }
        Application.runInBackground = oldBackground;
    }
    static void Finish(string outcome)
    {
        work.Clear(); File.WriteAllText(Report, outcome + "\n" + string.Join("\n", results));
        Cleanup(); EditorApplication.isPlaying = false;
    }
}
