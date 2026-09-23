using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HMProtection.Quiz
{
    /// <summary>选择题限时进度条：红色进度随已用时间增长，末端火焰标记当前进度，
    /// 进度每过 1/3 增加一枚重叠跟随火焰，旁边显示倒计时。
    /// 计时窗口＝题目呈现（俯视界面可交互）→ 玩家与所选物体交互；
    /// 超时只发一次 TimedOut，由 OfficeChoiceInteraction 跳转 Incorrect 界面。</summary>
    public sealed class QuizTimerHUD : MonoBehaviour
    {
        [Tooltip("出题流程：题目呈现时自动开始计时，题目被取消时收起进度条。")]
        public OfficeFireChoiceFlow flow;
        [Tooltip("限时秒数：从俯视答题界面出现，到玩家对所选物体按 E 为止。")]
        public float durationSeconds = 45f;
        [Tooltip("优先使用题目 JSON 的 timeLimitSeconds；关闭后使用 Inspector 限时。")]
        public bool useQuestionTimeLimit = true;
        [Tooltip("剩余秒数不大于该值时倒计时变红。")]
        public float urgentSeconds = 10f;
        public Canvas canvas;
        [Tooltip("填充区域：火焰与里程碑的横向定位基准。")]
        public RectTransform fillArea;
        [Tooltip("红色进度填充，宽度由代码按进度驱动。")]
        public Image fill;
        [Tooltip("跟随进度末端的火焰图标。")]
        public RectTransform leadingFlame;
        [Tooltip("跟随火焰：进度每过 1/n 增加一枚，与末端火焰重叠并一起移动。")]
        public Image[] milestoneFlames;
        public TMP_Text countdownLabel;
        public Color normalColor = Color.white;
        public Color urgentColor = new Color(1f, .42f, .42f);

        public bool IsRunning { get; private set; }
        public bool HasFired { get; private set; }
        public float RemainingSeconds { get; private set; }
        public float Progress01 { get; private set; }
        public float ElapsedSeconds { get; private set; }
        public event Action TimedOut;

        const float MilestonePopSeconds = .3f;
        HMProtection.Core.StageTimer clock;
        HMProtection.Modules.Assessment.QuestionSession attempt;
        public string AttemptId => attempt?.Attempt.AttemptId;
        double[] litAt;

        void OnEnable()
        {
            if (flow != null) { flow.QuestionPresented += OnQuestionPresented; flow.QuestionCancelled += OnQuestionCancelled; }
        }
        void OnDisable()
        {
            if (flow != null) { flow.QuestionPresented -= OnQuestionPresented; flow.QuestionCancelled -= OnQuestionCancelled; }
            StopCountdown();
        }
        void OnQuestionPresented()
        {
            float seconds = durationSeconds;
            if (useQuestionTimeLimit && flow != null && flow.presenter != null && flow.presenter.TryGetCatalog(out var catalog, out _))
            {
                var question = catalog.Find(flow.questionId);
                if (question != null && question.timeLimitSeconds > 0f) seconds = question.timeLimitSeconds;
            }
            Begin(seconds);
        }
        void OnQuestionCancelled() => StopCountdown();

        /// <summary>开始一轮倒计时；题目与阶段共用 Session 仿真时钟及暂停语义。</summary>
        public void Begin(float seconds)
        {
            durationSeconds = Mathf.Max(.1f, seconds);
            attempt?.TryCancel(ElapsedSeconds, out _);
            attempt = new HMProtection.Modules.Assessment.QuestionSession(Guid.NewGuid().ToString("N"),
                flow != null ? flow.questionId : "standalone", 0d, durationSeconds);
            if (clock == null)
            {
                var owner = new GameObject("Question Clock");
                owner.transform.SetParent(transform, false);
                clock = owner.AddComponent<HMProtection.Core.StageTimer>();
                clock.OnExpired += OnClockExpired;
            }
            clock.sessionHost = flow != null && flow.entityBindings != null ? flow.entityBindings.sessionHost : null;
            clock.Begin(durationSeconds);
            HasFired = false; IsRunning = true;
            RemainingSeconds = durationSeconds; Progress01 = 0f; ElapsedSeconds = 0f;
            int count = milestoneFlames != null ? milestoneFlames.Length : 0;
            litAt = new double[count];
            for (int i = 0; i < count; i++) { litAt[i] = -1.0; PlaceMilestone(i, false, 0f); }
            if (fill != null) fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            if (leadingFlame != null) { leadingFlame.anchoredPosition = Vector2.zero; leadingFlame.localScale = Vector3.one; }
            UpdateLabel();
            if (canvas != null) canvas.gameObject.SetActive(true);
        }
        /// <summary>完成交互或题目取消：停止计时并收起进度条，不触发 TimedOut。</summary>
        public void StopCountdown()
        {
            if (IsRunning) ReadClock();
            attempt?.TryCancel(ElapsedSeconds, out _);
            IsRunning = false;
            if (clock != null) clock.Stop();
            if (canvas != null) canvas.gameObject.SetActive(false);
        }
        void Update()
        {
            if (!IsRunning) return;
            ReadClock();
            float width = fillArea != null ? fillArea.rect.width : 0f;
            if (fill != null) fill.rectTransform.anchorMax = new Vector2(Progress01, 1f);
            if (leadingFlame != null)
            {
                leadingFlame.anchoredPosition = new Vector2(width * Progress01, 0f);
                leadingFlame.localScale = Vector3.one * (1f + .07f * Mathf.Sin((float)(Time.realtimeSinceStartupAsDouble * 7.0)));
            }
            if (milestoneFlames != null && litAt != null)
                for (int i = 0; i < milestoneFlames.Length; i++)
                {
                    double now = Time.realtimeSinceStartupAsDouble;
                    if (litAt[i] < 0.0 && Progress01 >= (i + 1) / (float)milestoneFlames.Length) litAt[i] = now;
                    PlaceMilestone(i, litAt[i] >= 0.0, litAt[i] >= 0.0 ? (float)(now - litAt[i]) : 0f);
                }
            UpdateLabel();
            ExpireIfDue();
        }
        void OnClockExpired() => ExpireIfDue();
        void ReadClock()
        {
            ElapsedSeconds = clock != null ? Mathf.Clamp(clock.Elapsed, 0f, durationSeconds) : 0f;
            Progress01 = Mathf.Clamp01(ElapsedSeconds / durationSeconds);
            RemainingSeconds = Mathf.Max(0f, durationSeconds - ElapsedSeconds);
        }
        void UpdateLabel()
        {
            if (countdownLabel != null)
            {
                int total = Mathf.CeilToInt(RemainingSeconds);
                countdownLabel.text = (total / 60) + ":" + (total % 60).ToString("00");
                countdownLabel.color = RemainingSeconds <= urgentSeconds ? urgentColor : normalColor;
            }
        }
        /// <summary>交互入口也检查截止时间，避免同帧 E 输入抢在 Update 前绕过超时。</summary>
        public bool ExpireIfDue()
        {
            if (!IsRunning) return HasFired;
            ReadClock();
            if (RemainingSeconds <= 0f && (attempt == null || attempt.TryTimeout(ElapsedSeconds, out _)))
            {
                IsRunning = false; HasFired = true;
                if (canvas != null) canvas.gameObject.SetActive(false);
                TimedOut?.Invoke();
            }
            return HasFired;
        }
        public bool TryCompleteAnswer(string optionId)
        {
            if (ExpireIfDue() || attempt == null) return false;
            return attempt.TryAnswer(optionId, ElapsedSeconds, out var result)
                && result.Resolution == HMProtection.Modules.Assessment.QuestionResolution.Answered;
        }
        void PlaceMilestone(int index, bool lit, float sinceLit)
        {
            var flame = milestoneFlames[index];
            if (flame == null) return;
            float width = fillArea != null ? fillArea.rect.width : 0f;
            // 错开约三分之一图标宽度，形成向左叠放的火焰簇；整簇随红色末端移动。
            float spacing = leadingFlame != null ? leadingFlame.rect.width * .35f : 20f;
            flame.rectTransform.anchoredPosition = new Vector2(width * Progress01 - spacing * (index + 1), 2f * (index + 1));
            if (!lit) { flame.rectTransform.localScale = Vector3.zero; flame.gameObject.SetActive(false); return; }
            flame.gameObject.SetActive(true);
            float t = Mathf.Clamp01(sinceLit / MilestonePopSeconds);
            float back = 1f + 2.70158f * Mathf.Pow(t - 1f, 3) + 1.70158f * Mathf.Pow(t - 1f, 2);
            flame.rectTransform.localScale = Vector3.one * Mathf.Max(.001f, back);
        }
    }
}
