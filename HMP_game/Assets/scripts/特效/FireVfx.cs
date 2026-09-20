// 火焰特效运行时参数控制（占位视觉版）——挂在 VFX_Fire_* / VFX_Smoke_Only 预制体根上。
// 设计文档：《第一阶段计划》任务 4——对外只暴露 intensity(0-1)、scale、smokeAmount、开关四个参数。
//
// 预制体内部结构（由 FireSetup 工厂生成）：Flame/Core 两个加色火焰层 + Smoke 烟层 + 暖色点光。
// 本组件按名字分类各粒子系统，运行时用强度参数缩放发射率与灯光；冻结用粒子 Pause
// （状态机暂停/解说/等待继续期间火焰与计时一起停——文档硬约束）。
// 视觉为程序化占位，待 Astra 交付正式 Shader Graph 火焰后整体替换预制体，本组件接口不变。
using UnityEngine;

public class FireVfx : MonoBehaviour
{
    [Header("参数（对外接口，按文档约定）")]
    [Tooltip("火势强度 0-1：驱动火焰发射率与灯光亮度")]
    [Range(0f, 1f)] [SerializeField] private float intensity = 1f;
    [Tooltip("整体缩放")]
    [SerializeField] private float scale = 1f;
    [Tooltip("烟量 0-1：驱动烟发射率")]
    [Range(0f, 1f)] [SerializeField] private float smokeAmount = 1f;

    private ParticleSystem[] flameSystems;   // 名字含 Flame / Core
    private ParticleSystem[] smokeSystems;   // 名字含 Smoke
    private float[] flameBaseRates;
    private float[] smokeBaseRates;
    private Light fireLight;
    private float baseLightIntensity;
    private bool frozen;
    private float flickerSeed;

    private void Awake()
    {
        var all = GetComponentsInChildren<ParticleSystem>(true);
        var flames = new System.Collections.Generic.List<ParticleSystem>();
        var smokes = new System.Collections.Generic.List<ParticleSystem>();
        foreach (ParticleSystem ps in all)
        {
            string n = ps.name;
            if (n.Contains("Smoke")) smokes.Add(ps);
            else flames.Add(ps);
        }
        flameSystems = flames.ToArray();
        smokeSystems = smokes.ToArray();
        flameBaseRates = new float[flameSystems.Length];
        smokeBaseRates = new float[smokeSystems.Length];
        for (int i = 0; i < flameSystems.Length; i++)
            flameBaseRates[i] = flameSystems[i].emission.rateOverTimeMultiplier;
        for (int i = 0; i < smokeSystems.Length; i++)
            smokeBaseRates[i] = smokeSystems[i].emission.rateOverTimeMultiplier;

        fireLight = GetComponentInChildren<Light>(true);
        if (fireLight != null) baseLightIntensity = fireLight.intensity;
        flickerSeed = Random.value * 10f;
        Apply();
    }

    /// <summary>火势强度 0-1</summary>
    public void SetIntensity(float value)
    {
        intensity = Mathf.Clamp01(value);
        Apply();
    }

    /// <summary>烟量 0-1</summary>
    public void SetSmokeAmount(float value)
    {
        smokeAmount = Mathf.Clamp01(value);
        Apply();
    }

    /// <summary>整体缩放</summary>
    public void SetScale(float value)
    {
        scale = Mathf.Max(0.01f, value);
        transform.localScale = Vector3.one * scale;
    }

    /// <summary>随状态机冻结：粒子 Pause、灯光停止闪烁（火势不随时间自主演变）</summary>
    public void Freeze(bool value)
    {
        frozen = value;
        foreach (ParticleSystem ps in flameSystems) { if (value) ps.Pause(true); else ps.Play(); }
        foreach (ParticleSystem ps in smokeSystems) { if (value) ps.Pause(true); else ps.Play(); }
        if (!value) Apply();
    }

    private void Apply()
    {
        for (int i = 0; i < flameSystems.Length; i++)
        {
            var em = flameSystems[i].emission;
            em.rateOverTimeMultiplier = flameBaseRates[i] * Mathf.Max(intensity, 0.05f);
        }
        for (int i = 0; i < smokeSystems.Length; i++)
        {
            var em = smokeSystems[i].emission;
            em.rateOverTimeMultiplier = smokeBaseRates[i] * smokeAmount;
        }
    }

    private void Update()
    {
        // 灯光亮度 = 基准 × 强度 × 闪烁（冻结时静止）
        if (fireLight == null) return;
        if (frozen) return;
        float flicker = 0.85f + 0.3f * (0.5f + 0.5f * Mathf.Sin(Time.time * 11f + flickerSeed)
                                        * Mathf.Sin(Time.time * 4.7f + flickerSeed * 2f));
        fireLight.intensity = baseLightIntensity * Mathf.Max(intensity, 0.05f) * flicker;
    }
}
