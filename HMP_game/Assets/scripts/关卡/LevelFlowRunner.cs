// 关卡流程状态机引擎——《关卡架构设计（骨架版）》§八 阶段二：
//   「LevelFlowRunner 按 stages 执行序列；StageTimer/ScoreBoard；
//     统一计时冻结开关（答题/操作段外计时与火焰一起冻结——计划书硬约束）；
//     FireEffectController 只收「火势几级」」
//
// 数据来源：LevelBootstrapper 解析出的 LevelConfigDto（Resources/Configs/Levels/*.json）。
//   stages      → 本文件按 kind 逐段执行
//   fireCues    → 阶段开始时下发分级（+ 可选 intensity/scale/smokeAmount）
//   quiz        → 出题/计时/解说交接口径
//
// 与既有模块的分工（不重写已交付功能）：
//   - 出题、俯视机位、天花板/人物显隐、气泡与作答 → HMProtection.Quiz.OfficeFireChoiceFlow
//     + SceneChoicePresenter + QuizViewController（已交付并已通过 OfficeFireEntryChecks 验证）。
//     本引擎通过 quizFlow.runnerDriven 把「出题时机」接过来，其余一概不动。
//   - 取物指引路线 → GuidanceSystem.ShowRoute（由 OfficeFireChoiceFlow 在作答后调用）。
//   - 火焰分级 → FireEffectController.SetLevel / SetFrozen（本引擎是唯一下发方）。
//
// 调试热键（debugKeys，默认开）：F8 暂停冻结、F9 循环火势分级（计划书 §6.5 验收）、F10 跳过当前阶段。
using System;
using System.Collections;
using HMProtection.Quiz;
using HMProtection.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace HMProtection.Core
{
    [DisallowMultipleComponent]
    public sealed class LevelFlowRunner : MonoBehaviour
    {
        [Header("场景接线（留空则自动查找）")]
        [Tooltip("火焰分级控制器，挂在场景根物体「起火点」上")]
        [SerializeField] private FireEffectController fire;
        [Tooltip("已交付的办公室出题流程，挂在根物体「Scene Choices」上")]
        [SerializeField] private OfficeFireChoiceFlow quizFlow;
        [Tooltip("路线指引系统，挂在根物体「路线指引」上")]
        [SerializeField] private GuidanceSystem guidance;

        [Header("调试")]
        [Tooltip("热键：F8 暂停冻结、F9 循环火势分级、F10 跳过当前阶段")]
        [SerializeField] private bool debugKeys = true;
        [Tooltip("把阶段切换、火势下发、作答与结算打进 Console")]
        [SerializeField] private bool verboseLog = true;

        // ==== 对外只读状态（UI / 验收脚本 / 结算面板读这些） ====

        public LevelEntry Entry { get; private set; }
        public LevelConfigDto Config { get; private set; }
        public ScoreBoard Score { get; } = new ScoreBoard();
        public StageTimer Timer => timer;

        /// <summary>当前阶段下标；未开局为 -1。</summary>
        public int StageIndex { get; private set; } = -1;

        /// <summary>当前阶段配置；未开局为 null。</summary>
        public StageDto CurrentStage =>
            (Config != null && StageIndex >= 0 && StageIndex < Config.stages.Length) ? Config.stages[StageIndex] : null;

        public string CurrentStageId => CurrentStage != null ? CurrentStage.id : "";

        /// <summary>流程是否在跑。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>是否处于暂停（玩家/系统暂停，冻结火焰与计时）。</summary>
        public bool IsPaused { get; private set; }

        /// <summary>火焰与计时当前是否被冻结（= 暂停 或 阶段性冻结，如解说/等待「继续」）。</summary>
        public bool IsFrozen { get; private set; }

        /// <summary>当前火焰分级（转发 FireEffectController）。</summary>
        public FireLevel CurrentFireLevel => fire != null ? fire.CurrentLevel : FireLevel.None;

        /// <summary>当前是否有火焰实例在场景里。</summary>
        public bool HasFireInstance => fire != null && fire.HasFire;

        /// <summary>是否已经作答过（验收脚本用）。</summary>
        public bool QuizAnswered { get; private set; }

        public event Action<StageDto> StageEntered;
        public event Action<StageDto> StageExited;
        public event Action<bool> FrozenChanged;
        public event Action<ScoreBoard> LevelFinished;

        // ==== 内部状态 ====
        private body player;
        private StageTimer timer;
        private Coroutine routine;
        private bool holdFrozen;          // 阶段性冻结（解说 / 等待「继续」 / 配置要求冻结的阶段）
        private bool waitingForContinue;
        private bool skipRequested;
        private bool stopRequested;
        private bool destinationReached;
        private bool manualFireOverride;
        private OfficeChoiceInteraction pendingInteraction;
        private TrainingSettlementView settlement;
        private UnityEngine.Events.UnityAction<string, bool> pendingAnswer;

        // ==== 对外控制接口 ====

        /// <summary>开局：按配置的阶段序列跑一遍。由 LevelBootstrapper 调用。</summary>
        public void Begin(LevelEntry entry, LevelConfigDto config)
        {
            if (settlement != null) settlement.Dismiss();
            ClearPendingAnswer();
            Entry = entry;
            Config = config;
            ResolveReferences();

            Score.Configure(config.quiz != null ? config.quiz.totalQuestions : 4,
                            config.quiz != null ? config.quiz.scorePerQuestion : 7.5f,
                            config.quiz != null ? config.quiz.passCorrectCount : 3);
            Score.Reset();
            QuizAnswered = false;

            StageIndex = -1;
            stopRequested = false;
            skipRequested = false;
            waitingForContinue = false;
            holdFrozen = false;
            manualFireOverride = false;
            IsPaused = false;

            bool fromMenu = OfficeFireChoiceFlow.ConsumeEntryRequest();
            Log("开局：" + (entry != null ? entry.id : "(no entry)")
                + "　入口=" + (fromMenu ? "主菜单选场景" : "直接 Play 场景（开发/验收）"));

            if (routine != null) StopCoroutine(routine);
            routine = StartCoroutine(RunStages());
        }

        /// <summary>暂停/继续：暂停期间火焰与计时一起停（计划书 §6.3）。</summary>
        public void SetPaused(bool paused)
        {
            if (IsPaused == paused) return;
            IsPaused = paused;
            RefreshFrozen();
            Log(paused ? "暂停" : "继续");
        }

        /// <summary>“继续”按钮回调：结束「等待继续」，推进解说段。</summary>
        public void ContinueFromNarration()
        {
            if (!waitingForContinue) return;
            waitingForContinue = false;
            Log("收到「继续」");
        }

        /// <summary>Called before the instructor fades away, so no free-roam frame leaks between lessons.</summary>
        public void PrepareNextQuizTransition()
        {
            if (!IsRunning || stopRequested || Config?.stages == null) return;
            for (int i = StageIndex + 1; i < Config.stages.Length; i++)
            {
                var next = Config.stages[i];
                if (next == null) continue;
                if (string.Equals(next.kind, "settlement", StringComparison.OrdinalIgnoreCase))
                {
                    QuizLoadingOverlay.Show("PREPARING YOUR RESULTS");
                    QuizLoadingOverlay.SetProgress(.1f, "Putting your session report together...");
                    ApplyPlayerControl(false);
                    return;
                }
                if (!string.Equals(next.kind, "quiz", StringComparison.OrdinalIgnoreCase)) continue;
                QuizLoadingOverlay.Show("PREPARING NEXT QUESTION");
                QuizLoadingOverlay.SetProgress(.1f, "Updating the training environment...");
                ApplyPlayerControl(false);
                return;
            }
        }

        /// <summary>跳过当前阶段的剩余等待（调试/验收用）。</summary>
        public void SkipStage()
        {
            skipRequested = true;
            if (waitingForContinue) waitingForContinue = false;
            Log("跳过当前阶段：" + CurrentStageId);
        }

        /// <summary>中止流程。</summary>
        public void StopLevel()
        {
            if (settlement != null) settlement.Dismiss();
            stopRequested = true;
            waitingForContinue = false;
            if (routine != null) { StopCoroutine(routine); routine = null; }
            if (quizFlow != null) quizFlow.CancelQuestion();
            Finish();
        }

        /// <summary>手动循环火势分级——计划书 §6.5 验收：None→SmokeOnly→Small→Medium→Large。
        /// 下次阶段切换时自动交还给引擎（引擎始终是唯一权威下发方）。</summary>
        public void CycleFireLevel()
        {
            if (fire == null) return;
            FireLevel next = fire.CurrentLevel switch
            {
                FireLevel.None => FireLevel.SmokeOnly,
                FireLevel.SmokeOnly => FireLevel.Small,
                FireLevel.Small => FireLevel.Medium,
                FireLevel.Medium => FireLevel.Large,
                _ => FireLevel.None,
            };
            fire.SetLevel(next);
            manualFireOverride = true;
            Log("[验收] 手动火势分级 → " + next + "（下次阶段切换时交还引擎）");
        }

        // ==== 阶段序列执行 ====

        private IEnumerator RunStages()
        {
            IsRunning = true;
            QuizLoadingOverlay.Show();

            // 从主菜单进来时，CG 转场黑场还盖在画面上：等它自然结束再开演，
            // 否则「失火原因动画」会在黑屏后面白播一遍。（兜底 60s，防止 CG 异常时卡死流程）
            float cgWaitStart = Time.realtimeSinceStartup;
            while (FindAnyObjectByType<CgTransitionPlayer>() != null)
            {
                if (Time.realtimeSinceStartup - cgWaitStart > 60f)
                {
                    Warn("等待 CG 转场超过 60s，直接开局");
                    break;
                }
                yield return null;
            }
            yield return null;
            QuizLoadingOverlay.SetProgress(.2f, "Preparing the training environment...");

            StageDto[] stages = Config != null ? Config.stages : null;
            if (stages == null || stages.Length == 0)
            {
                Warn("配置里没有 stages，流程无事可做");
                Finish();
                yield break;
            }

            for (StageIndex = 0; StageIndex < stages.Length; StageIndex++)
            {
                if (stopRequested) break;
                StageDto stage = stages[StageIndex];
                if (stage == null) continue;

                EnterStage(stage);
                yield return ExecuteStage(stage);
                ExitStage(stage);
                skipRequested = false;
            }

            Finish();
        }

        private IEnumerator ExecuteStage(StageDto stage)
        {
            switch ((stage.kind ?? "cutscene").ToLowerInvariant())
            {
                case "quiz": yield return QuizStage(stage); break;
                case "freeroam": yield return FreeRoamStage(stage); break;
                case "result": yield return ResultStage(stage); break;
                case "settlement": yield return SettlementStage(); break;
                // cutscene / video / 其它：按 duration 定时推进
                default: yield return TimedStage(stage); break;
            }
        }

        private IEnumerator SettlementStage()
        {
            if (settlement == null) settlement = GetComponent<TrainingSettlementView>();
            if (settlement == null) settlement = gameObject.AddComponent<TrainingSettlementView>();
            QuizLoadingOverlay.SetProgress(.95f, "Your session report is ready");
            settlement.Show(Score, () => Begin(Entry, Config), () => StartCoroutine(ReturnToMenu()));
            yield return QuizLoadingOverlay.Reveal();
        }

        private IEnumerator ReturnToMenu()
        {
            const string menuPath = "Assets/Scenes/初始界面.unity";
            if (!Application.CanStreamedLevelBeLoaded(menuPath))
            {
                Debug.LogError("Main menu is not in the build scene list.");
                settlement.Dismiss();
                settlement.Show(Score, () => Begin(Entry, Config), () => StartCoroutine(ReturnToMenu()));
                yield break;
            }
            StopLevel();
            QuizLoadingOverlay.Show("RETURNING TO MAIN MENU");
            yield return null;
            var operation = SceneManager.LoadSceneAsync(menuPath, LoadSceneMode.Single);
            while (!operation.isDone)
            {
                QuizLoadingOverlay.SetProgress(Mathf.Clamp01(operation.progress / .9f), "Loading the main menu...");
                yield return null;
            }
        }

        private void EnterStage(StageDto stage)
        {
            manualFireOverride = false; // 阶段切换收回火势权威
            Log("阶段 " + (StageIndex + 1) + "/" + Config.stages.Length + " → " + stage.id
                + "（" + stage.displayName + "／" + stage.kind + "）");

            ApplyPlayerControl(stage.playerControl);
            // 把操控交还给玩家的阶段，不能还盖着加载遮罩：第一关是靠答题流程（OfficeFireChoiceFlow →
            // QuizLoadingOverlay.Reveal）撤掉它的，没有答题段的关卡（如第二关自由练习）没有别的地方会撤，
            // 会永远停在"PREPARING TRAINING"。已隐藏时 Reveal() 自己 no-op，第一关时序不变。
            if (stage.playerControl && QuizLoadingOverlay.IsVisible)
                StartCoroutine(QuizLoadingOverlay.Reveal());
            holdFrozen = stage.freezeFire;
            RefreshFrozen();
            ApplyFire(stage.fireCue, stage.displayName);
            if (stage.frameFire) FrameCameraAtFire();

            if (timer != null)
            {
                if (stage.timed && stage.duration > 0f) timer.Begin(stage.duration);
                else timer.Stop();
            }
            StageEntered?.Invoke(stage);
        }

        private void ExitStage(StageDto stage)
        {
            if (timer != null) timer.Stop();
            StageExited?.Invoke(stage);
        }

        /// <summary>cutscene / video / settlement：按 duration 推进，期间可按 fireCueAt 切第二次火势。
        /// 用 Time.realtimeSinceStartup 计墙上时间，而不是累加 deltaTime——
        /// 编辑器在某些状态下 deltaTime 会退化（甚至为 0），累加式计时会让流程永远走不完。</summary>
        private IEnumerator TimedStage(StageDto stage)
        {
            float startedAt = Time.realtimeSinceStartup;
            float elapsed = 0f;
            bool secondaryFired = false;
            while (elapsed < stage.duration)
            {
                if (stopRequested || skipRequested) break;
                elapsed = Time.realtimeSinceStartup - startedAt;
                if (QuizLoadingOverlay.IsVisible)
                    QuizLoadingOverlay.SetProgress(.2f + .6f * Mathf.Clamp01(elapsed / Mathf.Max(.01f, stage.duration)));

                // 失火原因动画：冒烟 →（第 fireCueAt 秒）→ 起火
                if (!secondaryFired && stage.fireCueAt > 0f
                    && elapsed >= stage.fireCueAt && !string.IsNullOrEmpty(stage.secondaryFireCue))
                {
                    secondaryFired = true;
                    ApplyFire(stage.secondaryFireCue, stage.displayName + "·第二段");
                }
                yield return null;
            }
        }

        /// <summary>答题段：出题时机由本引擎决定，题目/俯视机位/作答仍由 OfficeFireChoiceFlow 负责。</summary>
        private IEnumerator QuizStage(StageDto stage)
        {
            if (quizFlow == null)
            {
                Warn("没有接线 OfficeFireChoiceFlow，答题段跳过");
                yield break;
            }

            quizFlow.questionId = !string.IsNullOrEmpty(stage.questionId) ? stage.questionId : Config.quiz.questionId;
            if (stage.resetPlayerToSpawn) GetComponent<LevelBootstrapper>()?.TeleportPlayerToSpawn();
            quizFlow.BeginQuestion();

            // 等题目真正可交互（OfficeFireChoiceFlow 内部还要等 CG 黑场结束）
            float guardStart = Time.realtimeSinceStartup;
            while (!quizFlow.IsQuestionActive && Time.realtimeSinceStartup - guardStart < 10f)
                yield return null;
            if (!quizFlow.IsQuestionActive)
            {
                Warn("答题段没能打开：" + (string.IsNullOrEmpty(quizFlow.LastError) ? "(无错误信息)" : quizFlow.LastError));
                yield break;
            }

            // 实体交互题（办公室四选一）：一条限时横跨「俯视选择→行走→按 E 交互」，
            // 由 QuizTimerHUD 负责计时与超时 Incorrect；本阶段的阶段计时必须让位，否则 15s 会提前掐断题目。
            var interaction = quizFlow.GetComponent<OfficeChoiceInteraction>();
            bool physical = interaction != null && interaction.isActiveAndEnabled;
            float limit = stage.duration > 0f
                ? stage.duration
                : (Config.quiz != null ? Config.quiz.secondsPerQuestion : 15f);
            if (physical)
            {
                if (timer != null) timer.Stop();
            }
            else if (timer != null)
            {
                timer.Begin(limit);
            }
            float startedAt = Time.realtimeSinceStartup;

            if (physical) ObservePhysicalAnswer(interaction, startedAt);

            while (quizFlow.IsQuestionActive
                   && !(timer != null && timer.HasExpired)
                   && !skipRequested
                   && !stopRequested)
            {
                yield return null;
            }

            bool timedOut = !physical && timer != null && timer.HasExpired;
            string optionId = quizFlow.LastSelectedOption;
            if (timedOut && quizFlow.IsQuestionActive) quizFlow.CancelQuestion(); // 超时要正常收尾，不阻断流程
            if (timer != null) timer.Stop();

            string questionId = string.IsNullOrEmpty(quizFlow.questionId) ? "unknown" : quizFlow.questionId;
            string correctId = !string.IsNullOrEmpty(quizFlow.CurrentQuestion?.correctOptionId)
                ? quizFlow.CurrentQuestion.correctOptionId : Config.quiz?.correctOptionId;
            if (!physical)
            {
                AnswerRecord record = Score.Record(questionId, optionId, correctId,
                                               Time.realtimeSinceStartup - startedAt, timedOut);
                QuizAnswered = true;
                Log("作答：" + (timedOut ? "超时未作答" : optionId) + " → " + (record.correct ? "正确" : "错误")
                + "　（正确答案 " + (string.IsNullOrEmpty(correctId) ? "未配置" : correctId) + "）");
            }

            // Physical choice questions reveal feedback only after the selected object is used.
            if (physical && string.IsNullOrEmpty(optionId))
            {
                // A timeout in the overview must finish feedback and review before any stage transition.
                while (interaction.IsPresentationActive && !stopRequested) yield return null;
            }
            else if (!physical)
                yield return Narration();
        }

        // 实体题在实际交互或超时时计分。只选择正确选项但没有及时完成操作，仍记为错误。
        private void ObservePhysicalAnswer(OfficeChoiceInteraction interaction, float startedAt)
        {
            ClearPendingAnswer();
            pendingInteraction = interaction;
            string questionId = quizFlow.questionId;
            string correctId = !string.IsNullOrEmpty(quizFlow.CurrentQuestion?.correctOptionId)
                ? quizFlow.CurrentQuestion.correctOptionId : Config.quiz?.correctOptionId;
            pendingAnswer = (option, correct) =>
            {
                bool timedOut = interaction.timer != null && interaction.timer.HasFired;
                float elapsed = interaction.timer != null ? interaction.timer.ElapsedSeconds : Time.realtimeSinceStartup - startedAt;
                var record = Score.Record(questionId, quizFlow.LastSelectedOption, correctId, elapsed, timedOut);
                record.correct = correct && !timedOut;
                QuizAnswered = true;
                ClearPendingAnswer();
                Log("实体题完成：" + (timedOut ? "超时" : option) + " → " + (record.correct ? "正确" : "错误"));
            };
            interaction.onActionCompleted.AddListener(pendingAnswer);
        }
        private void ClearPendingAnswer()
        {
            if (pendingInteraction != null && pendingAnswer != null) pendingInteraction.onActionCompleted.RemoveListener(pendingAnswer);
            pendingInteraction = null; pendingAnswer = null;
        }
        private void OnDestroy() => ClearPendingAnswer();

        /// <summary>解说 +（可选）等待「继续」：期间火焰与计时一起冻结（计划书 §2 / §6.3）。</summary>
        private IEnumerator Narration()
        {
            float seconds = Config.quiz != null ? Config.quiz.narrationSeconds : 6f;
            holdFrozen = true;
            RefreshFrozen();
            Log("解说中 " + seconds + "s —— 火焰与计时已冻结");
            yield return WaitUnscaled(seconds);

            if (Config.quiz != null && Config.quiz.waitForContinue)
            {
                waitingForContinue = true;
                bool infinite = Config.quiz.continueFallbackSeconds <= 0f;
                float guard = Config.quiz.continueFallbackSeconds;
                float waitStart = Time.realtimeSinceStartup;
                Log("等待点击「继续」" + (infinite ? "（无兜底超时）" : "（兜底 " + guard + "s）"));
                while (waitingForContinue && !stopRequested)
                {
                    if (skipRequested) break;
                    if (!infinite && Time.realtimeSinceStartup - waitStart >= guard)
                    {
                        Log("等待「继续」兜底超时，自动推进");
                        break;
                    }
                    yield return null;
                }
                waitingForContinue = false;
            }

            holdFrozen = false;
            RefreshFrozen();
        }

        /// <summary>取物段：路线由 GuidanceSystem 渲染，本引擎只计时并等「抵达 / 超时」。</summary>
        private IEnumerator FreeRoamStage(StageDto stage)
        {
            var interaction = quizFlow != null ? quizFlow.GetComponent<OfficeChoiceInteraction>() : null;
            if (interaction != null && interaction.isActiveAndEnabled && (interaction.Completed || !string.IsNullOrEmpty(quizFlow.LastSelectedOption)))
            {
                // Arrival only ends route guidance. The player must interact, then dismiss feedback.
                while (((!interaction.Completed && !skipRequested) || interaction.IsPresentationActive) && !stopRequested)
                    yield return null;
                if (timer != null) timer.Stop();
                Log("Selected destination interaction completed");
                yield break;
            }
            destinationReached = false;
            if (guidance != null) guidance.OnDestinationReached += OnGuidanceDestinationReached;
            if (timer != null && stage.duration > 0f) timer.Begin(stage.duration);

            while (!destinationReached
                   && !(timer != null && timer.HasExpired)
                   && !skipRequested
                   && !stopRequested)
            {
                yield return null;
            }

            if (guidance != null) guidance.OnDestinationReached -= OnGuidanceDestinationReached;
            if (timer != null) timer.Stop();
            Log(destinationReached ? "已抵达取物目标" : "取物段结束（超时/跳过，不阻断流程）");
        }

        private void OnGuidanceDestinationReached(Vector3 position, string label) => destinationReached = true;

        /// <summary>结果动画：正确 → 火势缩小 → 余烟 → 熄灭；错误 → 火势扩大。计时冻结。</summary>
        private IEnumerator ResultStage(StageDto stage)
        {
            bool correct = DecideCorrect();
            Log("结果动画：分支 = " + (correct ? "正确（火势缩小/熄灭）" : "错误（火势扩大）"));
            ApplyFire(correct ? stage.correctFireCue : stage.wrongFireCue,
                      "结果动画·" + (correct ? "正确分支" : "错误分支"));

            float total = stage.duration;
            bool hasEndCue = correct && !string.IsNullOrEmpty(stage.correctEndFireCue) && stage.correctEndAt > 0f;
            if (hasEndCue)
            {
                float middle = .15f + .65f * Mathf.Clamp01(stage.correctEndAt / Mathf.Max(.01f, total));
                yield return WaitUnscaled(Mathf.Min(stage.correctEndAt, total), .15f, middle);
                ApplyFire(stage.correctEndFireCue, "结果动画·正确分支·余烟后熄灭");
                yield return WaitUnscaled(Mathf.Max(0f, total - stage.correctEndAt), middle, .8f);
            }
            else
            {
                yield return WaitUnscaled(total, .15f, .8f);
            }
        }

        private bool DecideCorrect()
        {
            bool useLast = Config.quiz == null || Config.quiz.resultUsesLastAnswer;
            if (useLast && Score.LastAnswerCorrect.HasValue) return Score.LastAnswerCorrect.Value;
            return Score.Passed;
        }

        // ==== 火焰下发（本引擎是唯一权威） ====

        /// <summary>按 cue id 下发火势分级；可选同时下发 intensity/scale/smokeAmount。</summary>
        public void ApplyFire(string cueId, string reason)
        {
            if (fire == null) return;
            if (string.IsNullOrEmpty(cueId))
            {
                Log("[火势] " + reason + "：未配 cue，保持当前分级 " + fire.CurrentLevel);
                return;
            }
            FireCueDto cue = FindCue(cueId);
            if (cue == null)
            {
                Warn("[火势] 配置里找不到 cue \"" + cueId + "\"（阶段：" + reason + "）");
                return;
            }

            FireLevel level = ParseFireLevel(cue.level);
            fire.SetLevel(level);
            if (cue.useVfxParams)
            {
                FireVfx vfx = fire.CurrentVfx;
                if (vfx != null)
                {
                    vfx.SetIntensity(cue.intensity);
                    vfx.SetScale(cue.scale);
                    vfx.SetSmokeAmount(cue.smokeAmount);
                }
            }
            Log("[火势] " + reason + " → " + level + "（cue " + cue.id + "，实例 "
                + (fire.HasFire ? "已生成" : "无") + "）");
        }

        public FireCueDto FindCue(string cueId)
        {
            if (Config == null || Config.fireCues == null || string.IsNullOrEmpty(cueId)) return null;
            for (int i = 0; i < Config.fireCues.Length; i++)
                if (Config.fireCues[i] != null && Config.fireCues[i].id == cueId) return Config.fireCues[i];
            return null;
        }

        private static FireLevel ParseFireLevel(string name)
        {
            if (string.IsNullOrEmpty(name)) return FireLevel.None;
            return Enum.TryParse(name, true, out FireLevel parsed) ? parsed : FireLevel.None;
        }

        // ==== 冻结（火焰与计时成对） ====

        private void RefreshFrozen()
        {
            bool value = IsPaused || holdFrozen;
            if (IsFrozen == value) return;
            IsFrozen = value;
            if (fire != null) fire.SetFrozen(value);
            if (timer != null) { if (value) timer.Pause(); else timer.Resume(); }
            Log(value ? "冻结：火焰与计时一起停" : "解冻：火焰与计时继续");
            FrozenChanged?.Invoke(value);
        }

        // ==== 玩家操控与镜头 ====

        private void ApplyPlayerControl(bool enabled)
        {
            if (player == null) player = FindAnyObjectByType<body>();
            if (player == null) return;
            player.controlEnabled = enabled;
        }

        /// <summary>失火原因动画期间把玩家相机转向起火点——一进 Play 就能看见火焰（验收诉求）。</summary>
        public void FrameCameraAtFire()
        {
            if (fire == null || player == null) return;
            Camera camera = player.PlayerCamera;
            if (camera == null) return;

            Vector3 target = fire.transform.position + Vector3.up * 0.25f; // 火苗大致中心
            Vector3 direction = target - camera.transform.position;
            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            if (flat.sqrMagnitude < 1e-4f) return;

            float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(direction.y, flat.magnitude) * Mathf.Rad2Deg;
            player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            player.SetPitch(pitch);
            Log("相机转向起火点 " + target.ToString("F2") + "（yaw " + yaw.ToString("F0")
                + "°, pitch " + pitch.ToString("F0") + "°）");
        }

        // ==== 收尾 ====

        private void Finish()
        {
            QuizLoadingOverlay.Hide();
            ClearPendingAnswer();
            IsRunning = false;
            StageIndex = -1;
            IsPaused = false;
            holdFrozen = settlement != null && settlement.IsShowing;
            RefreshFrozen();
            if (timer != null) timer.Stop();
            if (player == null) player = FindAnyObjectByType<body>();
            if (player != null) player.controlEnabled = !(settlement != null && settlement.IsShowing);
            routine = null;
            Log("关卡流程结束\n" + Score.Summary());
            LevelFinished?.Invoke(Score);
        }

        // ==== 工具 ====

        private void ResolveReferences()
        {
            if (settlement == null) settlement = GetComponent<TrainingSettlementView>();
            if (settlement == null) settlement = gameObject.AddComponent<TrainingSettlementView>();
            if (fire == null) fire = FindAnyObjectByType<FireEffectController>();
            if (quizFlow == null) quizFlow = FindAnyObjectByType<OfficeFireChoiceFlow>();
            if (guidance == null) guidance = FindAnyObjectByType<GuidanceSystem>();
            if (player == null) player = FindAnyObjectByType<body>();
            if (timer == null) timer = GetComponent<StageTimer>();
            if (timer == null) timer = gameObject.AddComponent<StageTimer>();
        }

        /// <summary>按墙上时间等待若干秒（不用 deltaTime 累加，避免 deltaTime 退化时流程走不完）。</summary>
        private IEnumerator WaitUnscaled(float seconds, float progressFrom = -1f, float progressTo = -1f)
        {
            float startedAt = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - startedAt < seconds)
            {
                if (stopRequested || skipRequested) yield break;
                if (progressFrom >= 0f && QuizLoadingOverlay.IsVisible)
                    QuizLoadingOverlay.SetProgress(Mathf.Lerp(progressFrom, progressTo,
                        Mathf.Clamp01((Time.realtimeSinceStartup - startedAt) / Mathf.Max(.01f, seconds))));
                yield return null;
            }
        }

        private void Update()
        {
            if (!debugKeys || Config == null) return;
            if (settlement != null && settlement.IsShowing) return;
            if (QuizLoadingOverlay.IsVisible) return;
            // 教官的“任意键”也包含 F8/F9/F10，展示期间不让调试快捷键改变训练流程。
            if (quizFlow != null && quizFlow.GetComponent<OfficeChoiceInteraction>() is OfficeChoiceInteraction interaction
                && interaction.IsPresentationActive) return;
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.f8Key.wasPressedThisFrame) SetPaused(!IsPaused);
            // F9 火势分级只能有一个拥有者：火源自己开着 debugMode 时（测试场景）由它独占，
            // 否则同一次按键会被两个 Update 各推进一档（None→Small→Large…），看起来像随机跳级。
            if (keyboard.f9Key.wasPressedThisFrame && (fire == null || !fire.DebugKeyEnabled)) CycleFireLevel();
            if (keyboard.f10Key.wasPressedThisFrame) SkipStage();
        }

        private void Log(string message)
        {
            if (verboseLog) Debug.Log("[LevelFlow] " + message, this);
        }

        private void Warn(string message) => Debug.LogWarning("[LevelFlow] " + message, this);

        // 供验收脚本读取当前阶段是否卡在「等待继续」
        public bool WaitingForContinue => waitingForContinue;
    }
}
