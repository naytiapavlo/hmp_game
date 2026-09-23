// 灭火器喷射——挂灭火器实例上（第三场景由 ExtinguisherSpraySetup 装配，第二关复用）。
// 操作：E 拿起（PickupItem 的 HandleAndNozzle 模式：右拳拎提把 + 左手握喷头）→ 按住鼠标左键 = 压把喷射，松开渐停。
//
// 持握由 ExtinguisherCarryRig 负责：瓶身直立、喷嘴随左手、软管弯曲。
// 表现与判定分离（沿用「火焰不参与物理判定」的项目原则）：
//   表现 = 喷嘴处沿视线的锥形干粉雾粒子（VFX_ExtinguisherSpray，ABC 干粉 → 米白偏黄）；
//   判定 = 从相机中心发 1 主 + 2 散射射线，对火焰根部带做解析几何求交（不依赖任何碰撞体）——
//          从相机发才符合准星预期，喷嘴只是粒子发射原点。
//
// 判定规则（计划书 §4.3）：喷嘴距火源超 range 无效；只喷到火苗上部无效（TooHigh）；
//   覆盖根部带（followTarget 顶面上方 rootBandHeight、半径 rootBandRadius）才计有效，喂给 FireSuppression。
//
// 第二关预留：PinPulled（拔销步骤，模型语义化后接交互）、喷射统计（总时长/水平扫角累计——「扫射 10 分」数据）。
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(11000)]
[DisallowMultipleComponent]
[RequireComponent(typeof(PickupItem))]
public class ExtinguisherSpray : MonoBehaviour
{
    [Header("接线")]
    [Tooltip("兼容旧场景的单个明确目标；不再自动全场景查找")]
    [SerializeField] private FireSuppression suppression;
    [Tooltip("本喷射器可作用的明确火源候选。关卡 Scope 注册后由安装器填入。")]
    [SerializeField] private FireSuppression[] suppressionTargets;
    [Tooltip("每帧最多评估的火源数，避免大关卡喷射时产生无界物理查询。")]
    [SerializeField, Min(1)] private int targetSampleBudget = 8;
    [Tooltip("射线到火焰根部前被其它碰撞体遮挡时不计有效。")]
    [SerializeField] private LayerMask obstructionMask = ~0;
    [Tooltip("喷射粒子预制体（VFX_ExtinguisherSpray）")]
    [SerializeField] private GameObject sprayVfxPrefab;

    [Header("喷口（优先真实软管喷口）")]
    [Tooltip("软管口锚点：模型 Nozzle 空挂点，随左手喷嘴移动；缺省时按名查找")]
    [SerializeField] private Transform hoseNozzle;
    [Tooltip("旧模型缺少 Nozzle 时的备用局部喷口位置")]
    [SerializeField] private Vector3 nozzleLocalPosition = new Vector3(0f, 0.46f, 0.06f);

    [Header("判定参数")]
    [Tooltip("有效射程（米）：喷嘴到火源根部超过此值无效")]
    [SerializeField] private float range = 3f;
    [Tooltip("火焰根部带高度（米）：火源锚点顶面往上这段内命中才算有效覆盖")]
    [SerializeField] private float rootBandHeight = 0.35f;
    [Tooltip("火焰根部带半径（米）")]
    [SerializeField] private float rootBandRadius = 0.5f;
    [Tooltip("散射射线半角（度）：主射线左右各一条，模拟粉雾散布")]
    [SerializeField] private float scatterDegrees = 6f;

    [Header("第二关预留")]
    [Tooltip("保险销已拔（模型有独立销部件前默认 true；第二关接拔销交互后改由交互置位）")]
    [SerializeField] private bool pinPulled = true;
    [Tooltip("喷射音效挂点（占位，素材到位后指定）")]
    [SerializeField] private AudioSource sprayAudio;

    [Header("调试")]
    [Tooltip("无效命中（喷上部/超程）节流打进 Console")]
    [SerializeField] private bool verboseLog = true;

    /// <summary>保险销是否已拔。</summary>
    public bool PinPulled => pinPulled;

    /// <summary>拔销（第二关拔销交互调用）。</summary>
    public void PullPin() => pinPulled = true;

    /// <summary>当前是否正在喷射。</summary>
    public bool IsSpraying { get; private set; }

    /// <summary>本轮累计喷射秒数（第二关操作统计）。</summary>
    public float SpraySecondsTotal { get; private set; }

    /// <summary>喷射期间水平扫角累计（度；第二关「扫射」计分数据）。</summary>
    public float SweepAngleTotal { get; private set; }

