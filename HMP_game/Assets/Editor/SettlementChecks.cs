using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HMProtection.Core;
using HMProtection.Quiz;
using HMProtection.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Opt-in Play-mode checks for the three-question settlement. The saved scene is never modified.
/// Create Library/SettlementChecks.request or use the Tools menu to run it.
/// </summary>
[InitializeOnLoad]
public static class SettlementChecks
{
    const string Request = "Library/SettlementChecks.request";
    const string Report = "Library/SettlementChecks.txt";
    const string Session = "SettlementChecks";
    const string OfficeScene = "Assets/Scenes/办公室场景.unity";
    const string MenuScene = "Assets/Scenes/初始界面.unity";

    static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    static readonly List<string> results = new List<string>();
    static double deadline;
    static bool oldBackground;

    static SettlementChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Session, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                results.Clear();
                deadline = EditorApplication.timeSinceStartup + 120;
                oldBackground = Application.runInBackground;
                Application.runInBackground = true;
                work.Push(Check());
            }
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                work.Clear();
                Application.runInBackground = oldBackground;
            }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.SetBool(Session, false);
                string scene = SessionState.GetString(Session + "Scene", "");
                if (!string.IsNullOrEmpty(scene)) EditorSceneManager.OpenScene(scene);
            }
        };
    }

    [MenuItem("Tools/Scene Choices/Check Settlement")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = SceneManager.GetActiveScene();
        if (scene.isDirty)
        {
            File.WriteAllText(Report, "BLOCKED: save the current scene before checks.");
            return;
        }
        SessionState.SetString(Session + "Scene", scene.path);
        EditorSceneManager.OpenScene(OfficeScene);
        SessionState.SetBool(Session, true);
        EditorApplication.isPlaying = true;
    }

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
            Need(EditorApplication.timeSinceStartup < deadline, "Settlement check timeout");
            IEnumerator current = work.Peek();
            if (!current.MoveNext())
            {
                work.Pop();
                if (work.Count == 0) Finish("PASS");
            }
            else if (current.Current is IEnumerator child) work.Push(child);
        }
        catch (Exception e)
        {
            results.Add(e.ToString());
            Finish("FAIL");
        }
    }

    static IEnumerator Delay(float seconds)
    {
        double end = EditorApplication.timeSinceStartup + seconds;
        while (EditorApplication.timeSinceStartup < end) yield return null;
    }

    static IEnumerator Until(Func<bool> predicate)
    {
        while (!predicate()) yield return null;
    }

    static void Need(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static ScoreBoard Score(params (bool correct, bool timeout, float seconds)[] answers)
    {
        var score = new ScoreBoard();
        score.Configure(3, 10f, 2);
        for (int i = 0; i < answers.Length; i++)
        {
            var answer = answers[i];
            string correct = "correct_" + i;
            string selected = answer.correct ? correct : "wrong_" + i;
            score.Record("question_" + i, selected, correct, answer.seconds, answer.timeout);
        }
        return score;
    }

    static IEnumerator CheckFixture(TrainingSettlementView view, body player, Interactor actor,
                                    ScoreBoard score, string correct, string time, bool useRetry, string capture = null)
    {
        bool called = false;
        Action callback = () => called = true;
        view.Show(score, useRetry ? callback : (Action)(() => { }), useRetry ? (Action)(() => { }) : callback);
        yield return Delay(.45f);
        Need(view.IsShowing, "Settlement remains visible after its entrance animation");
        Need(view.CorrectText != null && view.CorrectText.text.Contains(correct), "Correct count displays " + correct);
        Need(view.TimeText != null && view.TimeText.text.Contains(time), "Elapsed time displays ceil mm:ss " + time);
        Need(!player.enabled && !actor.enabled, "Settlement disables player movement and interaction");
        Canvas.ForceUpdateCanvases();
        Need(!view.GetComponentsInChildren<TMPro.TMP_Text>(true).Any(text => text.isTextOverflowing),
            "Settlement text fits its designed cards");
        if (!string.IsNullOrEmpty(capture))
        {
            ScreenCapture.CaptureScreenshot(capture);
            yield return Delay(.15f);
        }
        (useRetry ? view.RetryButton : view.MenuButton).onClick.Invoke();
        yield return Until(() => called);
        view.Dismiss();
        yield return Delay(.05f);
        Need(!view.IsShowing && player.enabled && actor.enabled, "Dismiss restores player and interaction components");
    }

    static IEnumerator Check()
    {
        yield return Delay(2f);
        var runner = UnityEngine.Object.FindAnyObjectByType<LevelFlowRunner>();
        var boot = runner != null ? runner.GetComponent<LevelBootstrapper>() : null;
        var flow = UnityEngine.Object.FindAnyObjectByType<OfficeFireChoiceFlow>();
        var player = UnityEngine.Object.FindAnyObjectByType<body>();
        var actor = player != null ? player.GetComponent<Interactor>() : null;
        Need(runner != null && boot != null && flow != null && player != null && actor != null, "Office runtime wiring");

        runner.StopLevel();
        flow.CancelQuestion();
        yield return null;
        var view = runner.GetComponent<TrainingSettlementView>();
        Need(view != null, "LevelFlowRunner automatically owns TrainingSettlementView");

        var zero = Score((false, false, 1.2f), (false, true, 2.1f), (false, false, float.NaN));
        Need(zero.CorrectCount == 0 && Mathf.Approximately(zero.TotalElapsedSeconds, 3.3f),
            "Timeout is incorrect and invalid elapsed values are excluded");
        Need(Resources.Load<Texture2D>("Settlement/FireSafetyBadge") != null, "Settlement fire-safety badge resource");
        yield return CheckFixture(view, player, actor, zero, "0 / 3", "00:04", false, "Library/Settlement-review.png");

        var one = Score((true, false, 60.1f), (false, false, 0f), (false, true, 0f));
        Need(one.CorrectCount == 1 && Mathf.Approximately(one.TotalElapsedSeconds, 60.1f), "One-correct fixture score and elapsed sum");
        yield return CheckFixture(view, player, actor, one, "1 / 3", "01:01", true);

        var three = Score((true, false, 40f), (true, false, 41f), (true, false, 42.01f));
        Need(three.CorrectCount == 3 && Mathf.Approximately(three.TotalElapsedSeconds, 123.01f), "Three-correct fixture score and elapsed sum");
        yield return CheckFixture(view, player, actor, three, "3 / 3", "02:04", true, "Library/Settlement-perfect.png");
        results.Add("PASS 0/1/3 correct, timeout scoring, valid answer-time sum and ceil mm:ss display");

        // Enter a real settlement stage, then swap the shared config back to its first quiz before Retry.
        // This keeps the check short while proving that the production callback calls Begin(Entry, Config).
        LevelConfigDto config = JsonUtility.FromJson<LevelConfigDto>(JsonUtility.ToJson(boot.Config));
        StageDto firstQuiz = config.stages.First(s => string.Equals(s.kind, "quiz", StringComparison.OrdinalIgnoreCase));
        StageDto settlement = config.stages.First(s => string.Equals(s.kind, "settlement", StringComparison.OrdinalIgnoreCase));
        config.stages = new[] { settlement };
        runner.Begin(boot.Entry, config);
        runner.Score.Record("q1", "right", "right", 5f, false);
        runner.Score.Record("q2", "wrong", "right", 7f, false);
        runner.Score.Record("q3", "", "right", 9f, true);
        yield return Until(() => view.IsShowing && !runner.IsRunning && !QuizLoadingOverlay.IsVisible);
        Need(view.CorrectText.text.Contains("1 / 3") && view.TimeText.text.Contains("00:21"),
            "Real settlement receives the completed runner score");
        Need(!player.enabled && !actor.enabled, "Real settlement holds controls after the runner finishes");
        config.stages = new[] { firstQuiz };
        yield return Delay(.4f);
        view.RetryButton.onClick.Invoke();
        yield return Until(() => flow.IsQuestionActive);
        Need(!view.IsShowing && runner.IsRunning && runner.Score.AskedCount == 0,
            "Retry closes settlement, clears old answers and starts a fresh run");
        Need(flow.questionId == firstQuiz.questionId, "Retry returns to the first configured question");
        results.Add("PASS real settlement remains after loading reveal; Retry clears score and returns to question 1");

        runner.StopLevel();
        flow.CancelQuestion();
        yield return null;
        config.stages = new[] { settlement };
        runner.Begin(boot.Entry, config);
        yield return Until(() => view.IsShowing && !runner.IsRunning && !QuizLoadingOverlay.IsVisible);
        yield return Delay(.4f);
        view.MenuButton.onClick.Invoke();
        yield return Until(() => SceneManager.GetActiveScene().path == MenuScene);
        Need(SceneManager.GetActiveScene().path == MenuScene, "Menu button exits to the initial scene");
        results.Add("PASS Menu closes training and loads the initial scene");
    }

    static void Finish(string outcome)
    {
        work.Clear();
        File.WriteAllText(Report, outcome + "\n" + string.Join("\n", results));
        Application.runInBackground = oldBackground;
        EditorApplication.isPlaying = false;
    }
}
