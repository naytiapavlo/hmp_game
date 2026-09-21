// 火焰流程集成验收——《关卡架构设计（骨架版）》§七「自查编译引用 → Unity 验证」。
//
// 沿用本工程既有验收模式（见 OfficeFireEntryChecks）：写 Library/LevelFireChecks.request
// → 自动进 Play → 在 EditorApplication.update 里逐步断言 → 结果落到 Library/LevelFireChecks.txt。
// 也可以走菜单 Tools/Level1/Run Fire Checks (enters Play)。
//
// 它回答的就是本次要修的 bug：**Play 模式下起火点到底有没有真的在烧**。
// 断言按「越接近渲染层越硬」排序：
//   1. 起火点下有 FireVfx 实例（分级预制体被真正 Instantiate 了）
//   2. 该实例的粒子系统里**有活粒子**（particleCount > 0）——不只是"对象存在"
//   3. 点光源强度 > 0（火焰外溢的暖光）
//   4. 阶段推进时火势分级按配置变化；暂停时粒子真的被 Pause；每条 cue 都能下发
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HMProtection.Core;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class LevelFireChecks
{
    private const string RequestPath = "Library/LevelFireChecks.request";
    private const string ResultPath = "Library/LevelFireChecks.txt";
    private const string SessionKey = "LevelFireChecks.Running";
    private const string Revision = "v3";           // 打印进结果，便于对照是哪一版断言
    private const double Budget = 180.0;   // 总兜底，防挂死

    private static int phase;
    private static double phaseDeadline;  // 当前阶段的等待上限
    private static double startTime;
    private static readonly List<string> passes = new List<string>();
    private static readonly List<string> failures = new List<string>();
    private static readonly List<string> logs = new List<string>();

    private static LevelFlowRunner runner;
    private static FireEffectController fire;
    private static HMProtection.Quiz.OfficeFireChoiceFlow flow;

    static LevelFireChecks()
    {
        EditorApplication.delayCall += () =>
        {
            if (!File.Exists(RequestPath)) return;
            File.Delete(RequestPath);
            // 先保证场景装好状态机，再进 Play——否则 Play 里根本没有 LevelFlowRunner
            if (!File.Exists("Library/LevelSetup.v1.done")) LevelSetup.Install();
            SessionState.SetBool(SessionKey, true);
            EditorApplication.isPlaying = true;
        };

        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            if (!SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, false);
            passes.Clear(); failures.Clear(); logs.Clear();
            Application.logMessageReceived += OnLog;
            // 验收必须在后台也能跑完：Unity 在「Run In Background」关闭时，
            // 编辑器一旦失去焦点就会暂停 Play 模式，游戏协程随之停摆
            // ——上一轮就是这样卡在失火动画里的。这里只在本局强制开启，不改工程设置。
            Application.runInBackground = true;
            Time.timeScale = 1f;
            phase = 0;
            runner = null; fire = null; flow = null;
            startTime = EditorApplication.timeSinceStartup;
            phaseDeadline = startTime + 10.0;
            EditorApplication.update += Tick;
        };
    }

    [MenuItem("Tools/Level1/Run Fire Checks (enters Play)")]
    public static void Request()
    {
        Directory.CreateDirectory("Library");
        File.WriteAllText(RequestPath, "run");
        Debug.Log("[LevelFireChecks] 已写入 " + RequestPath + "；编辑器将自动进入 Play，结果输出到 " + ResultPath);
    }

    private static void Check(bool ok, string message)
    {
        (ok ? passes : failures).Add((ok ? "PASS  " : "FAIL  ") + message);
    }

    private static void Tick()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorApplication.update -= Tick;
            return;
        }
        try
        {
            if (EditorApplication.timeSinceStartup - startTime > Budget)
                throw new Exception("验收超时（budget " + Budget + "s，停在 phase " + phase + "）");

            switch (phase)
            {
                case 0: Phase0_Boot(); break;
                case 1: Phase1_Smoke(); break;
                case 2: Phase2_Flame(); break;
                case 3: Phase3_Quiz(); break;
                case 4: Phase4_Freeze(); break;
                case 5: Phase5_DebugKey(); break;
                case 6: Phase6_CueSweep(); break;
                case 7: Phase7_ResultBranch(); break;
                case 8: Phase8_RunToCompletion(); break;
                default: Complete(); break;
            }
        }
        catch (Exception e)
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;
            Write("FAIL\n" + Join() + "\n\n" + e + Diagnostics());
            Debug.LogException(e);
        }
    }

    /// <summary>把状态机自己的 Console 输出收进结果文件——Editor.log 缓冲很重，不能指望它。</summary>
    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Log
            && condition.IndexOf("[LevelFlow]", StringComparison.Ordinal) < 0
            && condition.IndexOf("[LevelBoot]", StringComparison.Ordinal) < 0) return;
        if (logs.Count < 300) logs.Add(type + "  " + condition.Replace("\n", " | "));
    }

    private static string Diagnostics()
    {
        var sb = new StringBuilder();
        sb.Append("\n\n--- 运行时诊断 ---");
        sb.Append("\nTime.timeScale=").Append(Time.timeScale.ToString("F2"))
          .Append("  unscaledDeltaTime=").Append(Time.unscaledDeltaTime.ToString("F4"))
          .Append("  frameCount=").Append(Time.frameCount)
          .Append("  realtime=").Append(Time.realtimeSinceStartup.ToString("F1"))
          .Append("  isPlaying=").Append(Application.isPlaying);
        if (runner != null)
            sb.Append("\nrunner: stage#").Append(runner.StageIndex)
              .Append(" id=").Append(runner.CurrentStageId)
              .Append(" running=").Append(runner.IsRunning)
              .Append(" paused=").Append(runner.IsPaused)
              .Append(" frozen=").Append(runner.IsFrozen)
              .Append(" timerRunning=").Append(runner.Timer != null && runner.Timer.IsRunning);
        if (fire != null)
            sb.Append("\nfire: level=").Append(fire.CurrentLevel)
              .Append(" hasFire=").Append(fire.HasFire)
              .Append(" aliveParticles=").Append(AliveParticles());
        sb.Append("\n\n--- Console（状态机日志 / 警告 / 错误）---\n");
        sb.Append(logs.Count == 0 ? "(未捕获到状态机日志)" : string.Join("\n", logs));
        return sb.ToString();
    }

    // ---- 0) 引擎起来了，且停在失火原因动画 ----
    private static void Phase0_Boot()
    {
        runner ??= UnityEngine.Object.FindAnyObjectByType<LevelFlowRunner>();
        fire ??= UnityEngine.Object.FindAnyObjectByType<FireEffectController>();
        flow ??= UnityEngine.Object.FindAnyObjectByType<HMProtection.Quiz.OfficeFireChoiceFlow>();

        bool booted = runner != null && fire != null && runner.IsRunning
                      && runner.CurrentStageId == "ignition_cutscene";
        if (!booted)
        {
            if (EditorApplication.timeSinceStartup > phaseDeadline)
                throw new Exception("等不到引擎开局（runner=" + (runner != null)
                    + ", fire=" + (fire != null)
                    + ", running=" + (runner != null && runner.IsRunning)
                    + ", stage=" + (runner != null ? runner.CurrentStageId : "-") + "）");
            return;
        }

        var boot = UnityEngine.Object.FindAnyObjectByType<LevelBootstrapper>();
        Check(true, "LevelFlowRunner 已在运行，首阶段 = ignition_cutscene（失火原因动画）");
        Check(boot != null && boot.Ready, "LevelBootstrapper 配置就绪（Resources JSON 加载成功）"
            + (boot != null && !string.IsNullOrEmpty(boot.LastError) ? "：" + boot.LastError : ""));
        Check(runner.Config != null && runner.Config.stages.Length > 0,
            "关卡配置解析出 " + (runner.Config != null ? runner.Config.stages.Length : 0) + " 个阶段");
        Check(runner.Config != null && runner.Config.fireCues.Length > 0,
            "关卡配置解析出 " + (runner.Config != null ? runner.Config.fireCues.Length : 0) + " 条火势 cue");
        Check(flow != null && flow.runnerDriven,
            "OfficeFireChoiceFlow.runnerDriven = true（出题时机交给状态机）");
        Next(1, 12.0);
    }

    // ---- 1) 冒烟段：起火点下真的出现了火焰实例 + 冒烟粒子在发射 ----
    private static void Phase1_Smoke()
    {
        if (fire.CurrentLevel != FireLevel.SmokeOnly)
        {
            if (EditorApplication.timeSinceStartup > phaseDeadline)
                throw new Exception("等不到冒烟段火势 SmokeOnly（当前 " + fire.CurrentLevel + "）");
            return;
        }
        Check(fire.HasFire, "冒烟段：起火点下已实例化分级预制体（FireVfx 存在）← 原 bug 的直接反证");
        Check(fire.CurrentVfx != null, "冒烟段：FireEffectController.CurrentVfx 可访问");
        int alive = AliveParticles();
        Check(alive > 0, "冒烟段：粒子系统里有活粒子（particleCount = " + alive + "）");
        Next(2, 12.0);
    }

    // ---- 2) 起火段：小火苗 + 暖光 ----
    private static void Phase2_Flame()
    {
        if (fire.CurrentLevel != FireLevel.Small)
        {
            if (EditorApplication.timeSinceStartup > phaseDeadline)
                throw new Exception("等不到起火段火势 Small（当前 " + fire.CurrentLevel + "）");
            return;
        }
        Check(true, "失火原因动画按配置自 smokeOnly 切到初起火焰 Small（fireCueAt 生效）");
        int alive = AliveParticles();
        Check(alive > 0, "起火段：粒子系统里有活粒子（particleCount = " + alive + "）");

        float maxLight = 0f;
        if (fire.CurrentVfx != null)
            foreach (Light l in fire.CurrentVfx.GetComponentsInChildren<Light>(true))
                maxLight = Mathf.Max(maxLight, l.intensity);
        Check(maxLight > 0f, "起火段：火焰暖光点光源亮着（max intensity = " + maxLight.ToString("F2") + "）");
        Next(3, 45.0);   // 失火动画只剩 3.5s，留足余量
    }

    // ---- 3) 答题段：题目由引擎排程打开，火势维持初起规模 ----
    private static void Phase3_Quiz()
    {
        if (runner.CurrentStageId != "quiz")
        {
            if (EditorApplication.timeSinceStartup > phaseDeadline)
                throw new Exception("等不到答题阶段（当前 " + runner.CurrentStageId + "）");
            return;
        }
        Check(flow != null && flow.IsQuestionActive, "答题段：题目已打开并可交互（引擎排程生效）");
        Check(fire.CurrentLevel == FireLevel.Small,
            "答题段：火势维持初起规模（当前 " + fire.CurrentLevel + "）");
        Next(4, 5.0);
    }

    // ---- 4) 冻结：暂停时火焰与计时一起停（计划书 §6.3 硬约束）----
    private static void Phase4_Freeze()
    {
        runner.SetPaused(true);
        Check(runner.IsFrozen, "SetPaused(true) → IsFrozen = true");
        Check(runner.Timer != null && !runner.Timer.IsRunning, "冻结时倒计时也停了（Timer.IsRunning = false）");

        ParticleSystem[] systems = ParticleSystems();
        Check(systems.Length > 0, "冻结检查：取到 " + systems.Length + " 个粒子系统");
        Check(systems.Length > 0 && systems.All(p => p.isPaused),
            "冻结检查：全部粒子系统已 Pause（"
            + string.Join(", ", systems.Select(p => p.name + "=" + (p.isPaused ? "paused" : "PLAYING"))) + "）");

        runner.SetPaused(false);
        Check(!runner.IsFrozen, "SetPaused(false) → 解冻");
        Next(5, 5.0);
    }

    // ---- 5) F9 验收键等价接口：手动循环火势分级 ----
    private static void Phase5_DebugKey()
    {
        FireLevel before = fire.CurrentLevel;
        runner.CycleFireLevel();
        FireLevel after = fire.CurrentLevel;
        Check(after != before, "CycleFireLevel()（计划书 §6.5 的 F9 等价接口）切换分级：" + before + " → " + after);
        Next(6, 5.0);
    }

    // ---- 6) 每条 fireCue 都能真的下发到 FireEffectController ----
    private static void Phase6_CueSweep()
    {
        var bad = new List<string>();
        int total = 0;
        foreach (FireCueDto cue in runner.Config.fireCues)
        {
            if (cue == null) continue;
            total++;
            FireLevel expected = ParseLevel(cue.level);
            runner.ApplyFire(cue.id, "验收扫描");
            if (fire.CurrentLevel != expected)
                bad.Add(cue.id + "(期望 " + expected + " 实际 " + fire.CurrentLevel + ")");
        }
        Check(bad.Count == 0 && total > 0,
            "全部 " + total + " 条 fireCue 都能正确下发到 FireEffectController"
            + (bad.Count > 0 ? "；异常：" + string.Join("，", bad) : ""));
        Next(7, 5.0);
    }

    // ---- 7) 结果动画两条分支 ----
    private static void Phase7_ResultBranch()
    {
        StageDto result = runner.Config.stages.FirstOrDefault(s => s != null && s.kind == "result");
        Check(result != null, "配置里有 result 类型阶段");
        if (result == null) { Next(8, 5.0); return; }

        CheckCueApplied("结果动画·错误分支", result.wrongFireCue);
        CheckCueApplied("结果动画·正确分支", result.correctFireCue);
        CheckCueApplied("结果动画·正确分支「余烟后熄灭」", result.correctEndFireCue);

        Check(!runner.Score.Passed, "未答对时 Score.Passed = false → 结果动画会走错误分支");
        Next(8, 5.0);
    }

    private static void CheckCueApplied(string label, string cueId)
    {
        FireCueDto cue = runner.FindCue(cueId);
        if (cue == null)
        {
            Check(false, label + "：配置里找不到 cue \"" + cueId + "\"");
            return;
        }
        runner.ApplyFire(cueId, label);
        Check(fire.CurrentLevel == ParseLevel(cue.level),
            label + "下发正确（" + cueId + " → " + fire.CurrentLevel + "）");
    }

    // ---- 8) 一路跑到结束：阶段序列能完整走完并结算 ----
    private static void Phase8_RunToCompletion()
    {
        if (runner.IsRunning)
        {
            if (EditorApplication.timeSinceStartup > phaseDeadline)
                throw new Exception("阶段序列跑不完（卡在 " + runner.CurrentStageId + "）");
            runner.SkipStage();   // 快进：不等 15s 答题 / 50s 取物
            return;
        }
        Check(true, "阶段序列完整走完，流程自行结束（未卡死）");
        Check(runner.Score.AskedCount >= 1,
            "结算有作答记录（AskedCount = " + runner.Score.AskedCount + "）");
        Next(9, 1.0);
    }

    private static void Complete()
    {
        EditorApplication.update -= Tick;
        Application.logMessageReceived -= OnLog;
        Write((failures.Count == 0 ? "PASS" : "FAIL") + "  [" + Revision + "]\n" + Join()
              + "\n\n耗时 " + (EditorApplication.timeSinceStartup - startTime).ToString("0.0") + "s"
              + Diagnostics());
        EditorApplication.isPlaying = false;
    }

    // ==== 工具 ====

    private static string Join()
    {
        var sb = new StringBuilder();
        sb.Append(string.Join("\n", passes));
        if (failures.Count > 0) sb.Append('\n').Append(string.Join("\n", failures));
        return sb.ToString();
    }

    private static ParticleSystem[] ParticleSystems()
    {
        if (fire == null || fire.CurrentVfx == null) return new ParticleSystem[0];
        return fire.CurrentVfx.GetComponentsInChildren<ParticleSystem>(true);
    }

    private static int AliveParticles()
    {
        int total = 0;
        foreach (ParticleSystem ps in ParticleSystems()) total += ps.particleCount;
        return total;
    }

    private static FireLevel ParseLevel(string name)
    {
        if (string.IsNullOrEmpty(name)) return FireLevel.None;
        return Enum.TryParse(name, true, out FireLevel parsed) ? parsed : FireLevel.None;
    }

    private static void Next(int nextPhase, double seconds)
    {
        phase = nextPhase;
        phaseDeadline = EditorApplication.timeSinceStartup + seconds;
    }

    private static void Write(string text)
    {
        Directory.CreateDirectory("Library");
        File.WriteAllText(ResultPath, text);
        Debug.Log("[LevelFireChecks]\n" + text);
    }
}