    private PickupItem pickup;
    private ParticleSystem emitter;
    private Camera viewCamera;
    private InteractionHUD hud;
    private float prevYaw;
    private bool hasPrevYaw;
    private float lastInvalidLogAt;
    private float sprayStartedAt = -1f;

    private ExtinguisherCarryRig carryRig;
    // Fixed priority is explicit: legacy single target first, then serialized
    // candidates in Inspector order.  The bounded arrays keep held-spray Update
    // allocation-free even in levels that expose many possible fire sources.
    private const int MaxTargetSamples = 16;
    private readonly FireSuppression[] targetBuffer = new FireSuppression[MaxTargetSamples];
    private readonly SprayHitQuality[] qualityBuffer = new SprayHitQuality[MaxTargetSamples];

    // 喷射有效命中：准星淡蓝白微放大（粉雾打在火焰根部的感觉）
    private static readonly Color SprayHitColor = new Color(0.72f, 0.90f, 1.00f, 0.95f);
    private const float SprayHitScale = 1.45f;

    private void Awake()
    {
        foreach (Transform child in GetComponentsInChildren<Transform>(true))
            if (hoseNozzle == null && child.name == "Nozzle") { hoseNozzle = child; break; }
        pickup = GetComponent<PickupItem>();
        hud = InteractionHUD.EnsureExists();   // Interactor.Awake 建过就直接复用
        carryRig = GetComponent<ExtinguisherCarryRig>();
        if (carryRig == null) carryRig = gameObject.AddComponent<ExtinguisherCarryRig>();
    }

    private void Update()
    {
        if (pickup == null) pickup = GetComponent<PickupItem>();
        bool held = pickup != null && pickup.IsHeld;
        bool wantSpray = held && pinPulled && ToolUseAllowed() && Mouse.current != null
                         && Mouse.current.leftButton.isPressed
                         && !PointerOverUi();

        if (wantSpray)
        {
            ResolveCamera();
            EnsureEmitter();
            if (emitter != null && viewCamera != null)
            {
                // 粒子锥体沿局部 +Z 发射：喷嘴位置挂在物品下（跟手），朝向每帧对齐视线
                emitter.transform.position = NozzlePosition;
                emitter.transform.rotation = Quaternion.LookRotation(viewCamera.transform.forward);
                if (!emitter.isPlaying) emitter.Play();
            }
            if (sprayAudio != null && !sprayAudio.isPlaying) sprayAudio.Play();
            if (sprayStartedAt < 0f) sprayStartedAt = Time.time;

            IsSpraying = true;
            float dt = Time.deltaTime;
            SpraySecondsTotal += dt;

            SprayHitQuality quality = ApplyToTargets();
            ApplyFeedback(quality);

            TrackSweep(dt);
            LogInvalidThrottled(quality);
        }
        else if (IsSpraying)
        {
            StopSpray();
        }

        // 未持握：清掉占用过的 HUD 状态；持握待喷：提示操作方式
        if (!held)
        {
            if (hud != null) { hud.SetCrosshairTint(null, 1f); hud.SetPromptOverride(null); }
        }
        else if (!IsSpraying && hud != null)
        {
            hud.SetCrosshairTint(null, 1f);
            hud.SetPromptOverride("按住左键喷射");
        }

        // 持握期间：节流报告「喷头 ↔ 左手抓握点」偏差（两点持握调参的唯一依据）

    }

    /// <summary>Explicit runtime wiring for a scoped level. No global scene discovery occurs.</summary>
    public void ConfigureTargets(FireSuppression legacyTarget, FireSuppression[] targets)
    {
        suppression = legacyTarget;
        suppressionTargets = targets;
    }

    // After arms, cylinder and nozzle have reached this frame's final poses.
    private void LateUpdate()
    {
        if (emitter == null || pickup == null || !pickup.IsHeld) return;
        emitter.transform.position = NozzlePosition;
        if (viewCamera != null) emitter.transform.rotation = Quaternion.LookRotation(viewCamera.transform.forward);
    }

    private void OnDisable()
    {
        StopSpray();
        if (hud != null) { hud.SetCrosshairTint(null, 1f); hud.SetPromptOverride(null); }
    }

    /// <summary>喷射期间的准星与提示反馈（覆盖通道优先于 Interactor 的基础提示）。</summary>
    private void ApplyFeedback(SprayHitQuality quality)
    {
        if (hud == null) return;
        if (quality == SprayHitQuality.Effective)
        {
            hud.SetCrosshairTint(SprayHitColor, SprayHitScale);
            hud.SetPromptOverride(null);   // 有效时不占提示条，让基础提示（放下方式）显示
        }
        else
        {
            hud.SetCrosshairTint(null, 1f);
            hud.SetPromptOverride(quality switch
            {
                SprayHitQuality.TooHigh => "对准火焰根部才有效",
                SprayHitQuality.TooFar => "太远了——靠近些再喷",
                _ => "对准火焰",
            });
        }
    }

