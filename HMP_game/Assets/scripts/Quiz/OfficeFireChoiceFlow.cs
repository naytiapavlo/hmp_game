using System;
using System.Collections;
using System.Linq;
using HMProtection.UI;
using UnityEngine;

namespace HMProtection.Quiz
{
    /// <summary>Office entry question, then restore the player and delegate navigation to GuidanceSystem.</summary>
    public sealed class OfficeFireChoiceFlow : MonoBehaviour
    {
        [Serializable] public sealed class Destination
        {
            public string optionId, label, targetId;
            public Transform approach;
        }
        public SceneChoicePresenter presenter;
        public GuidanceSystem guidance;
        public string questionId = "office_fire_first_action";
        public Destination[] destinations;
        [Tooltip("交给 LevelFlowRunner 排程：置 true 后本组件不再于 Start() 自启出题，" +
                 "改由关卡状态机在「失火原因动画」结束后调用 BeginQuestion()。" +
                 "默认 false ＝ 完全保持原有行为（在主菜单流程里选完场景后立即出题）。")]
        public bool runnerDriven;
        public bool IsQuestionActive { get; private set; }
        public string LastSelectedOption { get; private set; }
        public string LastError { get; private set; }
        public Vector3 LastDestination { get; private set; }
        public SceneChoiceQuestion CurrentQuestion { get; private set; }
        public event Action<Destination> RouteStarted;
        /// <summary>俯视题目界面真正可交互（计时起点）。</summary>
        public event Action QuestionPresented;
        /// <summary>题目在作答完成前被取消（跳过/关卡停止），限时流程应一并收尾。</summary>
        public event Action QuestionCancelled;
        static bool pendingEntry;
        bool starting, submitting;
        Coroutine routine;
        CanvasGroup inputGate;
        Destination[] sceneDestinations;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetRequest() => pendingEntry = false;
        public static void RequestOnNextOfficeLoad() => pendingEntry = true;
        public static void CancelPendingEntry() => pendingEntry = false;
        /// <summary>本局是否带有「从主菜单进入办公室」的入场请求（供关卡状态机判断入口来源）。</summary>
        public static bool EntryPending => pendingEntry;
        /// <summary>读取并清掉入场请求；返回读取到的值。</summary>
        public static bool ConsumeEntryRequest() { bool v = pendingEntry; pendingEntry = false; return v; }
        void OnEnable() { if (presenter != null) presenter.SelectionSubmitted += Selected; }
        IEnumerator Start()
        {
            if (!pendingEntry) yield break;
            // runnerDriven：把出题时机交给 LevelFlowRunner（失火原因动画之后再出题）。
            // 不在这里清 pendingEntry——留给 ConsumeEntryRequest()，避免状态机读不到入口来源。
            if (runnerDriven) yield break;
            pendingEntry = false;
            // Allow player/UI Awake and the first Update to establish their cursor state.
            yield return null;
            BeginQuestion();
        }
        public void BeginQuestion()
        {
            if (starting || IsQuestionActive || submitting) return;
            LastError = null;
            if (!ConfigureQuestion()) return;
            if (presenter == null || guidance == null || destinations == null || destinations.Length != 4 ||
                destinations.Any(d => d == null || d.approach == null || string.IsNullOrWhiteSpace(d.optionId)) ||
                destinations.Select(d => d.optionId).Distinct().Count() != 4)
            { Fail("Office question is missing its presenter, guidance system or four destinations."); return; }
            starting = true; routine = StartCoroutine(ShowAfterCg());
        }
        bool ConfigureQuestion()
        {
            if (!SceneChoiceCatalog.TryLoad(out var catalog, out var error)) { Fail(error); return false; }
            var question = catalog.Find(questionId);
            if (question == null) { Fail("Question not found: " + questionId); return false; }
            if (sceneDestinations == null) sceneDestinations = destinations;
            if (sceneDestinations == null) { Fail("No scene destination bindings."); return false; }
            var configured = new Destination[question.options.Length];
            for (int i = 0; i < configured.Length; i++)
            {
                var option = question.options[i];
                string targetId = string.IsNullOrEmpty(option.interactionTargetId) ? option.id : option.interactionTargetId;
                var binding = sceneDestinations.FirstOrDefault(d => d != null && d.optionId == targetId);
                if (binding == null) { Fail(questionId + ": missing physical target " + targetId); return false; }
                configured[i] = new Destination { optionId = option.id, targetId = targetId, approach = binding.approach,
                    label = string.IsNullOrEmpty(option.routeLabel) ? binding.label : option.routeLabel };
            }
            CurrentQuestion = question; destinations = configured;
            return true;
        }
        IEnumerator ShowAfterCg()
        {
            guidance.HideRoute();
            if (!presenter.ReloadCatalog() || !presenter.EnterOverview() || !presenter.ShowQuestion(questionId))
            { Fail(presenter.LastError); starting = false; presenter.ExitOverview(); yield break; }
            inputGate = presenter.canvas.GetComponent<CanvasGroup>();
            if (inputGate == null) inputGate = presenter.canvas.gameObject.AddComponent<CanvasGroup>();
            SetInput(false);
            // Prepare the overview under the CG blackout. Do not allow the skip click to answer.
            while (FindAnyObjectByType<CgTransitionPlayer>() != null) yield return null;
            yield return null;
            yield return new WaitForSecondsRealtime(.2f);
            IsQuestionActive = true; starting = false; submitting = false; LastSelectedOption = null;
            SetInput(true); routine = null;
            QuestionPresented?.Invoke();
        }
        void Selected(SceneChoiceResult result)
        {
            if (!IsQuestionActive || submitting || result.questionId != questionId) return;
            var destination = destinations.FirstOrDefault(d => d.optionId == result.optionId);
            if (destination == null || destination.approach == null) { Fail("Selected destination is missing."); presenter.ExitOverview(); IsQuestionActive = false; return; }
            submitting = true; SetInput(false);
            LastSelectedOption = result.optionId; LastDestination = destination.approach.position;
            routine = StartCoroutine(Guide(destination));
        }
        IEnumerator Guide(Destination destination)
        {
            yield return new WaitForSecondsRealtime(.2f);
            presenter.ExitOverview(); SetInput(true);
            // This lesson explicitly requires the ceiling on after the choice, regardless of edit-mode visibility.
            if (presenter.viewController.ceiling != null) presenter.viewController.ceiling.SetCeilingsVisible(true);
            IsQuestionActive = false; starting = submitting = false;
            guidance.ShowRoute(destination.approach.position, destination.label);
            RouteStarted?.Invoke(destination);
            routine = null;
        }
        void SetInput(bool enabled) { if (inputGate != null) { inputGate.interactable = enabled; inputGate.blocksRaycasts = enabled; } }
        void Fail(string message) { LastError = message; Debug.LogError("[OfficeFireChoice] " + message, this); }
        public void CancelQuestion()
        {
            bool cancellable = starting || IsQuestionActive || submitting;
            if (routine != null) StopCoroutine(routine);
            if (presenter != null && cancellable) presenter.ExitOverview();
            SetInput(true); starting = IsQuestionActive = submitting = false; routine = null;
            if (cancellable) QuestionCancelled?.Invoke();
        }
        void OnDisable()
        {
            if (presenter != null) presenter.SelectionSubmitted -= Selected;
            CancelQuestion();
        }
    }
}
