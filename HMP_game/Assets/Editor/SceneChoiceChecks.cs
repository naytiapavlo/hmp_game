using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HMProtection.Quiz;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class SceneChoiceChecks
{
    [Serializable] sealed class Report { public bool passed; public string error; public List<string> checks = new List<string>(); }
    static SceneChoiceChecks()
    {
        EditorApplication.delayCall += () =>
        {
            if (!File.Exists("Library/SceneChoiceImageChecks.request")) return;
            File.Delete("Library/SceneChoiceImageChecks.request"); Begin();
        };
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool("SceneChoiceImageTest", false)) return;
            SessionState.SetBool("SceneChoiceImageTest", false);
            double ready = EditorApplication.timeSinceStartup + 2;
            void Wait() { if (EditorApplication.timeSinceStartup < ready) return; EditorApplication.update -= Wait; if (EditorApplication.isPlaying) Run(); }
            EditorApplication.update += Wait;
        };
    }
    [MenuItem("Tools/Scene Choices/Run Play Mode Checks")]
    public static void Begin()
    {
        if (EditorApplication.isPlaying) { Run(); return; }
        for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) { Debug.LogError("Save your scenes before running choice checks."); return; }
        SceneChoiceSetup.Install();
        EditorSceneManager.OpenScene("Assets/Scenes/办公室场景.unity");
        SessionState.SetBool("SceneChoiceImageTest", true); EditorApplication.isPlaying = true;
    }
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Run()
    {
        var report = new Report();
        var p = UnityEngine.Object.FindFirstObjectByType<SceneChoicePresenter>();
        GameObject duplicate = null;
        try
        {
            Require(p != null, "Presenter installed");
            p.ExitOverview();
            Require(SceneChoiceCatalog.TryLoad(out var c, out _), "JSON loads");
            Require(!SceneChoiceCatalog.TryParse("{}", out _, out _), "Invalid config fails");
            Require(!SceneChoiceCatalog.TryParse("{\"version\":9,\"questions\":[]}", out _, out _), "Invalid version fails");
            report.checks.Add("Catalog load and invalid configuration rejection");
            var components = p.gameObject.scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Behaviour>(true)).Where(b => b is Camera || b is AudioListener || b is body || b is Interactor).ToDictionary(b => b, b => b.enabled);
            var renderers = p.gameObject.scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Renderer>(true)).ToDictionary(r => r, r => r.enabled);
            var player = UnityEngine.Object.FindFirstObjectByType<body>();
            var pos = player != null ? player.transform.position : Vector3.zero;
            var time = Time.timeScale; var cursor = Cursor.lockState;
            Require(p.ReloadCatalog() && p.EnterOverview() && p.EnterOverview(), "Enter/reenter");
            int calls = 0; SceneChoiceResult result = null;
            Action<SceneChoiceResult> handler = r => { calls++; result = r; }; p.SelectionSubmitted += handler;
            foreach (int n in new[] {2,4,6})
            {
                Require(p.ShowQuestion("office_choose_" + n), p.LastError);
                Require(p.Bubbles.Count == n && p.Bubbles.All(b => b.artwork.artwork == p.bubblePrefab.artwork.artwork), "Shared supplied image prefab");
                Require(p.Bubbles.All(b => b.artwork.GetComponent<CanvasRenderer>() != null), "Artwork renderer present");
                var b = p.Bubbles.First(b => b.TargetVisible); int before = calls;
                b.button.onClick.Invoke(); b.button.onClick.Invoke();
                Require(calls == before + 1 && p.Submitted && result.questionId == "office_choose_" + n && p.Bubbles.All(x => !x.button.interactable), "Submit once and lock");
            }
            report.checks.Add("2/4/6 options use supplied PNG; single submission and lock");
            Require(p.ShowQuestion("office_choose_2"), p.LastError);
            var first = p.Bubbles[0]; var anchor = first.Anchor; var original = anchor.position;
            var tip = first.artwork.tailTip + first.bodyRect.anchoredPosition;
            anchor.position += Vector3.right; p.RefreshLayout();
            Require(Vector2.Distance(tip, first.artwork.tailTip + first.bodyRect.anchoredPosition) > .01f, "Tail follows anchor");
            anchor.position = p.viewController.overviewCamera.transform.position - p.viewController.overviewCamera.transform.forward;
            p.RefreshLayout(); Require(!first.TargetVisible, "Behind camera hides");
            anchor.position = original; p.RefreshLayout(); Require(first.TargetVisible, "Reappears");
            EventSystem.current.SetSelectedGameObject(null); p.RefreshLayout();
            Require(EventSystem.current.currentSelectedGameObject == first.button.gameObject, "Focus recovery");
            ExecuteEvents.Execute(first.button.gameObject, new AxisEventData(EventSystem.current) { moveDir = MoveDirection.Right }, ExecuteEvents.moveHandler);
            Require(EventSystem.current.currentSelectedGameObject == p.Bubbles[1].button.gameObject, "Navigate next");
            ExecuteEvents.Execute(EventSystem.current.currentSelectedGameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Require(p.Submitted, "Navigation submits");
            report.checks.Add("Moving target, visibility, keyboard navigation event and submit");
            string id = anchor.GetComponent<SceneChoiceAnchor>().anchorId;
            anchor.GetComponent<SceneChoiceAnchor>().anchorId = "temporarily_missing";
            Require(!p.ShowQuestion("office_choose_2") && p.Bubbles.Count == 0, "Missing anchor rejects whole question");
            anchor.GetComponent<SceneChoiceAnchor>().anchorId = id;
            duplicate = new GameObject("Transient duplicate"); duplicate.AddComponent<SceneChoiceAnchor>().anchorId = id;
            Require(!p.ShowQuestion("office_choose_2"), "Duplicate rejects");
            UnityEngine.Object.DestroyImmediate(duplicate); duplicate = null;
            Require(!p.ShowQuestion("unknown") && p.Bubbles.Count == 0, "Unknown rejects");
            p.SelectionSubmitted -= handler;
            p.ShowQuestion("office_choose_6"); p.enabled = false;
            Require(!p.viewController.IsOverview && p.Bubbles.Count == 0, "Disable restores");
            foreach (var kv in components) Require(kv.Key.enabled == kv.Value, "Component state restored: " + kv.Key.name);
            foreach (var kv in renderers) Require(kv.Key.enabled == kv.Value, "Renderer state restored: " + kv.Key.name);
            Require(Time.timeScale == time && Cursor.lockState == cursor && (player == null || player.transform.position == pos), "Player/time/cursor preserved");
            p.enabled = true;
            report.checks.Add("Missing/duplicate anchors; cleanup, component-disable, input and player restoration");
            report.passed = true;
        }
        catch (Exception e) { report.error = e.ToString(); Debug.LogException(e); }
        finally
        {
            if (duplicate != null) UnityEngine.Object.DestroyImmediate(duplicate);
            if (p != null) { p.enabled = true; p.ExitOverview(); }
            File.WriteAllText("Library/SceneChoiceImageChecks.json", JsonUtility.ToJson(report, true));
            if (report.passed) { p.EnterOverview(); p.ShowQuestion("office_choose_6"); SceneChoicePreviewWindow.Open(); }
        }
    }
}
