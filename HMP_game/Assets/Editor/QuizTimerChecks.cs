using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HMProtection.Quiz;
using HMProtection.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Opt-in integration checks. Short deadlines and moved player poses exist only in Play mode.
[InitializeOnLoad]
public static class QuizTimerChecks
{
    const string Request = "Library/QuizTimerChecks.request", Report = "Library/QuizTimerChecks.txt";
    static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    static readonly List<string> results = new List<string>();
    static double deadline;
    static bool previousBackground;
    static QuizTimerChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("QuizTimerChecks", false))
            { results.Clear(); deadline = EditorApplication.timeSinceStartup + 100; previousBackground = Application.runInBackground; Application.runInBackground = true; work.Push(Check()); }
            if (state == PlayModeStateChange.ExitingPlayMode && SessionState.GetBool("QuizTimerChecks", false))
            { work.Clear(); Application.runInBackground = previousBackground; }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool("QuizTimerChecks", false))
            {
                SessionState.SetBool("QuizTimerChecks", false);
                string scene = SessionState.GetString("QuizTimerChecksScene", "");
                if (!string.IsNullOrEmpty(scene)) EditorSceneManager.OpenScene(scene);
            }
        };
    }
    [MenuItem("Tools/Scene Choices/Check Quiz Timer")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = SceneManager.GetActiveScene();
        if (scene.isDirty) { File.WriteAllText(Report, "BLOCKED: save current scene before running checks."); return; }
        SessionState.SetString("QuizTimerChecksScene", scene.path);
        EditorSceneManager.OpenScene("Assets/Scenes/办公室场景.unity");
        QuizTimerSetup.Install();
        SessionState.SetBool("QuizTimerChecks", true);
        EditorApplication.isPlaying = true;
    }
    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode && File.Exists(Request))
        { File.Delete(Request); SessionState.SetBool("QuizTimerChecksPending", true); AssetDatabase.Refresh(); return; }
        if (!EditorApplication.isPlayingOrWillChangePlaymode && SessionState.GetBool("QuizTimerChecksPending", false))
        { SessionState.SetBool("QuizTimerChecksPending", false); Begin(); }
        if (!EditorApplication.isPlaying || work.Count == 0) return;
        try
        {
            Need(EditorApplication.timeSinceStartup < deadline, "Check timed out");
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
    static IEnumerator CloseReview(OfficeChoiceInteraction owner)
    { owner.feedback.Dismiss(); yield return Delay(.2f); Need(owner.instructor.IsShowing, "Instructor handoff"); owner.instructor.Dismiss(); }
    static IEnumerator Check()
    {
        yield return Delay(2);
        var owner = UnityEngine.Object.FindAnyObjectByType<OfficeChoiceInteraction>();
        var hud = UnityEngine.Object.FindAnyObjectByType<QuizTimerHUD>();
        var runner = UnityEngine.Object.FindAnyObjectByType<LevelFlowRunner>();
        Need(owner != null && hud != null && owner.timer == hud && hud.flow == owner.flow, "Saved timer wiring");
        var flow = owner.flow;
        runner.StopLevel(); flow.CancelQuestion();
        Need(!hud.IsRunning && !hud.canvas.gameObject.activeSelf, "Idle HUD hidden");
        Need(SceneChoiceCatalog.TryLoad(out var catalog, out var error), error);
        Need(catalog.Find(flow.questionId).timeLimitSeconds == 45f, "JSON default 45s");
        Need(!SceneChoiceCatalog.TryParse(File.ReadAllText(SceneChoiceCatalog.DefaultPath).Replace("\"timeLimitSeconds\": 45", "\"timeLimitSeconds\": -1"), out _, out _), "Negative deadline rejected");
        hud.durationSeconds = 9f;
        flow.BeginQuestion(); yield return Until(() => flow.IsQuestionActive);
        Need(hud.IsRunning && hud.durationSeconds == 45f && hud.countdownLabel.text == "0:45", "JSON wins and timer starts on presentation");
        flow.CancelQuestion();
        Need(!hud.IsRunning && !hud.canvas.gameObject.activeSelf && !hud.HasFired, "Cancellation hides HUD without result");
        results.Add("PASS JSON deadline, initial countdown label and cancellation");

        int expiryCount = 0;
        hud.TimedOut += () => expiryCount++;
        hud.useQuestionTimeLimit = false; hud.durationSeconds = 8f;
        var config = JsonUtility.FromJson<LevelConfigDto>(JsonUtility.ToJson(runner.Config));
        config.stages[0].duration = .1f;
        config.stages[1].duration = .1f;
        config.stages[2].duration = .1f; // Legacy stage deadlines must not interrupt this timer.
        runner.Begin(runner.Entry, config);
        yield return Until(() => flow.IsQuestionActive);
        Need(hud.canvas.gameObject.activeInHierarchy && !hud.milestoneFlames[0].gameObject.activeSelf, "Fresh milestones");
        yield return Until(() => hud.Progress01 > .38f);
        Need(hud.milestoneFlames[0].gameObject.activeSelf && !hud.milestoneFlames[1].gameObject.activeSelf, "One extra flame after 1/3");
        yield return Until(() => hud.Progress01 > .72f);
        Need(hud.milestoneFlames[1].gameObject.activeSelf && !hud.milestoneFlames[2].gameObject.activeSelf, "Two extra flames after 2/3");
        Need(Mathf.Abs(hud.fill.rectTransform.anchorMax.x - hud.Progress01) < .01f, "Fill advances");
        Need(Mathf.Abs(hud.leadingFlame.anchoredPosition.x - hud.fillArea.rect.width * hud.Progress01) < 1f, "Flame at red edge");
        float flameOffset = hud.leadingFlame.anchoredPosition.x - hud.milestoneFlames[0].rectTransform.anchoredPosition.x;
        Need(flameOffset > 0f && flameOffset < hud.milestoneFlames[0].rectTransform.rect.width,
            "Added flame overlaps original flame");
        Need(hud.leadingFlame.GetSiblingIndex() > hud.milestoneFlames[0].transform.GetSiblingIndex(), "Original flame draws in front");
        ScreenCapture.CaptureScreenshot("Library/QuizTimer-overview.png"); yield return Delay(.2f);
        flow.presenter.Bubbles[2].button.onClick.Invoke();
        yield return Until(() => runner.CurrentStageId == "free_roam");
        Need(hud.IsRunning && hud.canvas.gameObject.activeInHierarchy && runner.Score.AskedCount == 0, "Selection neither stops timer nor awards points");
        Need(Mathf.Abs(hud.leadingFlame.anchoredPosition.x - hud.milestoneFlames[0].rectTransform.anchoredPosition.x - flameOffset) < 1f,
            "Added flame moves with original flame");
        ScreenCapture.CaptureScreenshot("Library/QuizTimer-walk.png"); yield return Delay(.2f);
        yield return Until(() => owner.feedback.IsShowing);
        Need(owner.Completed && !owner.feedback.LastCorrect && owner.feedback.title.text == "Incorrect", "Walking timeout overrides correct selection");
        Need(!hud.IsRunning && hud.HasFired && !hud.canvas.gameObject.activeSelf && expiryCount == 1, "Single expiry and hidden HUD");
        Need(runner.Score.AskedCount == 1 && runner.Score.Answers[0].timedOut && runner.Score.LastAnswerCorrect == false
            && Mathf.Abs(runner.Score.Answers[0].elapsedSeconds - 8f) < .1f, "Timeout score includes walking time");
        hud.ExpireIfDue(); Need(expiryCount == 1, "No duplicate timeout");
        owner.feedback.Dismiss(); yield return Delay(.2f);
        Need(owner.instructor.IsShowing && runner.IsPaused && runner.IsFrozen, "Frozen instructor review");
        owner.instructor.Dismiss(); yield return Until(() => runner.CurrentStageId == "result_cutscene");
        Need(runner.CurrentFireLevel == FireLevel.Large, "Wrong result fire branch"); runner.StopLevel();
        results.Add("PASS overview/walk progress, 1/3 and 2/3 flames, single timeout, incorrect score/result, instructor handoff");

        hud.durationSeconds = .8f;
        flow.BeginQuestion(); yield return Until(() => owner.feedback.IsShowing);
        Need(!flow.IsQuestionActive && !owner.feedback.LastCorrect && expiryCount == 2, "Overview timeout restores camera and shows Incorrect");
        yield return CloseReview(owner);
        results.Add("PASS timeout before choosing");

        hud.durationSeconds = 4f;
        runner.Begin(runner.Entry, config); yield return Until(() => flow.IsQuestionActive);
        flow.presenter.Bubbles[2].button.onClick.Invoke(); yield return Until(() => runner.CurrentStageId == "free_roam");
        var player = UnityEngine.Object.FindAnyObjectByType<body>();
        var actor = player.GetComponent<Interactor>();
        var target = owner.targets[2];
        var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
        var pos = flow.destinations[2].approach.position;
        player.transform.position = new Vector3(pos.x, player.transform.position.y, pos.z); cc.enabled = true;
        var dir = target.GetComponent<Collider>().bounds.center - player.PlayerCamera.transform.position;
        player.transform.rotation = Quaternion.Euler(0, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, 0);
        player.SetPitch(-Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg);
        Physics.SyncTransforms();
        Need(Physics.Raycast(player.PlayerCamera.transform.position, player.PlayerCamera.transform.forward, out var hit, 2.8f)
            && hit.collider.GetComponentInParent<OfficeChoiceTarget>() == target, "Eye ray reaches selected door");
        target.Interact(actor);
        Need(owner.feedback.IsShowing && owner.feedback.LastCorrect && !hud.IsRunning && !hud.HasFired, "Interaction stops timer and shows Correct");
        Need(runner.Score.AskedCount == 1 && runner.Score.LastAnswerCorrect == true && !runner.Score.Answers[0].timedOut, "Correct interaction awards points");
        yield return Delay(4.2f);
        Need(expiryCount == 2 && owner.feedback.LastCorrect && !hud.canvas.gameObject.activeSelf, "No late timeout");
        yield return CloseReview(owner); runner.StopLevel();
        results.Add("PASS reachable door interaction, correct score, no late timeout");

        hud.durationSeconds = 30f; flow.BeginQuestion(); yield return Until(() => flow.IsQuestionActive);
        flow.presenter.Bubbles[2].button.onClick.Invoke(); yield return Until(() => !flow.IsQuestionActive);
        hud.Begin(.1f);
        typeof(QuizTimerHUD).GetField("startedAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(hud, Time.realtimeSinceStartupAsDouble - 1.0);
        owner.Complete(target);
        Need(owner.feedback.IsShowing && !owner.feedback.LastCorrect && hud.HasFired && expiryCount == 3, "Late interaction cannot beat Update");
        yield return CloseReview(owner);
        hud.durationSeconds = 30f; flow.BeginQuestion(); yield return Until(() => flow.IsQuestionActive);
        owner.enabled = false;
        Need(!hud.IsRunning && !hud.canvas.gameObject.activeSelf, "Disable hides HUD"); flow.CancelQuestion();
        results.Add("PASS deadline/input boundary and disable cleanup");
        // Reuse the existing Input System E/Enter suite against the now-installed timer.
        owner.enabled = true; hud.useQuestionTimeLimit = true; hud.durationSeconds = 45f;
        string inputReport = "Library/OfficeChoiceActionChecks.txt";
        DateTime previousReport = File.Exists(inputReport) ? File.GetLastWriteTimeUtc(inputReport) : DateTime.MinValue;
        OfficeChoiceActionChecks.Begin();
        yield return Until(() => File.Exists(inputReport) && File.GetLastWriteTimeUtc(inputReport) != previousReport);
        Need(File.ReadAllText(inputReport).StartsWith("PASS"), "Input regression: " + File.ReadAllText(inputReport));
        results.Add("PASS existing four-option Input System E/Enter and runner regression with timer installed");
    }
    static void Finish(string result)
    {
        work.Clear(); File.WriteAllText(Report, result + "\n" + string.Join("\n", results));
        Application.runInBackground = previousBackground; EditorApplication.isPlaying = false;
    }
}
