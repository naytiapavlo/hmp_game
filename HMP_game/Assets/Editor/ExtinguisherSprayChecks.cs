// 灭火器压制链路自动验收——进第三场景 Play，直接驱动 FireSuppression.ApplySpray 断言行为：
//   A 压制降级链 Large→Medium→Small→SmokeOnly→None + 扑灭观察窗确认；
//   B 外部切换（F9/关卡 cue）后压制自愈不卡死；
//   C 只喷上部（TooHigh）不积累压制；
//   D 压降级后停手 → 余火复燃 Small→Medium（一次一级）。
// 喷射的射线/输入部分（ExtinguisherSpray.EvaluateHit）不在此覆盖——那部分按第三场景说明手动验收。
// 模式与 QuizTimerChecks 一致：SessionState 标记 + 打开目标场景进 Play + EditorApplication.update 驱动协程。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class ExtinguisherSprayChecks
{
    private const string Report = "Library/ExtinguisherSprayChecks.txt";
    private static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
    private static readonly List<string> results = new List<string>();
    private static double deadline;
    private static bool previousBackground;

    static ExtinguisherSprayChecks()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("ExtinguisherSprayChecks", false))
            {
                results.Clear();
                deadline = EditorApplication.timeSinceStartup + 60;
                previousBackground = Application.runInBackground;
                Application.runInBackground = true;
                work.Push(Check());
            }
            if (state == PlayModeStateChange.ExitingPlayMode && SessionState.GetBool("ExtinguisherSprayChecks", false))
            {
                work.Clear();
                Application.runInBackground = previousBackground;
            }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool("ExtinguisherSprayChecks", false))
            {
                SessionState.SetBool("ExtinguisherSprayChecks", false);
                string scene = SessionState.GetString("ExtinguisherSprayChecksScene", "");
                if (!string.IsNullOrEmpty(scene)) EditorSceneManager.OpenScene(scene);
            }
        };
    }

    [MenuItem("Tools/Props/验收：灭火器压制链路")]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = SceneManager.GetActiveScene();
        if (scene.isDirty)
        {
            File.WriteAllText(Report, "BLOCKED: 先保存当前场景再运行验收。");
            Debug.LogWarning("[ExtinguisherSprayChecks] 当前场景未保存，已阻止。");
            return;
        }
        SessionState.SetString("ExtinguisherSprayChecksScene", scene.path);
        EditorSceneManager.OpenScene("Assets/Scenes/第三场景.unity");
        SessionState.SetBool("ExtinguisherSprayChecks", true);
        EditorApplication.isPlaying = true;
    }

    private static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!EditorApplication.isPlaying || work.Count == 0) return;
        try
        {
            Need(EditorApplication.timeSinceStartup < deadline, "验收超时");
            IEnumerator current = work.Peek();
            if (!current.MoveNext())
            {
                work.Pop();
                if (work.Count == 0) Finish("PASS");
            }
        }
        catch (Exception e)
        {
            results.Add(e.ToString());
            Finish("FAIL");
        }
    }

    private static void Finish(string verdict)
    {
        work.Clear();
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("== 灭火器压制链路验收 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " : " + verdict + " ==");
        foreach (string line in results) sb.AppendLine(line);
        File.WriteAllText(Report, sb.ToString());
        Debug.Log("[ExtinguisherSprayChecks] " + verdict + "，报告：" + Report + "\n" + sb);
    }

    // ==== 驱动工具 ====

    private static void Need(bool value, string message)
    {
        if (!value) throw new Exception("断言失败：" + message);
    }

    private static IEnumerator Delay(float seconds)
    {
        double end = EditorApplication.timeSinceStartup + seconds;
        while (EditorApplication.timeSinceStartup < end) yield return null;
    }

    /// <summary>每帧喂一次有效喷射直到条件满足（超时抛断言）。</summary>
    private static IEnumerator SprayUntil(FireSuppression supp, Func<bool> done, double timeoutSeconds, string what)
    {
        double end = EditorApplication.timeSinceStartup + timeoutSeconds;
        while (!done())
        {
            Need(EditorApplication.timeSinceStartup < end, "等待超时：" + what);
            supp.ApplySpray(SprayHitQuality.Effective);
            yield return null;
        }
    }

    /// <summary>每帧喂一次指定质量的喷射，持续若干秒。</summary>
    private static IEnumerator SprayFor(FireSuppression supp, SprayHitQuality quality, float seconds)
    {
        double end = EditorApplication.timeSinceStartup + seconds;
        while (EditorApplication.timeSinceStartup < end)
        {
            supp.ApplySpray(quality);
            yield return null;
        }
    }

    // ==== 验收流程 ====

    private static IEnumerator Check()
    {
        yield return Delay(2f);

        var fire = UnityEngine.Object.FindAnyObjectByType<FireEffectController>();
        var supp = UnityEngine.Object.FindAnyObjectByType<FireSuppression>();
        Need(fire != null, "场景里有 FireEffectController（起火点）");
        Need(supp != null, "场景里有 FireSuppression（跑 Tools/Props/部署灭火器喷射）");

        // 加速参数：只影响本次 Play，不落盘（public 字段，运行时覆盖）
        supp.suppressSpeed = 8f;
        supp.recoverSpeed = 0.5f;
        supp.reigniteAfter = 0.5f;
        supp.confirmSeconds = 0.5f;

        // --- A：压制降级链 + 扑灭确认 ---
        int lowered = 0, extinguishedCount = 0;
        supp.LevelLowered += _ => lowered++;
        supp.Extinguished += () => extinguishedCount++;

        fire.SetLevel(FireLevel.Large);
        yield return Delay(0.3f);
        yield return SprayUntil(supp, () => fire.CurrentLevel == FireLevel.None, 8, "压制到熄灭");
        Need(lowered >= 4, "Large→…→None 至少 4 次降级，实际 " + lowered);
        yield return Delay(supp.confirmSeconds + 0.4f);
        Need(extinguishedCount == 1 && supp.ExtinguishedConfirmed, "扑灭观察窗通过并触发一次 Extinguished");
        results.Add("PASS A 压制降级链（" + lowered + " 次降级）+ 扑灭确认（观察 " + supp.confirmSeconds + "s）");

        // --- B：外部切换（F9/关卡 cue）后自愈 ---
        fire.SetLevel(FireLevel.Small);          // 外部点火（模拟 F9）
        yield return Delay(0.2f);
        Need(supp.Pressed01 > 0.99f, "外部切换后压制状态重置为满压");
        int loweredBefore = lowered;
        yield return SprayUntil(supp, () => lowered > loweredBefore, 5, "压制 Small 降级");   // → SmokeOnly
        fire.SetLevel(FireLevel.Large);          // 压制途中被外部改级
        yield return SprayUntil(supp, () => lowered > loweredBefore + 1, 5, "外部切换后继续压制降级"); // Large→Medium
        Need(fire.CurrentLevel == FireLevel.Medium, "外部切 Large 后压制能继续降级到 Medium");
        results.Add("PASS B 外部切换火势后压制自愈（当前 " + fire.CurrentLevel + "）");

        // --- C：只喷上部无效 ---
        fire.SetLevel(FireLevel.Medium);
        yield return Delay(0.6f);                 // 让恢复把强度回满
        Need(fire.CurrentLevel == FireLevel.Medium && supp.Pressed01 > 0.9f, "无效判定前的基线满压");
        yield return SprayFor(supp, SprayHitQuality.TooHigh, 1.2f);
        yield return Delay(0.1f);
        Need(fire.CurrentLevel == FireLevel.Medium && supp.Pressed01 > 0.9f, "喷上部 1.2s 不降级也不压低强度");
        results.Add("PASS C 只喷火苗上部无效（级与强度均无变化）");

        // --- D：复燃（余火 Small 停手 → 回升一级）---
        fire.SetLevel(FireLevel.Medium);
        yield return Delay(0.2f);
        int loweredBase = lowered;
        int reignited = 0;
        supp.Reignited += () => reignited++;
        yield return SprayUntil(supp, () => lowered > loweredBase, 5, "压到余火 Small");      // Medium→Small（loweredByUs=true）
        Need(fire.CurrentLevel == FireLevel.Small, "已压到余火 Small");
        yield return Delay(supp.reigniteAfter + 0.8f);   // 停手等复燃
        Need(reignited == 1 && fire.CurrentLevel == FireLevel.Medium, "停手后余火复燃 Small→Medium");
        results.Add("PASS D 复燃：压降级后停手 " + supp.reigniteAfter + "s 余火回升一级");

        results.Add("全部通过：降级链 / 外部自愈 / 喷上部无效 / 复燃");
    }
}
