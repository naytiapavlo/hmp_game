// One-shot fire VFX setup: builds the four level prefabs procedurally (VFX_Smoke_Only /
// VFX_Fire_Small / VFX_Fire_Medium / VFX_Fire_Large) with a realism-tuned particle stack —
// stretched billboards (velocity-aligned flame tongues), built-in Noise turbulence,
// ember sparks, teardrop flame texture with noise breakup, spinning soft smoke —
// per 第一阶段计划 任务 4 (placeholder visuals; Astra can replace prefabs wholesale,
// FireEffectController 接口不变). Also imports the aged overload-socket prop from
// 模型/办公室场景/, places it on a desk in level scenes (root "场景") and creates the
// 「起火点」anchor with FireEffectController wired to the prefabs (followTarget = socket).
// Writes Library/FireSetup.json; per-scene marker prevents re-runs; menu forces a re-run.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class FireSetup
{
    private const string ReportPath = "Library/FireSetup.json";
    private const string VfxFolder = "Assets/VFX";
    private const string SceneRootName = "场景";
    private const string IgnitionPointName = "起火点";
    private const string SocketAgedName = "SM_Hazard_OverloadSocket_01_Aged";
    private const string ShaderName = "HMProtection/ParticleFx";

    static FireSetup()
    {
        EditorApplication.delayCall += Run;
        EditorSceneManager.sceneOpened += (scene, mode) => Run();
    }

    private static string MarkerPathFor(Scene scene) => "Library/FireSetup." + scene.name + ".done";

    public static void Run()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(scene.name)) return;
        string markerPath = MarkerPathFor(scene);
        if (File.Exists(markerPath)) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += Run; return; }
        if (EditorApplication.isPlaying) { EditorApplication.delayCall += Run; return; }

        var log = new List<string>();
        bool processed = false;
        string error = null;
        try { processed = ProcessScene(scene, log); }
        catch (Exception e) { error = e.GetType().Name + ": " + e.Message; Debug.LogException(e); }

        try { WriteReport(scene, processed, error, log); }
        catch (Exception e) { Debug.LogException(e); }

        if (processed) File.WriteAllText(markerPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Debug.Log("FireSetup finished. scene=" + scene.name + " processed=" + processed
                  + " error=" + (error ?? "none") + " report=" + ReportPath);
    }

    [MenuItem("Tools/Fire/Re-run Fire Setup")]
    public static void ForceRerun()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!string.IsNullOrEmpty(scene.name))
        {
            string markerPath = MarkerPathFor(scene);
            if (File.Exists(markerPath)) File.Delete(markerPath);
        }
        Run();
    }

    private static bool ProcessScene(Scene scene, List<string> log)
    {
        // 0) 资产层（所有场景共用）：粒子贴图/材质/四个分级预制体
        var prefabs = EnsureVfxAssets(log);

        // 只有关卡场景（有「场景」内容根）才部署起火点
        Transform sceneRoot = FindRoot(scene, SceneRootName);
        if (sceneRoot == null)
        {
            log.Add("no root '" + SceneRootName + "' (非关卡场景，只构建资产)");
            return true;
        }

        // 1) 导入并摆放做旧插排（第一关起火点道具）
        Transform socketTransform = FindInScene(SocketAgedName);
        GameObject socket = socketTransform != null ? socketTransform.gameObject : null;
        if (socket == null)
        {
            GameObject asset = EnsurePropImported(SocketAgedName, log);
            if (asset != null)
            {
                socket = (GameObject)PrefabUtility.InstantiatePrefab(asset, sceneRoot);
                socket.name = SocketAgedName;
                PlaceOnDesk(socket, log);
            }
        }
        else log.Add("socket already in scene: " + SocketAgedName);

        // 2) 起火点锚点 + FireEffectController（火源由 followTarget 每帧贴插排顶面）
        Transform ignition = FindRoot(scene, IgnitionPointName);
        if (ignition == null)
        {
            var go = new GameObject(IgnitionPointName);
            SceneManager.MoveGameObjectToScene(go, scene);
            ignition = go.transform;
        }
        if (socket != null)
        {
            Bounds b = WorldBounds(socket);
            ignition.position = new Vector3(b.center.x, b.max.y + 0.02f, b.center.z);
        }

        var controller = ignition.GetComponent<FireEffectController>();
        if (controller == null) controller = ignition.gameObject.AddComponent<FireEffectController>();
        var so = new SerializedObject(controller);
        so.FindProperty("prefabSmokeOnly").objectReferenceValue = prefabs.smoke;
        so.FindProperty("prefabSmall").objectReferenceValue = prefabs.small;
        so.FindProperty("prefabMedium").objectReferenceValue = prefabs.medium;
        so.FindProperty("prefabLarge").objectReferenceValue = prefabs.large;
        // 火源跟随插排：挪道具火就跟着走，无需手动同步起火点
        if (socket != null) so.FindProperty("followTarget").objectReferenceValue = socket.transform;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveOpenScenes();

        // Bloom 辉光：CG 里火焰根部白亮发光的关键（场景 Global Volume 缺失时补挂）
        EnsureBloom(scene, log);
        log.Add("ignition point wired (follow socket, scene saved: " + saved + ")");
        return true;
    }

    // 给场景全局 Volume 配置 Bloom（火焰/火星的白亮辉光）
    private static void EnsureBloom(Scene scene, List<string> log)
    {
        Volume volume = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            volume = root.GetComponent<Volume>();
            if (volume != null && volume.isGlobal) break;
            volume = null;
        }
        if (volume == null || volume.profile == null)
        {
            log.Add("WARNING: no global Volume/profile in scene, Bloom skipped");
            return;
        }
        if (!volume.profile.TryGet<Bloom>(out Bloom bloom))
        {
            bloom = volume.profile.Add<Bloom>();
            log.Add("added Bloom override to scene volume");
        }
        if (!bloom.intensity.overrideState)
        {
            bloom.intensity.Override(0.7f);
            bloom.threshold.Override(0.85f);
            bloom.scatter.Override(0.6f);
            EditorUtility.SetDirty(volume.profile);
            AssetDatabase.SaveAssets();
            log.Add("Bloom configured (intensity 0.7, threshold 0.85)");
        }
    }

    // ==== 分级预制体工厂 ====
    // 真实感手段：拉伸公告板（火舌沿速度拉长）+ Noise 湍流舞动 + 火星层 +
    // 水滴形火焰贴图（噪声破碎）+ 自旋软烟。粒子预算守文档红线：单实例 ≤500。

    private struct FirePrefabs
    {
        public GameObject smoke, small, medium, large;
    }

    private struct FireTier
    {
        public string Name;
        public bool HasFlame;
        // 外焰
        public float FlameSizeMin, FlameSizeMax, FlameLifeMin, FlameLifeMax, FlameSpeed;
        public int FlameRate, FlameMax;
        // 内焰
        public float CoreSizeMin, CoreSizeMax;
        public int CoreRate, CoreMax;
        // 火星
        public float EmberSizeMin, EmberSizeMax, EmberSpeed;
        public int EmberRate, EmberMax;
        // 烟
        public float SmokeSizeMin, SmokeSizeMax, SmokeLifeMin, SmokeLifeMax, SmokeSpeed;
        public int SmokeRate, SmokeMax;
        public Color SmokeColor;
        public float LightIntensity, LightRange;

        public int TotalParticles => (HasFlame ? FlameMax + CoreMax + EmberMax : 0) + SmokeMax;
    }

    private static FirePrefabs EnsureVfxAssets(List<string> log)
    {
        Directory.CreateDirectory(VfxFolder);

        // 贴图：水滴形火焰（下宽上尖 + 噪声破碎）/ 软烟团（噪声云状）
        Texture2D flameTex = EnsureFireTexture(VfxFolder + "/T_FireParticle.png", true, log);
        Texture2D smokeTex = EnsureFireTexture(VfxFolder + "/T_SmokeParticle.png", false, log);

        Material flameMat = EnsureMaterial(VfxFolder + "/MAT_FireFlame.mat", flameTex, 1f, log);
        Material smokeMat = EnsureMaterial(VfxFolder + "/MAT_FireSmoke.mat", smokeTex, 10f, log);

        var tiers = new[]
        {
            new FireTier { Name = "VFX_Smoke_Only", HasFlame = false,
                SmokeSizeMin = 0.14f, SmokeSizeMax = 0.30f, SmokeLifeMin = 1.8f, SmokeLifeMax = 3.0f, SmokeSpeed = 0.28f,
                SmokeRate = 10, SmokeMax = 50, SmokeColor = new Color(0.88f, 0.88f, 0.9f),
                LightIntensity = 0.25f, LightRange = 2f },
            // 初起火：文档口径 0.2-0.4m，对齐 CG 截图的根部白亮+细长火舌
            new FireTier { Name = "VFX_Fire_Small", HasFlame = true,
                FlameSizeMin = 0.20f, FlameSizeMax = 0.38f, FlameLifeMin = 0.4f, FlameLifeMax = 0.68f, FlameSpeed = 0.7f,
                FlameRate = 34, FlameMax = 120, CoreSizeMin = 0.09f, CoreSizeMax = 0.16f, CoreRate = 24, CoreMax = 80,
                EmberSizeMin = 0.02f, EmberSizeMax = 0.035f, EmberSpeed = 1.5f, EmberRate = 9, EmberMax = 28,
                SmokeSizeMin = 0.35f, SmokeSizeMax = 0.7f, SmokeLifeMin = 1.8f, SmokeLifeMax = 3.0f, SmokeSpeed = 0.42f,
                SmokeRate = 12, SmokeMax = 60, SmokeColor = new Color(0.1f, 0.095f, 0.09f),
                LightIntensity = 2.0f, LightRange = 3.5f },
            // 中火：CG 主表现档——大而密的火舌 + 浓黑烟柱 + 强暖光
            new FireTier { Name = "VFX_Fire_Medium", HasFlame = true,
                FlameSizeMin = 0.45f, FlameSizeMax = 0.75f, FlameLifeMin = 0.5f, FlameLifeMax = 0.8f, FlameSpeed = 0.9f,
                FlameRate = 60, FlameMax = 200, CoreSizeMin = 0.2f, CoreSizeMax = 0.34f, CoreRate = 44, CoreMax = 130,
                EmberSizeMin = 0.03f, EmberSizeMax = 0.05f, EmberSpeed = 1.9f, EmberRate = 16, EmberMax = 44,
                SmokeSizeMin = 0.6f, SmokeSizeMax = 1.15f, SmokeLifeMin = 2.2f, SmokeLifeMax = 3.6f, SmokeSpeed = 0.5f,
                SmokeRate = 24, SmokeMax = 90, SmokeColor = new Color(0.07f, 0.065f, 0.06f),
                LightIntensity = 4.5f, LightRange = 6f },
            new FireTier { Name = "VFX_Fire_Large", HasFlame = true,
                FlameSizeMin = 0.9f, FlameSizeMax = 1.4f, FlameLifeMin = 0.6f, FlameLifeMax = 0.95f, FlameSpeed = 1.15f,
                FlameRate = 100, FlameMax = 230, CoreSizeMin = 0.4f, CoreSizeMax = 0.65f, CoreRate = 60, CoreMax = 120,
                EmberSizeMin = 0.04f, EmberSizeMax = 0.065f, EmberSpeed = 2.3f, EmberRate = 22, EmberMax = 56,
                SmokeSizeMin = 1.1f, SmokeSizeMax = 1.9f, SmokeLifeMin = 2.8f, SmokeLifeMax = 4.5f, SmokeSpeed = 0.6f,
                SmokeRate = 42, SmokeMax = 90, SmokeColor = new Color(0.05f, 0.048f, 0.045f),
                LightIntensity = 7f, LightRange = 9f },
        };

        var result = new FirePrefabs();
        foreach (FireTier tier in tiers)
        {
            string path = VfxFolder + "/" + tier.Name + ".prefab";
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            GameObject prefab = existing != null ? existing : BuildFirePrefab(tier, path, flameMat, smokeMat);
            switch (tier.Name)
            {
                case "VFX_Smoke_Only": result.smoke = prefab; break;
                case "VFX_Fire_Small": result.small = prefab; break;
                case "VFX_Fire_Medium": result.medium = prefab; break;
                case "VFX_Fire_Large": result.large = prefab; break;
            }
            log.Add("prefab ready: " + tier.Name + " (粒子上限 " + tier.TotalParticles + ")");
        }
        return result;
    }

    private static GameObject BuildFirePrefab(FireTier tier, string path, Material flameMat, Material smokeMat)
    {
        var root = new GameObject(tier.Name);

        if (tier.HasFlame)
        {
            // 外焰（橙红，沿速度拉长的舔动火舌——CG 截图的细长火舌形态）
            CreateParticle(root.transform, "Flame", flameMat,
                tier.FlameSizeMin, tier.FlameSizeMax, tier.FlameLifeMin, tier.FlameLifeMax, tier.FlameSpeed,
                tier.FlameRate, tier.FlameMax, 0.05f, FlameGradient(), GrowShrinkCurve(),
                stretch: 3.2f, noiseStrength: 0.3f, gravity: -0.06f);
            // 内焰（白黄热核，更快更短——CG 根部的白亮区）
            CreateParticle(root.transform, "Core", flameMat,
                tier.CoreSizeMin, tier.CoreSizeMax, tier.FlameLifeMin * 0.75f, tier.FlameLifeMax * 0.75f,
                tier.FlameSpeed * 0.85f, tier.CoreRate, tier.CoreMax, 0.03f, CoreGradient(), GrowShrinkCurve(),
                stretch: 2.4f, noiseStrength: 0.2f, gravity: -0.05f);
            // 火星（细小亮点快速上飞，长条拉伸）
            CreateParticle(root.transform, "Embers", flameMat,
                tier.EmberSizeMin, tier.EmberSizeMax, 0.5f, 0.9f, tier.EmberSpeed,
                tier.EmberRate, tier.EmberMax, 0.04f, EmberGradient(), FadeOutCurve(),
                stretch: 7f, noiseStrength: 0.35f, gravity: 0.15f);
        }
        // 烟（慢升、自旋、云状噪声——CG 的浓黑烟柱）
        CreateParticle(root.transform, "Smoke", smokeMat,
            tier.SmokeSizeMin, tier.SmokeSizeMax, tier.SmokeLifeMin, tier.SmokeLifeMax, tier.SmokeSpeed,
            tier.SmokeRate, tier.SmokeMax, 0.06f, SmokeGradient(tier.SmokeColor), SmokeGrowCurve(),
            stretch: 0f, noiseStrength: 0.45f, gravity: -0.03f, spin: true);

        // 双灯光：近处强暖光（闪烁）+ 大范围补光（照亮桌面与墙面，对齐 CG 的环境映色）
        var lightGo = new GameObject("Light");
        lightGo.transform.SetParent(root.transform, false);
        lightGo.transform.localPosition = new Vector3(0f, 0.18f, 0f);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.55f, 0.25f);
        light.intensity = tier.LightIntensity;
        light.range = tier.LightRange;
        light.shadows = LightShadows.None;

        var fillGo = new GameObject("LightFill");
        fillGo.transform.SetParent(root.transform, false);
        fillGo.transform.localPosition = new Vector3(0f, 0.5f, 0f);
        var fill = fillGo.AddComponent<Light>();
        fill.type = LightType.Point;
        fill.color = new Color(1f, 0.6f, 0.3f);
        fill.intensity = tier.LightIntensity * 0.3f;
        fill.range = tier.LightRange * 1.7f;
        fill.shadows = LightShadows.None;

        root.AddComponent<FireVfx>();
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        UnityEngine.Object.DestroyImmediate(root);
        return prefab;
    }

    // 生成一层粒子（stretch>0 = 沿速度拉伸的公告板；noiseStrength>0 = 内置湍流；spin = 烟自旋）
    private static ParticleSystem CreateParticle(Transform parent, string name, Material material,
        float sizeMin, float sizeMax, float lifeMin, float lifeMax, float speed,
        int rate, int maxParticles, float shapeRadius, Gradient color, AnimationCurve sizeCurve,
        float stretch, float noiseStrength, float gravity, bool spin = false)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifeMin, lifeMax);
        main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.6f, speed * 1.15f);
        main.maxParticles = maxParticles;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.gravityModifier = gravity;

        var emission = ps.emission;
        emission.rateOverTime = rate;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = shapeRadius;
        shape.radiusThickness = 1f;

        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(color);

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        // 内置湍流：火苗舞动 / 烟飘散的自然感来源
        if (noiseStrength > 0f)
        {
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(noiseStrength * 0.6f, noiseStrength);
            noise.frequency = 0.35f;
            noise.scrollSpeed = 0.18f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Medium;
        }

        if (spin)
        {
            var rotation = ps.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-0.5f, 0.5f);
        }

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        if (stretch > 0f)
        {
            // 拉伸公告板：粒子沿速度方向拉长，火焰呈现“火舌”而非圆斑
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = stretch;
        }
        else
        {
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }
        renderer.sharedMaterial = material;
        return ps;
    }

    // ==== 颜色/尺寸曲线 ====

    // 外焰：白黄根 → 橙 → 暗红消散（对应参考色带“白黄根部→橙色尖部”）
    private static Gradient FlameGradient()
    {
        return new Gradient
        {
            colorKeys = new[]
            {
                new GradientColorKey(new Color(1f, 0.92f, 0.62f), 0f),
                new GradientColorKey(new Color(1f, 0.5f, 0.1f), 0.45f),
                new GradientColorKey(new Color(0.68f, 0.12f, 0.015f), 0.8f),
            },
            alphaKeys = new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 0.15f),
                new GradientAlphaKey(0.9f, 0.55f),
                new GradientAlphaKey(0f, 1f),
            },
        };
    }

    private static Gradient CoreGradient()
    {
        return new Gradient
        {
            colorKeys = new[]
            {
                new GradientColorKey(new Color(1f, 1f, 0.9f), 0f),
                new GradientColorKey(new Color(1f, 0.85f, 0.42f), 0.6f),
                new GradientColorKey(new Color(1f, 0.55f, 0.1f), 1f),
            },
            alphaKeys = new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 0.2f),
                new GradientAlphaKey(0f, 1f),
            },
        };
    }

    // 火星：亮黄白 → 橙红，尾部熄灭
    private static Gradient EmberGradient()
    {
        return new Gradient
        {
            colorKeys = new[]
            {
                new GradientColorKey(new Color(1f, 0.95f, 0.7f), 0f),
                new GradientColorKey(new Color(1f, 0.45f, 0.08f), 0.7f),
            },
            alphaKeys = new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0.8f, 0.5f),
                new GradientAlphaKey(0f, 1f),
            },
        };
    }

    private static Gradient SmokeGradient(Color baseColor)
    {
        return new Gradient
        {
            colorKeys = new[]
            {
                new GradientColorKey(baseColor, 0f),
                new GradientColorKey(baseColor * 0.75f, 1f),
            },
            alphaKeys = new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(0.5f, 0.3f),
                new GradientAlphaKey(0f, 1f),
            },
        };
    }

    private static AnimationCurve GrowShrinkCurve() =>
        new AnimationCurve(new Keyframe(0f, 0.3f), new Keyframe(0.22f, 1f), new Keyframe(1f, 0.08f));

    private static AnimationCurve FadeOutCurve() =>
        new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.5f, 0.8f), new Keyframe(1f, 0f));

    private static AnimationCurve SmokeGrowCurve() =>
        new AnimationCurve(new Keyframe(0f, 0.35f), new Keyframe(0.35f, 1f), new Keyframe(1f, 0.55f));

    // ==== 贴图生成 ====
    // 火焰：下宽上尖的水滴形 × 径向衰减 × 双线性值噪声破碎；
    // 烟：软圆团 × 更大尺度的噪声云化。
    private static Texture2D EnsureFireTexture(string path, bool flameShape, List<string> log)
    {
        Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (existing != null) return existing;

        const int size = 128;
        var rng = new System.Random(12345);
        int grid = flameShape ? 16 : 6;                       // 火焰细碎噪声 / 烟大团噪声
        var noiseGrid = new float[(grid + 1) * (grid + 1)];
        for (int i = 0; i < noiseGrid.Length; i++) noiseGrid[i] = (float)rng.NextDouble();

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            float v = y / (float)(size - 1);                  // 0=底 1=顶
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)(size - 1);

                float alpha;
                if (flameShape)
                {
                    // 水滴形：最宽处约在 v=0.35，向上收尖成火舌
                    float vv = Mathf.Clamp01((v - 0.35f) / 0.65f);
                    float halfWidth = Mathf.Lerp(0.42f, 0.08f, Mathf.Pow(vv, 0.8f));
                    float dx = (u - 0.5f) / halfWidth;
                    float dy = (v - 0.35f) / 0.65f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    alpha = Mathf.Pow(Mathf.Clamp01(1f - d), 1.5f);
                }
                else
                {
                    float dx = u - 0.5f;
                    float dy = v - 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                    alpha = Mathf.Pow(Mathf.Clamp01(1f - d), 1.1f);
                }

                // 双线性值噪声（平铺），给边缘做破碎
                float gx = u * grid;
                float gy = v * grid;
                int x0 = Mathf.FloorToInt(gx), y0 = Mathf.FloorToInt(gy);
                float fx = gx - x0, fy = gy - y0;
                float n00 = noiseGrid[(y0 % (grid + 1)) * (grid + 1) + (x0 % (grid + 1))];
                float n10 = noiseGrid[(y0 % (grid + 1)) * (grid + 1) + ((x0 + 1) % (grid + 1))];
                float n01 = noiseGrid[((y0 + 1) % (grid + 1)) * (grid + 1) + (x0 % (grid + 1))];
                float n11 = noiseGrid[((y0 + 1) % (grid + 1)) * (grid + 1) + ((x0 + 1) % (grid + 1))];
                float noise = Mathf.Lerp(Mathf.Lerp(n00, n10, fx), Mathf.Lerp(n01, n11, fx), fy);

                alpha *= flameShape ? (0.68f + 0.32f * noise) : (0.55f + 0.45f * noise);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply(false, false);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
        log.Add("created texture " + path);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static Material EnsureMaterial(string path, Texture2D texture, float dstBlend, List<string> log)
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            log.Add("WARNING: shader " + ShaderName + " not found");
            return null;
        }
        var mat = new Material(shader);
        mat.SetTexture("_MainTex", texture);
        mat.SetFloat("_SrcMode", 5f);           // SrcAlpha
        mat.SetFloat("_DstMode", dstBlend);     // 1=One 加色；10=OneMinusSrcAlpha 普通
        AssetDatabase.CreateAsset(mat, path);
        log.Add("created material " + path);
        return mat;
    }

    // ==== 道具导入与摆放 ====

    private static GameObject EnsurePropImported(string fileName, List<string> log)
    {
        GameObject existing = FindModelAsset(fileName);
        if (existing != null) return existing;

        string repoModelDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "模型", "办公室场景"));
        string src = Path.Combine(repoModelDir, fileName + ".fbx");
        if (!File.Exists(src))
        {
            log.Add("WARNING: prop source not found: " + src);
            return null;
        }
        Directory.CreateDirectory("Assets/Props/Models");
        File.Copy(src, "Assets/Props/Models/" + fileName + ".fbx", true);
        AssetDatabase.ImportAsset("Assets/Props/Models/" + fileName + ".fbx");
        log.Add("imported prop " + fileName);
        return FindModelAsset(fileName);
    }

    private static GameObject FindModelAsset(string fileName)
    {
        string[] guids = AssetDatabase.FindAssets(fileName + " t:Model");
        if (guids.Length == 0) return null;
        return AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guids[0])) as GameObject;
    }

    // 摆到 SM_Desk_01 桌面（文档未定具体工位；位置可拖动插排调整，火源自动跟随）
    private static void PlaceOnDesk(GameObject socket, List<string> log)
    {
        Transform desk = FindInScene("SM_Desk_01");
        Renderer deskRenderer = desk != null ? desk.GetComponent<Renderer>() : null;
        if (deskRenderer == null)
        {
            socket.transform.position = new Vector3(0f, 1f, 0f);
            log.Add("WARNING: SM_Desk_01 not found, socket placed at origin");
            return;
        }
        Bounds b = deskRenderer.bounds;
        socket.transform.rotation = Quaternion.identity;
        socket.transform.position = new Vector3(b.center.x + b.extents.x * 0.3f, b.max.y + 0.005f, b.center.z - b.extents.z * 0.15f);
        log.Add("socket placed on " + desk.name + " @" + socket.transform.position.ToString("F2"));
    }

    // ==== 工具 ====

    private static Transform FindRoot(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            if (root.name == name) return root.transform;
        return null;
    }

    private static Transform FindInScene(string name)
    {
        foreach (GameObject sceneRoot in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            foreach (Transform t in sceneRoot.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
        }
        return null;
    }

    private static Bounds WorldBounds(GameObject go)
    {
        Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) bounds.Encapsulate(r.bounds);
        return bounds;
    }

    private static string J(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 32) sb.Append(' ');
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static void WriteReport(Scene scene, bool processed, string error, List<string> log)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"unityVersion\": ").Append(J(Application.unityVersion)).Append(",\n");
        sb.Append("  \"scene\": ").Append(J(scene.name)).Append(",\n");
        sb.Append("  \"processed\": ").Append(processed ? "true" : "false").Append(",\n");
        sb.Append("  \"error\": ").Append(J(error)).Append(",\n");
        sb.Append("  \"log\": [\n    ");
        var quoted = new List<string>(log.Count);
        foreach (string s in log) quoted.Add(J(s));
        sb.Append(string.Join(",\n    ", quoted.ToArray()));
        sb.Append("\n  ]\n}\n");

        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
    }
}
