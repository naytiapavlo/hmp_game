// 灭火器喷射装配——生成喷射粒子资产（贴图/材质/prefab，全部程序化：零外部素材、零版权风险），
// 并把 ExtinguisherSpray（灭火器）与 FireSuppression（起火点）接进第三场景。
//
// 成品（遵循 VFX 目录命名约定）：
//   Assets/VFX/T_SprayParticle.png          128×128 径向渐变白雾贴图（Clamp、无 mipmap）
//   Assets/VFX/MAT_ExtinguisherSpray.mat    HMProtection/ParticleFx 透明混合，米白偏黄（ABC 干粉）
//   Assets/VFX/VFX_ExtinguisherSpray.prefab 锥形干粉雾（世界空间模拟、≤400 粒子——项目规格 ≤500）
//
// 场景接线（仅第三场景，幂等）：SM_Extinguisher_01 挂 ExtinguisherSpray（喷嘴按包围盒估算、
//   prefab 引用强写）；「起火点」挂 FireSuppression。已挂组件只刷新关键序列化值
//   （脚本默认值陷阱：已入场景的组件不吃代码新默认值——先例 InteractionSetup 刷 followFactor）。
//
// 幂等：资产缺失才创建（材质参数/prefab 参数每次重跑都刷新，GUID 稳定不丢引用）；
//       Library/ExtinguisherSpraySetup.v3.done 防重复执行；菜单可强制重跑。
//       v1→v2：接线 GPT 语义化模型的 Nozzle 精确喷嘴挂点（旧标记作废自动重跑）。
//       v2→v3：喷射粒子加重（密度翻倍/锥角收窄/粒径与透明度上调）。
//       v4 标记沿用；喷口现统一使用独立持握的 Nozzle，
//              由 ExtinguisherCarryRig 调整喷嘴与软管，保持真实出粉位置。
//       前置缺失（第三场景/灭火器未部署）不落标记，到位后自动补跑。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class ExtinguisherSpraySetup
{
    private const string TexPath = "Assets/VFX/T_SprayParticle.png";
    private const string MatPath = "Assets/VFX/MAT_ExtinguisherSpray.mat";
    private const string PrefabPath = "Assets/VFX/VFX_ExtinguisherSpray.prefab";
    private const string TargetScenePath = "Assets/Scenes/第三场景.unity";
    private const string PropName = "SM_Extinguisher_01";
    private const string FireRootName = "起火点";

    private const string Marker = "Library/ExtinguisherSpraySetup.v4.done";
    private const string ReportPath = "Library/ExtinguisherSpraySetup.txt";

    static ExtinguisherSpraySetup()
    {
        EditorApplication.delayCall += Auto;
    }

    private static void Auto()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += Auto;
            return;
        }
        if (EditorApplication.isPlaying)
        {
            EditorApplication.delayCall += Auto;
            return;
        }
        if (File.Exists(Marker)) return;
        Build();
    }

    [MenuItem("Tools/Props/部署灭火器喷射（粒子 + 压制接线）")]
    public static void ForceRun()
    {
        if (File.Exists(Marker)) File.Delete(Marker);
        Build();
    }

    private static void Build()
    {
        var log = new List<string>();
        log.Add("== 灭火器喷射装配 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==");

        try
        {
            Texture2D tex = EnsureTexture(log);
            Material mat = EnsureMaterial(tex, log);
            GameObject prefab = EnsurePrefab(mat, log);
            bool wired = WireThirdScene(prefab, log);

            log.Add(wired ? "RESULT OK" : "RESULT PENDING（场景接线未完成，不落标记，前置到位后自动补跑）");
            if (wired) File.WriteAllText(Marker, "done " + DateTime.Now + "\n");
        }
        catch (Exception e)
        {
            log.Add("FAIL: " + e);
            Debug.LogError("[ExtinguisherSpraySetup] " + e);
        }

        File.WriteAllText(ReportPath, string.Join("\n", log) + "\n");
        Debug.Log("[ExtinguisherSpraySetup] 完成，报告：Library/ExtinguisherSpraySetup.txt\n"
                  + string.Join("\n", log));
    }

    // ==== 资产 ====

    /// <summary>径向渐变白雾贴图（缺失才生成）</summary>
    private static Texture2D EnsureTexture(List<string> log)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
        if (existing != null)
        {
            log.Add("贴图已存在：" + TexPath);
            return existing;
        }

        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size - 0.5f;
                float dy = (y + 0.5f) / size - 0.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) * 2f;         // 0 中心 → 1 边缘
                float a = Mathf.Pow(Mathf.Clamp01(1f - d), 1.6f);     // 软边衰减
                pixels[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        File.WriteAllBytes(TexPath, tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);   // 静态类里裸调解析不到，必须全限定
        AssetDatabase.ImportAsset(TexPath);

        var importer = (TextureImporter)AssetImporter.GetAtPath(TexPath);
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.SaveAndReimport();

        log.Add("生成贴图：" + TexPath + "（128×128 径向渐变）");
        return AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
    }

    /// <summary>喷射材质（缺失创建，重跑刷新参数——换色只需改这里的 TintColor）</summary>
    private static Material EnsureMaterial(Texture2D tex, List<string> log)
    {
        Shader shader = Shader.Find("HMProtection/ParticleFx");
        if (shader == null)
        {
            string[] guids = AssetDatabase.FindAssets("t:Shader Sh_ParticleFx");
            if (guids.Length > 0) shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }
        if (shader == null)
        {
            log.Add("FAIL: 找不到 HMProtection/ParticleFx（Assets/VFX/Sh_ParticleFx.shader）");
            throw new InvalidOperationException("缺少粒子着色器");
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MatPath);
            log.Add("生成材质：" + MatPath);
        }
        else log.Add("材质已存在，刷新参数：" + MatPath);

        // ABC 干粉：米白偏黄；_SrcMode=5(SrcAlpha) + _DstMode=10(OneMinusSrcAlpha) 普通透明混合
        mat.shader = shader;
        mat.SetTexture("_MainTex", tex);
        mat.SetColor("_TintColor", new Color(0.93f, 0.90f, 0.80f, 1f));
        mat.SetFloat("_SrcMode", 5f);
        mat.SetFloat("_DstMode", 10f);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    /// <summary>喷射粒子 prefab（缺失创建骨架；参数每次重跑都刷新——LoadPrefabContents 保 GUID）</summary>
    private static GameObject EnsurePrefab(Material mat, List<string> log)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            var tmp = new GameObject("VFX_ExtinguisherSpray");
            tmp.AddComponent<ParticleSystem>();
            PrefabUtility.SaveAsPrefabAsset(tmp, PrefabPath);
            UnityEngine.Object.DestroyImmediate(tmp);
            log.Add("生成 prefab 骨架：" + PrefabPath);
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var ps = root.GetComponent<ParticleSystem>();
            if (ps == null) ps = root.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.55f, 0.85f);   // 加重：粉雾滞留更久更厚
            main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.07f);               // 加重：粉粒更大
            main.gravityModifier = 0.35f;                              // 粉雾轻微下坠
            main.simulationSpace = ParticleSystemSimulationSpace.World; // 转身时粉雾不跟着甩
            main.maxParticles = 480;                                   // 项目规格：单实例 ≤500

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(220f);    // 加重：密度翻倍

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 9f;                                          // 加重：锥角收窄，同等数量聚得更浓
            shape.radius = 0.015f;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            // 注意：本版本 MinMaxCurve 没有 (AnimationCurve) 单参构造，用 (multiplier, curve) 双参
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.09f, 0f, 2f), new Keyframe(1f, 0.55f)));  // 出口细、远处散开

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.95f, 0.10f),
                    new GradientAlphaKey(0.75f, 0.55f),
                    new GradientAlphaKey(0f, 1f),
                });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var rend = root.GetComponent<ParticleSystemRenderer>();
            rend.sharedMaterial = mat;

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            log.Add("prefab 参数已写入（Cone 9° / 220 每秒 / 世界空间 / max 480——加重版）");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
    }

    // ==== 场景接线 ====

    private static bool WireThirdScene(GameObject prefab, List<string> log)
    {
        if (!File.Exists(TargetScenePath)) { log.Add("SKIP：找不到 " + TargetScenePath); return false; }

        bool openedByUs = false;
        Scene scene = SceneManager.GetSceneByPath(TargetScenePath);
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Additive);
            openedByUs = true;
        }
        try
        {
            Transform extinguisher = FindInScene(scene, PropName);
            if (extinguisher == null)
            {
                log.Add("SKIP：第三场景里没有 " + PropName + "（先跑 Tools/Props/部署灭火器）");
                return false;
            }
            Transform fireRoot = FindInScene(scene, FireRootName);
            if (fireRoot == null)
            {
                log.Add("SKIP：第三场景里没有「" + FireRootName + "」（先跑 Scene3Setup/FireSetup）");
                return false;
            }

            bool changed = false;

            // 1. 起火点挂 FireSuppression（压制状态机自己找同物体上的 FireEffectController）
            var supp = fireRoot.GetComponent<FireSuppression>();
            if (supp == null)
            {
                supp = fireRoot.gameObject.AddComponent<FireSuppression>();
                changed = true;
                log.Add("「" + FireRootName + "」已挂 FireSuppression");
            }
            else log.Add("「" + FireRootName + "」已有 FireSuppression");

            // 2. 灭火器挂 ExtinguisherSpray + 强写 prefab 引用与喷嘴位置（脚本默认值陷阱：刷新用）
            var spray = extinguisher.GetComponent<ExtinguisherSpray>();
            if (spray == null)
            {
                spray = extinguisher.gameObject.AddComponent<ExtinguisherSpray>();
                changed = true;
                log.Add(PropName + " 已挂 ExtinguisherSpray");
            }
            else log.Add(PropName + " 已有 ExtinguisherSpray，刷新接线");

            Bounds local = AggregateLocalBounds(extinguisher.gameObject);
            // 瓶头出粉口（默认模式）：包围盒顶部前侧推导。
            // 软管口锚点：独立持握的喷嘴出粉位置，
            // 运行时跟随左手并朝向瞄准方向。
            Vector3 headNozzle = new Vector3(0f, local.center.y + local.size.y * 0.42f,
                                             local.center.z + local.size.z * 0.55f + 0.02f);
            Transform hoseAnchor = FindDeep(extinguisher, "Nozzle");

            var so = new SerializedObject(spray);
            so.FindProperty("sprayVfxPrefab").objectReferenceValue = prefab;
            so.FindProperty("suppression").objectReferenceValue = supp;
            so.FindProperty("nozzleLocalPosition").vector3Value = headNozzle;
            so.FindProperty("hoseNozzle").objectReferenceValue = hoseAnchor;
            changed |= so.ApplyModifiedPropertiesWithoutUndo();   // 只刷新序列化值也要存场景，否则白写
            log.Add("接线：sprayVfxPrefab=" + prefab.name + "　suppression=「" + FireRootName + "」"
                    + "　Nozzle 挂点优先（旧模型兜底 " + headNozzle.ToString("F3") + "，包围盒 " + local.size.ToString("F3") + " 推导）"
                    + "　hoseNozzle 锚点=" + (hoseAnchor != null ? "已接线，由左手独立持握" : "模型无 Nozzle 挂点"));

            if (changed)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                bool saved = EditorSceneManager.SaveScene(scene);
                log.Add("第三场景已保存：" + saved);
            }
            else log.Add("场景无结构变化，不重存");
            return true;
        }
        finally
        {
            if (openedByUs) EditorSceneManager.CloseScene(scene, true);
        }
    }

    // ==== 部件探查（帮助人工辨认 11 个 tripo_part 里哪个是喷嘴/把手/保险销） ====

    [MenuItem("Tools/Props/检查灭火器部件包围盒")]
    public static void ProbeParts()
    {
        if (!File.Exists(TargetScenePath))
        {
            Debug.LogWarning("[ExtinguisherSpraySetup] 找不到 " + TargetScenePath);
            return;
        }
        bool openedByUs = false;
        Scene scene = SceneManager.GetSceneByPath(TargetScenePath);
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Additive);
            openedByUs = true;
        }
        try
        {
            Transform ext = FindInScene(scene, PropName);
            if (ext == null)
            {
                Debug.LogWarning("[ExtinguisherSpraySetup] 场景里没有 " + PropName);
                return;
            }

            var rows = new List<(float y, string line)>();
            foreach (Renderer r in ext.GetComponentsInChildren<Renderer>(true))
            {
                Bounds lb = ToLocalBounds(r, ext);
                string path = RelativePath(r.transform, ext);
                rows.Add((lb.center.y, path.PadRight(28)
                    + " 局部中心 " + lb.center.ToString("F3")
                    + " 局部尺寸 " + lb.size.ToString("F3")));
            }
            rows.Sort((a, b) => a.y.CompareTo(b.y));   // 按高度排：顶部件（喷嘴/把手）在前

            var sb = new StringBuilder();
            sb.AppendLine("== " + PropName + " 部件包围盒（按局部高度升序；顶部的多是喷嘴/提把，细长小件可能是保险销）==");
            sb.AppendLine("整体局部包围盒：" + AggregateLocalBounds(ext.gameObject).center.ToString("F3")
                          + " / " + AggregateLocalBounds(ext.gameObject).size.ToString("F3"));
            foreach (var row in rows) sb.AppendLine(row.line);

            string text = sb.ToString();
            File.WriteAllText("Library/ExtinguisherParts.txt", text);
            Debug.Log(text + "\n（已写 Library/ExtinguisherParts.txt——Astra 语义化命名后按此改 nozzleLocalPosition）");
        }
        finally
        {
            // 只读探查：不 MarkDirty、不保存
            if (openedByUs) EditorSceneManager.CloseScene(scene, true);
        }
    }

    /// <summary>把渲染体包围盒折算到目标物体的局部空间（子物体有旋转/缩放也正确）</summary>
    private static Bounds ToLocalBounds(Renderer r, Transform target)
    {
        Bounds world = r.bounds;
        Vector3 e = world.extents;
        Vector3 min = target.InverseTransformPoint(world.center - e);
        Vector3 max = target.InverseTransformPoint(world.center + e);
        // 旋转后的包围盒要取 8 个角点（上面 min/max 只是两个角，斜着放会算小）
        Vector3 c = world.center;
        var pts = new Vector3[8];
        int n = 0;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    pts[n++] = target.InverseTransformPoint(new Vector3(c.x + e.x * sx, c.y + e.y * sy, c.z + e.z * sz));
        min = max = pts[0];
        for (int i = 1; i < 8; i++) { min = Vector3.Min(min, pts[i]); max = Vector3.Max(max, pts[i]); }
        return new Bounds((min + max) * 0.5f, max - min);
    }

    private static string RelativePath(Transform t, Transform root)
    {
        var names = new Stack<string>();
        while (t != null && t != root) { names.Push(t.name); t = t.parent; }
        return string.Join("/", names.ToArray());
    }

    /// <summary>整棵子树的渲染包围盒折算到根局部空间（与 ExtinguisherSetup 同口径）</summary>
    private static Bounds AggregateLocalBounds(GameObject root)
    {
        var rends = root.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return new Bounds(Vector3.zero, Vector3.one * 0.1f);
        Bounds world = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) world.Encapsulate(rends[i].bounds);

        Transform t = root.transform;
        Vector3 c = world.center, e = world.extents;
        var pts = new Vector3[8];
        int n = 0;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    pts[n++] = new Vector3(c.x + e.x * sx, c.y + e.y * sy, c.z + e.z * sz);
        Vector3 min = t.InverseTransformPoint(pts[0]);
        Vector3 max = min;
        for (int i = 1; i < 8; i++)
        {
            Vector3 p = t.InverseTransformPoint(pts[i]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return new Bounds((min + max) * 0.5f, max - min);
    }

    // ==== 查找 ====

    private static Transform FindInScene(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform hit = FindDeep(root.transform, name);
            if (hit != null) return hit;
        }
        return null;
    }

    private static Transform FindDeep(Transform parent, string name)
    {
        if (parent.name == name) return parent;
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform hit = FindDeep(parent.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }
}
