// 火焰压制状态机——灭火器喷射逻辑与火焰表现的对接层（第三场景原型，第二关复用）。
// 计划书 §4.3 口径：「正确覆盖可以灭火；只喷上方无效；局部压制会留下余火并复燃；不随机制造事故。」
//
// 与 FireEffectController 的分工（零改动对接）：
//   - 分级切换仍走 fire.SetLevel（本组件是继 LevelFlowRunner 之后的第二个合法下发方，
//     第三场景没有关卡流程，不冲突；正式第二关接入时由关卡层决定权威归属）；
//   - 级内渐变走 FireVfx.SetIntensity / SetSmokeAmount——明火级压发射率、SmokeOnly 级压烟量，
//     视觉是"火苗逐渐变小"而不是硬切；
//   - 外部切换检测：每帧比对 CurrentLevel 与自己记录的级别，不一致（F9 手动 / 关卡 cue）
//     就重置压制状态继续工作——与 F9 的互锁不需要 FireEffectController 暴露任何新接口。
//
// 行为参数（全部可调，验收脚本会改小时间参数加速测试）：
//   喷中根部 → pressed 从 1 压到 lowerAt 降一级（Large→Medium→Small→SmokeOnly→None）；
//   SmokeOnly 级压烟量，压到 smokeLowerAt → None，进入扑灭观察窗；
//   到 None 后 confirmSeconds 秒无复燃才算扑灭（第二关「扑灭且观察 5 秒」计分项接口）；
//   停手后级内缓慢恢复（余火回旺）；曾被压降级又停手超过 reigniteAfter → Small 回升一级（复燃，一次一级）。
using System;
using UnityEngine;

/// <summary>一次喷射判定的命中质量（由 ExtinguisherSpray 的射线组算出）。</summary>
public enum SprayHitQuality
{
    /// <summary>未命中火区</summary>
    None,
    /// <summary>距离超出有效射程</summary>
    TooFar,
    /// <summary>只喷到火苗上部（计划书：无效）</summary>
    TooHigh,
    /// <summary>有效覆盖火焰根部</summary>
    Effective,
}

[DisallowMultipleComponent]
public class FireSuppression : MonoBehaviour
{
    [Header("接线（留空自动找同物体上的 FireEffectController）")]
    [SerializeField] private FireEffectController fire;

    [Header("压制节奏（每级从满压到降级耗时 ≈ (1-lowerAt)/suppressSpeed 秒）")]
    [Tooltip("有效喷射时级内强度下降速度（每秒）")]
    public float suppressSpeed = 1.1f;
    [Tooltip("未喷射时级内强度自然恢复速度（每秒）——余火回旺")]
    public float recoverSpeed = 0.12f;
    [Tooltip("明火级压到该强度即降一级")]
    public float lowerAt = 0.2f;
    [Tooltip("SmokeOnly 级烟量压到该值即熄灭")]
    public float smokeLowerAt = 0.15f;

    [Header("复燃与扑灭确认")]
    [Tooltip("曾压降级后停手超过该秒数 → Small 回升一级（复燃）")]
    public float reigniteAfter = 4f;
    [Tooltip("到 None 后观察该秒数无复燃才触发扑灭确认")]
    public float confirmSeconds = 5f;

    [Header("调试")]
    [Tooltip("把压制/降级/复燃/扑灭过程打进 Console")]
    [SerializeField] private bool verboseLog = true;

    /// <summary>当前级内强度 1→lowerAt（SmokeOnly 级表示烟量）。验收/调试读。</summary>
    public float Pressed01 => pressed;

    /// <summary>是否处于扑灭观察窗（火已到 None，等待确认）。</summary>
    public bool Confirming => confirming;

    /// <summary>是否已确认扑灭（观察窗通过；此后仅外部 SetLevel 能重新点火）。</summary>
    public bool ExtinguishedConfirmed => extinguished;

    /// <summary>最近一次 ApplySpray 的命中质量。</summary>
    public SprayHitQuality LastQuality => lastQuality;

    /// <summary>压制导致降级（目标级为参数）。第二关计分/表现接这里。</summary>
    public event Action<FireLevel> LevelLowered;

    /// <summary>复燃（Small 回升 Medium）。第二关结果表现接这里。</summary>
    public event Action Reignited;

    /// <summary>扑灭确认（None 后观察窗通过）。第二关「扑灭且观察 5 秒」计分项接这里。</summary>
    public event Action Extinguished;