    private void StopSpray()
    {
        IsSpraying = false;
        hasPrevYaw = false;
        if (emitter != null && emitter.isPlaying)
            emitter.Stop(true, ParticleSystemStopBehavior.StopEmitting); // 已喷出的粉雾自然飘散
        if (sprayAudio != null && sprayAudio.isPlaying) sprayAudio.Stop();
        if (sprayStartedAt >= 0f && verboseLog)
            Debug.Log("[Spray] 停止喷射：本轮 " + (Time.time - sprayStartedAt).ToString("0.0")
                      + "s　累计 " + SpraySecondsTotal.ToString("0.0")
                      + "s　扫角 " + SweepAngleTotal.ToString("0") + "°", this);
        sprayStartedAt = -1f;
    }

    // ==== 命中判定（解析几何：射线 × 竖直圆柱带，不用物理） ====

    private SprayHitQuality ApplyToTargets()
    {
        if (viewCamera == null) return SprayHitQuality.None;
        SprayHitQuality best = SprayHitQuality.None;
        int sampled = CollectTargets();
        for (int i = 0; i < sampled; i++)
        {
            FireSuppression target = targetBuffer[i];
            SprayHitQuality quality = EvaluateHit(target);
            qualityBuffer[i] = quality;
            if (QualityRank(quality) > QualityRank(best)) best = quality;
        }
        int effectiveCount = 0;
        for (int i = 0; i < sampled; i++) if (qualityBuffer[i] == SprayHitQuality.Effective) effectiveCount++;
        float share = CoverageShareForEffectiveTarget(effectiveCount);
        for (int i = 0; i < sampled; i++)
            targetBuffer[i].ApplySpray(qualityBuffer[i], qualityBuffer[i] == SprayHitQuality.Effective ? share : 0f);
        return best;
    }

    private int CollectTargets()
    {
        int count = 0;
        int limit = Mathf.Min(MaxTargetSamples, Mathf.Max(1, targetSampleBudget));
        AddTarget(suppression, ref count, limit);
        if (suppressionTargets == null) return count;
        for (int i = 0; i < suppressionTargets.Length; i++)
        {
            if (count >= limit) break;
            AddTarget(suppressionTargets[i], ref count, limit);
        }
        return count;
    }

    private void AddTarget(FireSuppression target, ref int count, int limit)
    {
        if (target == null || !target.isActiveAndEnabled || count >= limit) return;
        for (int i = 0; i < count; i++) if (targetBuffer[i] == target) return;
        targetBuffer[count++] = target;
    }

    private static int QualityRank(SprayHitQuality quality) => quality switch
    {
        SprayHitQuality.Effective => 3, SprayHitQuality.TooHigh => 2, SprayHitQuality.TooFar => 1, _ => 0,
    };

    /// <summary>One tool's normalized budget is divided among its valid fire targets.</summary>
    public static float CoverageShareForEffectiveTarget(int effectiveTargetCount) =>
        effectiveTargetCount > 0 ? 1f / effectiveTargetCount : 0f;

    private SprayHitQuality EvaluateHit(FireSuppression target)
    {
        // 起火点锚点由 FireEffectController.LateUpdate 贴在起火道具顶面 +0.02，即火焰根部
        Vector3 basePos = target.transform.position;
        Vector3 nozzle = NozzlePosition;
        if (Vector3.Distance(nozzle, basePos) > range) return SprayHitQuality.TooFar;

        Transform camT = viewCamera.transform;
        return EvaluateTarget(target, new Ray(camT.position, camT.forward), camT.up);
    }

    /// <summary>Uses the production root-band and obstruction algorithm for editor/play checks.</summary>
    public SprayHitQuality EvaluateTargetForTest(FireSuppression target, Ray aimRay, Vector3 up) =>
        EvaluateTarget(target, aimRay, up);

