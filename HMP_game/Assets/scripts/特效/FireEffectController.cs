// 火焰特效控制器——关卡状态机与火焰表现之间的唯一接口。
// 设计文档：《第一阶段计划》任务 4——「关卡状态机只下发火势几级，不直接操作粒子」。
//
// 用法（关卡状态机 / 未来 fireCues 数据）：
//   fireController.SetLevel(FireLevel.Small);   // 冒烟/初起火/扩大/熄灭 四级
//   fireController.SetFrozen(true);             // 暂停/解说/等待继续期间冻结
// 挂载位置：场景「起火点」锚点物体（FireSetup 自动装配），自身位置即火源位置。
//
// 分级与预制体映射（文档 6.1 四级表现）：
//   SmokeOnly = VFX_Smoke_Only（隐患冒烟，无明火白烟）
//   Small     = VFX_Fire_Small（初起火 0.2-0.4m，本阶段主表现）
//   Medium    = VFX_Fire_Medium（过渡/结果动画用）
//   Large     = VFX_Fire_Large（火势扩大，结果动画错误分支）
//   扑灭 = SetLevel(SmokeOnly) 后 SetLevel(None) 的组合（余烟 → 熄灭）。
//
// 调试：debugMode 开启时按 F9 循环 None→SmokeOnly→Small→Medium→Large，
// 对应文档 6.5 交付物「连续演示四个分级」的验收手段。
using UnityEngine;
using UnityEngine.InputSystem;

public enum FireLevel { None, SmokeOnly, Small, Medium, Large }

// ExecuteAlways：编辑模式拖动插排时火源锚点实时跟随，不用重跑装配
[ExecuteAlways]
public class FireEffectController : MonoBehaviour
{
    [Header("分级预制体（FireSetup 自动接线）")]
    [SerializeField] private GameObject prefabSmokeOnly;
    [SerializeField] private GameObject prefabSmall;
    [SerializeField] private GameObject prefabMedium;
    [SerializeField] private GameObject prefabLarge;

    [Header("起火道具跟随")]
    [Tooltip("跟随目标（起火道具，如做旧插排）：火源每帧贴在它的顶面——挪道具即可挪火，" +
             "无需手动同步「起火点」位置（FireSetup 自动接线）")]
    [SerializeField] private Transform followTarget;

    [Header("调试")]
    [Tooltip("开启后 Play 中按 F9 循环切换火势分级（验收演示用）")]
    [SerializeField] private bool debugMode = true;

    /// <summary>当前火势分级</summary>
    public FireLevel CurrentLevel { get; private set; } = FireLevel.None;

    /// <summary>当前分级实例（可能为 null）；供关卡状态机下发 intensity/scale/smokeAmount 三个 0-1 参数。
    /// 分级仍由 SetLevel 唯一决定（架构文档 §八：FireEffectController 只收「火势几级」），本属性只读。</summary>
    public FireVfx CurrentVfx => currentInstance;

    /// <summary>当前是否有正在显示的分级实例。</summary>
    public bool HasFire => currentInstance != null;

    private FireVfx currentInstance;
    private bool frozen;
    private Renderer[] targetRenderers;

    /// <summary>切换火势分级（实例化对应预制体替换当前实例）</summary>
    public void SetLevel(FireLevel level)
    {
        if (level == CurrentLevel) return;
        if (currentInstance != null) Destroy(currentInstance.gameObject);
        currentInstance = null;

        GameObject prefab = level switch
        {
            FireLevel.SmokeOnly => prefabSmokeOnly,
            FireLevel.Small => prefabSmall,
            FireLevel.Medium => prefabMedium,
            FireLevel.Large => prefabLarge,
            _ => null,
        };
        if (prefab != null)
        {
            GameObject go = Instantiate(prefab, transform.position, Quaternion.identity, transform);
            currentInstance = go.GetComponent<FireVfx>();
            if (frozen && currentInstance != null) currentInstance.Freeze(true);
        }
        CurrentLevel = level;
        Debug.Log("[Fire] 火势分级 → " + level, this);
    }

    /// <summary>随状态机冻结（暂停/解说/等待继续）：火焰与计时一起停</summary>
    public void SetFrozen(bool value)
    {
        frozen = value;
        if (currentInstance != null) currentInstance.Freeze(value);
    }

    private void LateUpdate()
    {
        // 火源贴跟随目标（起火道具）顶面：挪插排火就跟着走
        if (followTarget == null) return;
        if (targetRenderers == null) targetRenderers = followTarget.GetComponentsInChildren<Renderer>(true);
        if (targetRenderers.Length == 0) return;
        Bounds b = targetRenderers[0].bounds;
        for (int i = 1; i < targetRenderers.Length; i++) b.Encapsulate(targetRenderers[i].bounds);
        transform.position = new Vector3(b.center.x, b.max.y + 0.02f, b.center.z);
    }

    private void Update()
    {
        if (!debugMode || !Application.isPlaying || HMProtection.UI.QuizLoadingOverlay.IsVisible) return;
        Keyboard kb = Keyboard.current;
        if (kb == null || !kb.f9Key.wasPressedThisFrame) return;
        FireLevel next = CurrentLevel switch
        {
            FireLevel.None => FireLevel.SmokeOnly,
            FireLevel.SmokeOnly => FireLevel.Small,
            FireLevel.Small => FireLevel.Medium,
            FireLevel.Medium => FireLevel.Large,
            _ => FireLevel.None,
        };
        SetLevel(next);
    }
}
