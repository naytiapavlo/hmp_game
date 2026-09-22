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
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// Opt-in integration check for the two opaque progress transitions around an office quiz.
/// Touch Library/QuizLoadingChecks.request or use the menu item; no scene is saved.
/// </summary>
[InitializeOnLoad]
public static class QuizLoadingChecks
{
    const string Request = "Library/QuizLoadingChecks.request";
    const string Report = "Library/QuizLoadingChecks.txt";
    const string Session = "QuizLoadingChecks";
    const string MenuScene = "Assets/Scenes/初始界面.unity";

    static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    static readonly List<string> results = new List<string>();
    static double deadline;
    static bool oldBackground;
    static Keyboard keyboard, oldKeyboard;
    static InputSettings settings;
    static InputSettings.BackgroundBehavior oldInputBackground;
    static InputSettings.EditorInputBehaviorInPlayMode oldEditorInput;

    static QuizLoadingChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (!SessionState.GetBool(Session, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                results.Clear();
                deadline = EditorApplication.timeSinceStartup + 150;
                oldBackground = Application.runInBackground;
                Application.runInBackground = true;
                settings = InputSystem.settings;
                oldInputBackground = settings.backgroundBehavior;
                oldEditorInput = settings.editorInputBehaviorInPlayMode;
                settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                oldKeyboard = Keyboard.current;
                keyboard = InputSystem.AddDevice<Keyboard>();
                keyboard.MakeCurrent();
                work.Push(Check());
            }
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                work.Clear();
                Cleanup();
            }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.SetBool(Session, false);
                string scene = SessionState.GetString(Session + "Scene", "");
                if (!string.IsNullOrEmpty(scene)) EditorSceneManager.OpenScene(scene);
            }
        };
    }

    [MenuItem("Tools/Scene Choices/Check Quiz Loading Transitions")]
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
        EditorSceneManager.OpenScene(MenuScene);
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
            Need(EditorApplication.timeSinceStartup < deadline, "Quiz loading integration timed out");
            var current = work.Peek();
            if (!current.MoveNext())
            {
                work.Pop();
                if (work.Count == 0) Finish("PASS");
            }
            else if (current.Current is IEnumerator child) work.Push(child);
        }
        catch (Exception exception)
        {
            results.Add(exception.ToString());
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

    static IEnumerator TapKey(Key key, float releasedDelay = .25f)
    {
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
        yield return Delay(.08f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return Delay(releasedDelay);
    }

    static void ObserveMonotonic(ref float previous, string transition)
    {
        float current = QuizLoadingOverlay.Progress;
        Need(current + .002f >= previous,
            transition + " loading progress regressed from " + previous.ToString("F3") + " to " + current.ToString("F3"));
        previous = Mathf.Max(previous, current);
    }

    static void CheckOverlayStructure()
    {
        var overlay = UnityEngine.Object.FindAnyObjectByType<QuizLoadingOverlay>();
        Need(overlay != null && overlay.gameObject.activeInHierarchy, "Opaque loading overlay exists");
        var canvas = overlay.GetComponent<Canvas>();
        var group = overlay.GetComponent<CanvasGroup>();
        var blocker = overlay.GetComponent<GraphicRaycaster>();
        var backdrop = overlay.transform.Find("Backdrop") as RectTransform;
        Need(canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay && canvas.sortingOrder == 31090,
            "Loading overlay uses the top-level transition canvas");
        Need(group != null && group.alpha > .99f && blocker != null && backdrop != null
            && backdrop.anchorMin == Vector2.zero && backdrop.anchorMax == Vector2.one,
            "Loading transition is opaque, full-screen and input-blocking");
        Need(overlay.transform.Find("Loading Panel/Progress Track/Progress")?.GetComponent<Image>() != null,
            "Loading view contains a real progress fill");
    }

    static IEnumerator SelectCorrectAndInteract(OfficeFireChoiceFlow flow, OfficeChoiceInteraction owner,
        body player, Interactor actor)
    {
        int index = Array.FindIndex(flow.CurrentQuestion.options, o => o.id == flow.CurrentQuestion.correctOptionId);
        Need(index >= 0, "Current question has a configured correct option");
        var option = flow.CurrentQuestion.options[index];
        flow.presenter.Bubbles[index].button.onClick.Invoke();
        yield return Until(() => !flow.IsQuestionActive);
        Need(owner.ArmedOption == option.id && owner.ArmedTargetId == option.interactionTargetId,
            "Correct choice arms its configured physical object");
        var target = owner.targets.Single(t => t.optionId == option.interactionTargetId);
        var controller = player.GetComponent<CharacterController>();
        controller.enabled = false;
        Vector3 destination = flow.destinations[index].approach.position;
        player.transform.position = new Vector3(destination.x, player.transform.position.y, destination.z);
        controller.enabled = true;
        Vector3 direction = target.GetComponent<Collider>().bounds.center - player.PlayerCamera.transform.position;
        player.transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, 0f);
        player.SetPitch(-Mathf.Atan2(direction.y, new Vector2(direction.x, direction.z).magnitude) * Mathf.Rad2Deg);
        Physics.SyncTransforms();
        Need(Physics.Raycast(player.PlayerCamera.transform.position, player.PlayerCamera.transform.forward,
                out var hit, 2.8f) && hit.collider.GetComponentInParent<OfficeChoiceTarget>() == target,
            "Correct object is reachable through the real E interaction ray");
        yield return Delay(.25f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.E));
        yield return Delay(.2f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        Need(owner.feedback.IsShowing && owner.Completed && owner.feedback.LastCorrect,
            "Correct physical interaction opens Correct feedback");
    }

    static IEnumerator Check()
    {
        EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
        yield return Delay(2f);

        var menu = UnityEngine.Object.FindAnyObjectByType<MainMenuView>();
        var select = UnityEngine.Object.FindAnyObjectByType<SceneSelectUI>();
        Need(menu != null && select != null, "Initial scene contains the main menu and scene selector");
        menu.StartTraining();
        Need(select.windowContent.activeInHierarchy, "Main-menu Start Training invokes SceneSelectUI.Open");
        select.SelectOffice();
        Need(select.startButton.interactable, "SceneSelectUI.SelectOffice enables training start");
        select.StartSelectedScene();

        yield return Until(() => UnityEngine.Object.FindAnyObjectByType<CgTransitionPlayer>() != null);
        var cg = UnityEngine.Object.FindAnyObjectByType<CgTransitionPlayer>();
        var cgVideo = cg.GetComponentInChildren<VideoPlayer>(true);
        yield return Until(() => cg != null && cg.IsChapterShowing);
        var chapter = cg.GetComponent<LevelChapterCard>();
        Need(chapter != null && chapter.IsShowing,
            "LevelChapterCard is attached to and shown by the CG transition object");
        Need(cgVideo != null && !cgVideo.isPrepared && !cgVideo.isPlaying && !cg.VideoDone,
            "Level chapter appears before the real CG prepares or plays");
        var chapterLayer = cg.transform.Find("Level Chapter");
        Need(chapterLayer != null && chapterLayer.gameObject.activeInHierarchy,
            "CG transition contains the visible Level Chapter layer");
        var chapterText = chapterLayer.GetComponentsInChildren<TMPro.TMP_Text>(true);
        Need(chapterText.Any(text => text.text.Trim() == "LEVEL 1")
            && chapterText.Any(text => text.text.Trim() == "EARLY-STAGE FIRE"),
            "Level chapter displays separate LEVEL 1 and EARLY-STAGE FIRE labels");
        yield return Delay(.4f);
        ScreenCapture.CaptureScreenshot("Library/LevelChapter.png");
        yield return TapKey(Key.Space, .05f);
        Need(cg.IsChapterShowing && !cg.VideoDone && !cgVideo.isPrepared && !cgVideo.isPlaying,
            "Keyboard input cannot skip the level chapter or leak into the CG transition");
        yield return Until(() => !cg.IsChapterShowing);
        Need(!cg.VideoDone, "Chapter-period keyboard input does not complete the following CG");
        yield return Until(() => cg == null || cg.VideoDone || (cgVideo != null && cgVideo.isPrepared));
        Need(cg != null && cgVideo != null && cgVideo.isPrepared && !cg.VideoDone, "Real CG prepares before keyboard skip");
        yield return Delay(.65f);
        results.Add(cgVideo.frame > 0 ? "INFO CG decoder produced frames before skip" : "INFO CG prepared; transition checked via skip before decoded frames");
        yield return TapKey(Key.Space, .05f);
        yield return Until(() => QuizLoadingOverlay.IsVisible);
        CheckOverlayStructure();
        Need(cg.VideoDone, "Keyboard input skips the real CG after its guard period");

        yield return Until(() => UnityEngine.Object.FindAnyObjectByType<OfficeFireChoiceFlow>() != null);
        var flow = UnityEngine.Object.FindAnyObjectByType<OfficeFireChoiceFlow>();
        var owner = flow.GetComponent<OfficeChoiceInteraction>();
        var runner = UnityEngine.Object.FindAnyObjectByType<LevelFlowRunner>();
        var player = UnityEngine.Object.FindAnyObjectByType<body>();
        var actor = player.GetComponent<Interactor>();
        Need(owner != null && owner.timer != null && runner != null, "Office quiz, timer and level runner are wired");
        int presented = 0;
        flow.QuestionPresented += () => presented++;

        ScreenCapture.CaptureScreenshot("Library/QuizLoading-entry.png");
        float entryProgress = 0f;
        while (!flow.IsQuestionActive)
        {
            Need(QuizLoadingOverlay.IsVisible, "CG-to-question transition never exposes first person view");
            Need(!owner.timer.IsRunning && presented == 0,
                "Entry loading does not start the quiz event or 45-second timer");
            ObserveMonotonic(ref entryProgress, "Entry");
            yield return null;
        }
        Need(presented == 1 && !QuizLoadingOverlay.IsVisible,
            "First QuestionPresented fires only after loading Reveal completes");
        Need(owner.timer.IsRunning && owner.timer.RemainingSeconds > 44f && owner.timer.RemainingSeconds <= 45f,
            "First quiz timer starts at approximately 45 seconds after Reveal");
        Need(flow.presenter.viewController.IsOverview && flow.presenter.Bubbles.Count == 4
            && flow.presenter.Bubbles.All(b => b.TargetVisible), "First overview is ready and interactive");
        results.Add("PASS menu -> guarded Level 1 chapter -> real CG keyboard skip -> opaque monotonic loading -> first overview; timer starts after Reveal");

        yield return SelectCorrectAndInteract(flow, owner, player, actor);
        Need(!owner.timer.IsRunning, "Completed physical answer stops the countdown");
        yield return Delay(2.15f);
        owner.feedback.continueButton.onClick.Invoke();
        yield return Delay(.4f);
        var instructor = owner.instructor;
        Need(instructor.IsShowing && instructor.Step == InstructorLessonView.LessonStep.First,
            "Correct feedback opens instructor line one");
        instructor.Advance();
        yield return Delay(.3f);
        Need(instructor.Step == InstructorLessonView.LessonStep.Second, "Instructor line two shown");
        instructor.Advance();
        yield return Until(() => !string.IsNullOrEmpty(instructor.LastVideoError));
        Need(instructor.Step == InstructorLessonView.LessonStep.Video,
            "Configured science-video slot is reached after line two");
        yield return Delay(.3f); // Video-unavailable also resets the input guard.
        instructor.Advance();
        yield return Delay(.3f);
        Need(instructor.Step == InstructorLessonView.LessonStep.Third, "Instructor line three shown after video slot");

        int beforeNext = presented;
        instructor.Advance();
        Need(instructor.IsClosing && QuizLoadingOverlay.IsVisible,
            "Closing event installs the next-question loading cover before instructor fades");
        Need(!owner.timer.IsRunning && presented == beforeNext,
            "Next-question loading begins with timer and question input still stopped");
        yield return Until(() => !instructor.IsShowing);
        ScreenCapture.CaptureScreenshot("Library/QuizLoading-next.png");
        CheckOverlayStructure();

        float nextProgress = 0f;
        while (presented == beforeNext || !flow.IsQuestionActive)
        {
            Need(QuizLoadingOverlay.IsVisible, "Instructor-to-next-question transition never exposes first person view");
            Need(!owner.timer.IsRunning, "Next-question timer remains stopped behind loading cover");
            ObserveMonotonic(ref nextProgress, "Next question");
            yield return null;
        }
        Need(flow.questionId == "office_fire_no_extinguisher", "Transition reaches the configured second question");
        Need(!QuizLoadingOverlay.IsVisible && owner.timer.IsRunning
            && owner.timer.RemainingSeconds > 44f && owner.timer.RemainingSeconds <= 45f,
            "Second timer starts near 45 seconds only after loading Reveal");
        results.Add("PASS Correct -> three instructor lines/video slot -> monotonic loading -> second overview; no first-person gap");

        runner.PrepareNextQuizTransition();
        Need(QuizLoadingOverlay.IsVisible, "A pending later quiz can install its transition cover");
        runner.StopLevel();
        yield return null;
        Need(!QuizLoadingOverlay.IsVisible && !owner.timer.IsRunning && !flow.IsQuestionActive,
            "StopLevel cancels quiz preparation, timer and loading overlay");

        flow.BeginQuestion();
        Need(QuizLoadingOverlay.IsVisible && !owner.timer.IsRunning,
            "BeginQuestion enters cancellable loading before presentation");
        flow.CancelQuestion();
        yield return null;
        Need(!QuizLoadingOverlay.IsVisible && !owner.timer.IsRunning && !flow.IsQuestionActive,
            "CancelQuestion removes loading and cannot start the timer later");
        results.Add("PASS StopLevel and CancelQuestion clean up loading, preparation and timer state");
    }

    static void Cleanup()
    {
        QuizLoadingOverlay.Hide();
        if (keyboard != null)
        {
            InputSystem.RemoveDevice(keyboard);
            keyboard = null;
            oldKeyboard?.MakeCurrent();
        }
        if (settings != null)
        {
            settings.backgroundBehavior = oldInputBackground;
            settings.editorInputBehaviorInPlayMode = oldEditorInput;
            settings = null;
        }
        Application.runInBackground = oldBackground;
    }

    static void Finish(string outcome)
    {
        work.Clear();
        File.WriteAllText(Report, outcome + "\n" + string.Join("\n", results));
        Cleanup();
        EditorApplication.isPlaying = false;
    }
}