    private SprayHitQuality EvaluateTarget(FireSuppression target, Ray main, Vector3 up)
    {
        if (target == null) return SprayHitQuality.None;
        Vector3 basePos = target.transform.position;
        Vector3 nozzle = NozzlePosition;
        if (Vector3.Distance(nozzle, basePos) > range) return SprayHitQuality.TooFar;
        Ray left = RotateRay(main, -scatterDegrees, up);
        Ray right = RotateRay(main, scatterDegrees, up);

        // 先看是否命中根部带（有效）；再看是否只够到更高的「火苗上部」带（无效）
        if (RayHitsBand(main, basePos, rootBandRadius, rootBandHeight, target)
            || RayHitsBand(left, basePos, rootBandRadius, rootBandHeight, target)
            || RayHitsBand(right, basePos, rootBandRadius, rootBandHeight, target))
            return SprayHitQuality.Effective;

        float upperHeight = rootBandHeight * 3.2f;
        if (RayHitsBand(main, basePos, rootBandRadius * 1.1f, upperHeight, target)
            || RayHitsBand(left, basePos, rootBandRadius * 1.1f, upperHeight, target)
            || RayHitsBand(right, basePos, rootBandRadius * 1.1f, upperHeight, target))
            return SprayHitQuality.TooHigh;

        return SprayHitQuality.None;
    }

    private static Ray RotateRay(Ray ray, float degrees, Vector3 axis) =>
        new Ray(ray.origin, Quaternion.AngleAxis(degrees, axis) * ray.direction);

    /// <summary>射线与「basePos 底、给定半径/高度的竖直圆柱带」是否相交（只算射线正向，允许轻微下探）。</summary>
    private bool RayHitsBand(Ray ray, Vector3 basePos, float radius, float height, FireSuppression target)
    {
        Vector3 o = ray.origin - basePos;
        Vector3 d = ray.direction;

        // 射线上离竖直轴线（水平最近点）：把问题降到 XZ 平面解 t
        float denom = d.x * d.x + d.z * d.z;
        float t = denom < 1e-8f ? 0f : -(o.x * d.x + o.z * d.z) / denom;
        if (t < 0f) t = 0f;

        Vector3 p = o + d * t;
        float horizDist = new Vector2(p.x, p.z).magnitude;
        if (horizDist > radius || p.y < -0.05f || p.y > height) return false;
        float hitDistance = t;
        if (Physics.Raycast(ray, out RaycastHit obstruction, hitDistance, obstructionMask, QueryTriggerInteraction.Ignore)
            && obstruction.collider != null && !obstruction.collider.transform.IsChildOf(target.transform)) return false;
        return true;
    }

    // ==== 统计与工具 ====

    private void TrackSweep(float dt)
    {
        if (viewCamera == null) return;
        float yaw = viewCamera.transform.eulerAngles.y;
        if (hasPrevYaw) SweepAngleTotal += Mathf.Abs(Mathf.DeltaAngle(prevYaw, yaw));
        prevYaw = yaw;
        hasPrevYaw = true;
    }

    private void ResolveCamera()
    {
        if (viewCamera != null) return;
        Transform view = pickup != null && pickup.Carrier != null ? pickup.Carrier.ViewTransform : null;
        if (view != null) viewCamera = view.GetComponent<Camera>();
        if (viewCamera == null) viewCamera = Camera.main;
    }

    private bool ToolUseAllowed()
    {
        body actorBody = pickup != null && pickup.Carrier != null
            ? pickup.Carrier.GetComponent<body>() : null;
        return IsToolUseAllowed(actorBody);
    }

    public static bool IsToolUseAllowed(body actorBody) => actorBody == null || actorBody.sessionHost == null
        || !actorBody.sessionHost.IsBlocked(HMProtection.Sessions.ControlMask.ToolUse);

    private void EnsureEmitter()
    {
        if (emitter != null || sprayVfxPrefab == null) return;
        emitter = Instantiate(sprayVfxPrefab, transform).GetComponent<ParticleSystem>();
        emitter.transform.localPosition = nozzleLocalPosition;
        emitter.transform.localRotation = Quaternion.identity;
        emitter.gameObject.name = "SprayEmitter";
        emitter.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    /// <summary>喷射原点世界坐标：优先软管口；旧模型没有挂点时才使用配置偏移。</summary>
    private Vector3 NozzlePosition => hoseNozzle != null
        ? hoseNozzle.position
        : transform.TransformPoint(nozzleLocalPosition);

    private static bool PointerOverUi() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    private void LogInvalidThrottled(SprayHitQuality quality)
    {
        if (!verboseLog || quality == SprayHitQuality.Effective) return;
        if (Time.time - lastInvalidLogAt < 0.5f) return;
        lastInvalidLogAt = Time.time;
        string reason = quality == SprayHitQuality.TooHigh
            ? "只喷到火苗上部——对准火焰根部（底部）才有效"
            : quality == SprayHitQuality.TooFar
                ? "超出有效射程 " + range + "m——再靠近些"
                : "未覆盖火焰根部";
        Debug.Log("[Spray] 无效命中：" + reason, this);
    }

}
