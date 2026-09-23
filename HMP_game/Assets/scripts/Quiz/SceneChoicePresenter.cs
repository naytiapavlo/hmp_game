using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HMProtection.Quiz
{
    public sealed class SceneChoicePresenter : MonoBehaviour
    {
        [Serializable] public sealed class ResultEvent : UnityEvent<SceneChoiceResult> { }
        public QuizViewController viewController;
        public HMProtection.EntityAdapters.LevelSceneBindings entityBindings;
        public TextAsset catalogAsset;
        public SceneChoiceBubble bubblePrefab;
        public Canvas canvas;
        public RectTransform bubbleContainer;
        public TextMeshProUGUI promptLabel;
        public ResultEvent onSelectionSubmitted = new ResultEvent();
        public UnityEvent<string> onDisplayFailed = new UnityEvent<string>();
        public event Action<SceneChoiceResult> SelectionSubmitted;
        public event Action<string> DisplayFailed;
        public string LastError { get; private set; }
        public string CurrentQuestionId { get; private set; }
        public bool Submitted { get; private set; }
        public IReadOnlyList<SceneChoiceBubble> Bubbles => active;
        readonly List<SceneChoiceBubble> active = new List<SceneChoiceBubble>();
        readonly Stack<SceneChoiceBubble> pool = new Stack<SceneChoiceBubble>();
        SceneChoiceCatalog catalog;
        bool subscribed;
        float promptReserve = 90f;
        void OnEnable() => Subscribe();
        void Subscribe() { if (!subscribed && viewController != null) { viewController.OverviewExited += HideQuestion; subscribed = true; } }
        public bool ReloadCatalog()
        {
            HideQuestion();
            string error;
            bool loaded = catalogAsset != null
                ? SceneChoiceCatalog.TryParse(catalogAsset.text, out catalog, out error)
                : SceneChoiceCatalog.TryLoad(out catalog, out error);
            if (!loaded) return Fail(error);
            LastError = null; return true;
        }
        public bool TryGetCatalog(out SceneChoiceCatalog value, out string error)
        {
            if (catalogAsset != null) return SceneChoiceCatalog.TryParse(catalogAsset.text, out value, out error);
            return SceneChoiceCatalog.TryLoad(out value, out error);
        }
        public bool EnterOverview() { Subscribe(); return viewController != null && viewController.EnterOverview() || Fail("Overview camera is unavailable."); }
        public void ExitOverview() { HideQuestion(); if (viewController != null) viewController.ExitOverview(); }
        public bool ShowQuestion(string questionId)
        {
            HideQuestion(); LastError = null;
            if (catalog == null && !ReloadCatalog()) return false;
            if (bubblePrefab == null || canvas == null || bubbleContainer == null || promptLabel == null || viewController == null) return Fail("Incomplete choice UI references.");
            var q = catalog.Find(questionId);
            if (q == null) return Fail("Question not found: " + questionId);
            var anchors = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (entityBindings == null)
            foreach (var root in gameObject.scene.GetRootGameObjects()) foreach (var a in root.GetComponentsInChildren<SceneChoiceAnchor>(true))
            {
                if (string.IsNullOrWhiteSpace(a.anchorId) || !anchors.TryAdd(a.anchorId, a.transform)) return Fail("Empty or duplicate anchor ID: " + a.anchorId);
            }
            foreach (var o in q.options)
            {
                if (o.target.mode != "anchor") continue;
                if (entityBindings != null)
                {
                    if (!entityBindings.TryGetAnchorPosition(o.target.anchorId, out _, out var error)) return Fail(error);
                }
                else if (!anchors.ContainsKey(o.target.anchorId)) return Fail(q.id + ": missing anchor " + o.target.anchorId);
            }
            if (!viewController.IsOverview) return Fail("Call EnterOverview before ShowQuestion.");
            CurrentQuestionId = q.id; canvas.gameObject.SetActive(true); promptLabel.text = q.prompt ?? "";
            Canvas.ForceUpdateCanvases();
            float promptHeight = Mathf.Max(68f, promptLabel.GetPreferredValues(promptLabel.text, promptLabel.rectTransform.rect.width, Mathf.Infinity).y);
            promptLabel.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, promptHeight);
            promptReserve = string.IsNullOrEmpty(q.prompt) ? 0f : promptHeight + 34f;
            for (int i = 0; i < q.options.Length; i++)
            {
                var o = q.options[i]; var b = pool.Count > 0 ? pool.Pop() : Instantiate(bubblePrefab, bubbleContainer);
                if (entityBindings != null && o.target.mode == "anchor")
                    b.BindEntity(o, () => entityBindings.TryGetAnchorPosition(o.target.anchorId, out var position, out _) ? position : (Vector3?)null,
                        i, Submit, q.numberedOptions);
                else b.Bind(o, o.target.mode == "anchor" ? anchors[o.target.anchorId] : null, i, Submit, q.numberedOptions);
                active.Add(b);
            }
            Canvas.ForceUpdateCanvases(); RefreshLayout(); Focus(); return true;
        }
        void Submit(SceneChoiceBubble b)
        {
            if (Submitted || !active.Contains(b) || !b.TargetVisible || CurrentQuestionId == null) return;
            Submitted = true;
            var result = new SceneChoiceResult { questionId = CurrentQuestionId, optionId = b.Option.id, targetMode = b.Option.target.mode, anchorId = b.Option.target.anchorId, worldPosition = b.WorldPosition };
            foreach (var other in active) other.Lock(other == b);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            SelectionSubmitted?.Invoke(result); onSelectionSubmitted.Invoke(result);
        }
        public void HideQuestion()
        {
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null && canvas != null && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(canvas.transform)) EventSystem.current.SetSelectedGameObject(null);
            foreach (var b in active) if (b != null) { b.Release(); pool.Push(b); }
            active.Clear(); CurrentQuestionId = null; Submitted = false;
            if (canvas != null) canvas.gameObject.SetActive(false);
        }
        public void RefreshLayout()
        {
            if (viewController == null || !viewController.IsOverview || CurrentQuestionId == null) return;
            bool changed = false;
            foreach (var b in active) { bool before = b.TargetVisible; b.Place(viewController.overviewCamera, (RectTransform)canvas.transform, promptReserve); changed |= before != b.TargetVisible; }
            if (changed || !Submitted && EventSystem.current != null && EventSystem.current.currentSelectedGameObject == null) Focus();
        }
        void Focus()
        {
            var visible = active.FindAll(b => b.TargetVisible);
            for (int i = 0; i < visible.Count; i++)
            {
                var prev = visible[(i + visible.Count - 1) % visible.Count].button; var next = visible[(i + 1) % visible.Count].button;
                visible[i].button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.Explicit, selectOnUp = prev, selectOnLeft = prev, selectOnDown = next, selectOnRight = next };
            }
            if (!Submitted && visible.Count > 0 && EventSystem.current != null)
            {
                var selected = EventSystem.current.currentSelectedGameObject;
                if (selected == null || !selected.activeInHierarchy) EventSystem.current.SetSelectedGameObject(visible[0].button.gameObject);
            }
        }
        public List<string> GetLayoutWarnings()
        {
            var warnings = new List<string>();
            for (int i = 0; i < active.Count; i++)
            {
                var b = active[i];
                if (!b.TargetVisible) { warnings.Add(b.Option.id + ": target is missing or outside the camera."); continue; }
                if (b.bodyRect.rect.height > ((RectTransform)canvas.transform).rect.height - 140) warnings.Add(b.Option.id + ": text exceeds safe area.");
                for (int j = 0; j < i; j++) if (active[j].TargetVisible && b.LayoutRect.Overlaps(active[j].LayoutRect)) warnings.Add(b.Option.id + " overlaps " + active[j].Option.id + "; adjust bubbleOffset.");
            }
            return warnings;
        }
        bool Fail(string error) { LastError = error; DisplayFailed?.Invoke(error); onDisplayFailed.Invoke(error); return false; }
        void LateUpdate() => RefreshLayout();
        void OnDisable() { ExitOverview(); if (subscribed && viewController != null) viewController.OverviewExited -= HideQuestion; subscribed = false; }
    }
}
