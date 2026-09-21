using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HMProtection.Core;
using HMProtection.Quiz;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>Opt-in checks: no scene edits; all gameplay changes take place in Play mode.</summary>
[InitializeOnLoad]
public static class InstructorLessonChecks
{
    const string Request = "Library/InstructorLessonChecks.request";
    const string Report = "Library/InstructorLessonChecks.txt";
    static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    static readonly List<string> results = new List<string>();
    static double deadline;
    static bool previousBackground;
    static InstructorLessonChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("InstructorChecks", false))
            { results.Clear(); deadline = EditorApplication.timeSinceStartup + 160; previousBackground = Application.runInBackground; Application.runInBackground = true; work.Push(Check()); }
            if (state == PlayModeStateChange.ExitingPlayMode && SessionState.GetBool("InstructorChecks", false))
            { work.Clear(); Application.runInBackground = previousBackground; }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool("InstructorChecks", false))
            {
                SessionState.SetBool("InstructorChecks", false);
                string scene = SessionState.GetString("InstructorChecksScene", "");
                if (!string.IsNullOrEmpty(scene)) EditorSceneManager.OpenScene(scene);
            }
        };
    }
    [MenuItem("Tools/Scene Choices/Check Instructor Lesson")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = SceneManager.GetActiveScene();
        if (scene.isDirty) { File.WriteAllText(Report, "BLOCKED: save the current scene before running checks."); return; }
        SessionState.SetString("InstructorChecksScene", scene.path);
        EditorSceneManager.OpenScene("Assets/Scenes/办公室场景.unity");
        SessionState.SetBool("InstructorChecks", true);
        EditorApplication.isPlaying = true;
    }
    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode && File.Exists(Request))
        {
            File.Delete(Request); SessionState.SetBool("InstructorChecksPending", true); AssetDatabase.Refresh(); return;
        }
        if (!EditorApplication.isPlayingOrWillChangePlaymode && SessionState.GetBool("InstructorChecksPending", false))
        { SessionState.SetBool("InstructorChecksPending", false); Begin(); }
        if (!EditorApplication.isPlaying || work.Count == 0) return;
        try
        {
            Need(EditorApplication.timeSinceStartup < deadline, "Check timeout");
            var current = work.Peek();
            if (!current.MoveNext()) { work.Pop(); if (work.Count == 0) Finish("PASS"); }
            else if (current.Current is IEnumerator child) work.Push(child);
        }
        catch (Exception e) { results.Add(e.ToString()); Finish("FAIL"); }
    }
    static IEnumerator Delay(float seconds)
    { double end = EditorApplication.timeSinceStartup + seconds; while (EditorApplication.timeSinceStartup < end) yield return null; }
    static IEnumerator Until(Func<bool> predicate)
    { while (!predicate()) yield return null; }
    static void Need(bool value, string message) { if (!value) throw new Exception(message); }
    static IEnumerator Check()
    {
        yield return Delay(2);
        var owner = UnityEngine.Object.FindAnyObjectByType<OfficeChoiceInteraction>();
        Need(owner != null && owner.instructor != null, "Runtime instructor wiring");
        var runner = UnityEngine.Object.FindAnyObjectByType<LevelFlowRunner>();
        var flow = owner.flow;
        runner.StopLevel(); flow.CancelQuestion();
        var player = UnityEngine.Object.FindAnyObjectByType<body>();
        var actor = player.GetComponent<Interactor>();
        Need(SceneChoiceCatalog.TryLoad(out var catalog, out var error), error);
        var lesson = catalog.Find(flow.questionId).instructor;
        Need(lesson.correct.first != lesson.incorrect.first, "Distinct configured branches");
        Need(!SceneChoiceCatalog.TryParse(File.ReadAllText(SceneChoiceCatalog.DefaultPath).Replace("\"third\"", "\"missingThird\""), out _, out _), "Reject incomplete dialogue");
        var view = owner.instructor;
        foreach (int index in new[] { 2, 0, 1, 3 })
        {
            flow.BeginQuestion(); yield return Until(() => flow.IsQuestionActive);
            flow.presenter.Bubbles[index].button.onClick.Invoke(); yield return Until(() => !flow.IsQuestionActive);
            owner.Complete(owner.targets[index]); owner.Complete(owner.targets[index]);
            Need(owner.feedback.IsShowing && !view.IsShowing, "Result before instructor");
            yield return Delay(2.2f);
            owner.feedback.continueButton.onClick.Invoke();
            Need(view.IsShowing && view.Step == InstructorLessonView.LessonStep.First && !player.enabled && !actor.enabled, "Continue hands off locked controls");
            Need(view.DialogueText.text == (index == 2 ? lesson.correct.first : lesson.incorrect.first), "Correct branch line 1");
            Need(!view.Player.isPlaying, "No video during line 1");
            yield return Delay(.4f);
            if (index == 2 || index == 0) { ScreenCapture.CaptureScreenshot("Library/Instructor-" + (index == 2 ? "correct" : "incorrect") + ".png"); yield return Delay(.3f); }
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = RectTransformUtility.WorldToScreenPoint(null, view.ContinueButton.transform.TransformPoint(view.ContinueButton.GetComponent<RectTransform>().rect.center)) }, hits);
            Need(hits.Count > 0 && hits[0].gameObject == view.ContinueButton.gameObject, "Instructor button receives clicks");
            view.ContinueButton.onClick.Invoke();
            Need(view.Step == InstructorLessonView.LessonStep.Second && !view.Player.isPlaying, "Line 2 before video");
            yield return Delay(.3f); view.Advance(); yield return Delay(.3f);
            Need(view.Step == InstructorLessonView.LessonStep.Video && !string.IsNullOrEmpty(view.LastVideoError), "Missing media presents recoverable message");
            view.Advance();
            Need(view.Step == InstructorLessonView.LessonStep.Third && view.DialogueText.text == (index == 2 ? lesson.correct.third : lesson.incorrect.third), "Line 3 after video slot");
            yield return Delay(.3f); view.Advance();
            Need(!owner.IsPresentationActive && player.enabled && actor.enabled, "Complete review restores controls");
            results.Add("PASS " + owner.targets[index].optionId + ": feedback -> branch 1 -> 2 -> missing video -> 3 -> restored");
        }
        // Real decoder/loopPointReached path uses the existing clip solely as a test fixture.
        lesson.videoPath = Path.GetFullPath("Assets/CG/起火.mp4");
        EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
        Need(view.Show(lesson, true), "Standalone reusable view");
        Need(!view.Show(lesson, false), "Repeated Show rejected");
        yield return Delay(.3f); view.Advance(); yield return Delay(.3f); view.Advance();
        yield return Until(() => view.Player.isPlaying || !string.IsNullOrEmpty(view.LastVideoError));
        Need(view.Player.isPlaying, "Real MP4 starts after line 2: " + view.LastVideoError
            + " focused=" + Application.isFocused + " scale=" + Time.timeScale + " playerEnabled=" + view.Player.isActiveAndEnabled
            + " step=" + view.Step + " editorPaused=" + EditorApplication.isPaused);
        yield return Delay(1);
        Need(view.Player.texture != null && view.Player.frame > 0, "Decoder produces frames");
        ScreenCapture.CaptureScreenshot("Library/Instructor-video.png");
        yield return Until(() => view.Step == InstructorLessonView.LessonStep.Third || !string.IsNullOrEmpty(view.LastVideoError));
        Need(view.Step == InstructorLessonView.LessonStep.Third && string.IsNullOrEmpty(view.LastVideoError), "Natural video end advances to line 3");
        yield return Delay(.3f); view.Advance();
        results.Add("PASS existing MP4 fixture: prepare -> decoded frames -> natural end -> third line");
        // Real runner must freeze and wait across the hand-off, then continue only after review.
        runner.Begin(runner.Entry, runner.Config);
        yield return Until(() => flow.IsQuestionActive);
        flow.presenter.Bubbles[2].button.onClick.Invoke(); yield return Until(() => runner.CurrentStageId == "free_roam");
        owner.Complete(owner.targets[2]); yield return Delay(2.2f); owner.feedback.Dismiss();
        yield return Delay(.4f);
        Need(runner.IsPaused && runner.IsFrozen && runner.CurrentStageId == "free_roam" && view.IsShowing, "Runner remains frozen during review");
        view.Advance(); yield return Delay(.3f); view.Advance(); yield return Delay(.3f); view.Advance(); yield return Delay(.3f); view.Advance();
        yield return Delay(.3f);
        Need(!runner.IsPaused && runner.CurrentStageId == "result_cutscene", "Runner advances after final line");
        results.Add("PASS runner handoff: free roam stays paused/frozen until final Continue");
        runner.StopLevel();
        // Timeout before choosing is also the incorrect branch; disabling cancels cleanly.
        if (owner.timer == null)
        {
            // The optional timer UI may not yet be installed in the saved scene.
            // Exercise its real timeout event with a Play-only fixture, without saving scene changes.
            var timerObject = new GameObject("Instructor Check Timer"); timerObject.SetActive(false);
            var timer = timerObject.AddComponent<QuizTimerHUD>(); timer.flow = flow;
            owner.enabled = false; owner.timer = timer; owner.enabled = true; timerObject.SetActive(true);
            results.Add("INFO timeout uses a Play-only QuizTimerHUD fixture (saved scene has no timer yet)");
        }
        owner.timer.useQuestionTimeLimit = false;
        owner.timer.durationSeconds = .6f;
        flow.BeginQuestion(); yield return Until(() => owner.feedback.IsShowing);
        Need(!owner.feedback.LastCorrect, "Overview timeout is incorrect");
        owner.feedback.Dismiss(); yield return Delay(.3f);
        Need(view.IsShowing && view.DialogueText.text == lesson.incorrect.first, "Timeout opens incorrect dialogue");
        owner.enabled = false;
        Need(!view.IsShowing && player.enabled && actor.enabled && !owner.IsPresentationActive, "Disable cleans up review");
        results.Add("PASS overview timeout, repeated question reset and component-disable cleanup");
    }
    static void Finish(string outcome)
    {
        work.Clear();
        File.WriteAllText(Report, outcome + "\n" + string.Join("\n", results));
        Application.runInBackground = previousBackground;
        EditorApplication.isPlaying = false;
    }
}