    private float pressed = 1f;             // 级内强度（新实例默认 1）
    private FireLevel trackedLevel = FireLevel.None;
    private bool loweredByUs;               // 本轮压制是否降过级（复燃资格）
    private bool confirming;                // 扑灭观察窗中
    private float confirmStartAt;           // 观察窗起点
    private bool extinguished;              // 已确认扑灭
    private int lastSprayFrame = -999;      // ApplySpray 心跳（帧号）
    private bool lastSprayEffective;
    private float lastEffectiveAt;          // 最近一次有效喷射时刻（复燃计时基准）
    private SprayHitQuality lastQuality = SprayHitQuality.None;

    /// <summary>灭火器每帧喂喷射判定；不调用即视为停手（帧窗口容忍编辑器 Tick 抖动）。</summary>
    public void ApplySpray(SprayHitQuality quality)
    {
        lastSprayFrame = Time.frameCount;
        lastQuality = quality;
        lastSprayEffective = quality == SprayHitQuality.Effective;
        if (lastSprayEffective) lastEffectiveAt = Time.time;
    }

    private void Awake()
    {
        if (fire == null) fire = GetComponent<FireEffectController>();
        if (fire != null) trackedLevel = fire.CurrentLevel;
    }

    private void Update()
    {
        if (fire == null)
        {
            fire = GetComponent<FireEffectController>();
            if (fire == null) return;
            trackedLevel = fire.CurrentLevel;   // 延迟接线时静默同步，别当成外部切换
        }

        // 外部切换检测（F9 手动循环 / 关卡 fireCue）：级别与我们的记录不一致 = 别人改的。
        // 重置状态后继续工作——手动切到 Large 仍可继续压，不会卡死。
        if (fire.CurrentLevel != trackedLevel)
        {
            Log("外部切换火势 " + trackedLevel + " → " + fire.CurrentLevel + "（F9/关卡 cue），压制状态重置");
            trackedLevel = fire.CurrentLevel;
            pressed = 1f;
            loweredByUs = false;
            confirming = false;
            extinguished = false;
        }

        bool effective = Time.frameCount - lastSprayFrame <= 3 && lastSprayEffective;
        float dt = Time.deltaTime;

        if (trackedLevel == FireLevel.None)
        {
            // 已熄灭：只处理扑灭观察窗；复燃只发生在 Small（有明火的余火），None 不会自燃
            if (confirming && Time.time - confirmStartAt >= confirmSeconds)
            {
                confirming = false;
                extinguished = true;
                Log("[验收] 扑灭确认：观察 " + confirmSeconds + "s 无复燃");
                Extinguished?.Invoke();
            }
            return;
        }

        if (effective)
        {
            pressed = Mathf.Max(0f, pressed - suppressSpeed * dt);
            ApplyPressed();
            if (pressed <= (trackedLevel == FireLevel.SmokeOnly ? smokeLowerAt : lowerAt))
                LowerOneLevel();
        }
        else
        {
            if (pressed < 1f)
            {
                pressed = Mathf.Min(1f, pressed + recoverSpeed * dt);
                ApplyPressed();
            }

            // 复燃：曾被压降级 → 余火 Small 停手超时 → 回升一级（一次一级，不随机制造事故）
            if (loweredByUs && trackedLevel == FireLevel.Small && !confirming && !extinguished
                && Time.time - lastEffectiveAt >= reigniteAfter)
            {
                fire.SetLevel(FireLevel.Medium);
                trackedLevel = FireLevel.Medium;
                pressed = 1f;
                ApplyPressed();
                loweredByUs = false;
                Log("复燃：余火回升 Small → Medium（停手 " + reigniteAfter + "s 未继续压制）");
                Reignited?.Invoke();
            }
        }
    }

    private void LowerOneLevel()
    {
        FireLevel next = trackedLevel switch
        {
            FireLevel.Large => FireLevel.Medium,
            FireLevel.Medium => FireLevel.Small,
            FireLevel.Small => FireLevel.SmokeOnly,
            _ => FireLevel.None,
        };
        fire.SetLevel(next);
        trackedLevel = next;
        pressed = 1f;          // 新实例是 prefab 默认强度
        loweredByUs = true;
        if (next == FireLevel.None)
        {
            confirming = true;
            confirmStartAt = Time.time;
        }
        Log("压制降级 → " + next + (next == FireLevel.None ? "（进入扑灭观察窗 " + confirmSeconds + "s）" : ""));
        LevelLowered?.Invoke(next);
    }

    private void ApplyPressed()
    {
        FireVfx vfx = fire.CurrentVfx;
        if (vfx == null) return;
        if (trackedLevel == FireLevel.SmokeOnly) vfx.SetSmokeAmount(pressed);
        else vfx.SetIntensity(pressed);
    }

    private void Log(string message)
    {
        if (verboseLog) Debug.Log("[FireSupp] " + message, this);
    }
}
