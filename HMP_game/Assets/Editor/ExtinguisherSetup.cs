// 灭火器道具装配——把 模型/办公室场景/ 里的交付件导入 Assets/Props/，生成 URP 材质并重映射到模型，
// 校正到真实尺寸（高 0.5m），再作为「双手可抓取道具」部署进第三场景。
//
// 源件（仓库内，交付件）：
//   模型/办公室场景/SM_Extinguisher_01.fbx
//   模型/办公室场景/灭火器贴图/T_Extinguisher_01_PartNN_BaseColor.jpg
// 成品（Unity 侧，遵循 SM_/MAT_/T_ 命名与 Props 目录约定）：
//   Assets/Props/Models/SM_Extinguisher_01.fbx        （File.Copy 保留 .meta → GUID 稳定，换模不丢引用）
//   Assets/Props/Textures/T_Extinguisher_01_PartNN_BaseColor.jpg
//   Assets/Props/Materials/MAT_Extinguisher_01_PartNN.mat （URP Lit；按材质名里的 part 序号配对贴图）
//
// 尺寸校正：交付件若被工具归一化（如 Tripo 的包围盒最大边 = 1.0），按实测包围盒算出 globalScale，
//           使整体高度 = TargetHeightM；已接近目标则不动。每次重跑都会重新校正。
//
// 部署（仅第三场景）：挂在内容根「场景」下，落在 SM_Desk_02 旁的空地（离火源更远的一侧，
//           让玩家必须走过去取），底部贴地面；接线 PickupItem(HandleAndNozzle) + BoxCollider + Rigidbody
//           + Interactable 层——与 InteractionSetup.SetupPickups 的做法一致。
//
// 持握字段的唯一归属（重要，防两个装配工具互相覆盖）：PickupItem 上的 carryMode /
//   holdAlignsToView / holdPitchSyncsArms / holdPitchFactor / holdPositionOffset /
//   holdRotationOffset / interactPromptOverride，以及 HandPoseController 上 Pose.Extinguisher 的
//   8 组臂角/腕角/指节，**都只由本工具写**；ExtinguisherSpraySetup 只管喷射粒子资产与
//   ExtinguisherSpray / FireSuppression 的接线，InteractionSetup 只登记灭火器名字，都不碰这些。
//   （两个 [InitializeOnLoad] 工具的执行顺序没有保证，"谁后跑谁生效"会变成随机结果。）
//   v2：carryMode = HandleAndNozzle（右拳拎提把、左手握喷头），俯仰改为与手臂支点严格同幅。
//   v3：持握点改用**几何包围盒**求提把/压把中点（节点 transform 全在原点，按它反推会得到 (0,0,0)，
//       瓶身"穿"在手腕上、手臂看不见了），并顺带刷新 Pose.Extinguisher 臂角（脚本默认值陷阱）。
//   v4：臂角改为**离线实测值**（Blender 扫骨骼 + 掌面抓握点公式）：右手前臂 +15°、左手 +25°，
//       两点间距正好等于模型刚性距离 0.398m、高度齐平、比 Carry 基线高 0.20m。
//
// 幂等：文件按需复制/覆盖；材质、碰撞体、组件缺失才创建；已存在同名实例则只刷新接线与位置。
//       Library/ExtinguisherSetup.v4.done 防重复执行；菜单可强制重跑（换了模型文件后请重跑）。
// 不写标记的情形：源 FBX 尚不存在（等低模交付）——此时不落标记，模型到位后自动补跑。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class ExtinguisherSetup
{
    private const string PropName = "SM_Extinguisher_01";
    private const string SourceModelRel = "模型/办公室场景/SM_Extinguisher_01.fbx";
    private const string SourceTexRel = "模型/办公室场景/灭火器贴图";
    private const string AssetFbxPath = "Assets/Props/Models/SM_Extinguisher_01.fbx";
    private const string AssetTexFolder = "Assets/Props/Textures";
    private const string AssetMatFolder = "Assets/Props/Materials";
    private const string TargetScenePath = "Assets/Scenes/第三场景.unity";
    private const string ContentRootName = "场景";
    private const string DeskAName = "SM_Desk_01";
    private const string DeskBName = "SM_Desk_02";
    private const string FireRootName = "起火点";
    private const string InteractableLayerName = "Interactable";

    /// <summary>目标高度（米）：5kg 手提干粉灭火器含提把/阀门约 0.5m——建模规范要求真实尺寸</summary>
    private const float TargetHeightM = 0.5f;

    /// <summary>模型语义节点名（Astra 语义化命名的部件）：右手抓握的**两个红色手柄**取提把与压把，
    /// 左手抓握取喷嘴本体（喇叭口）；喷射口空挂点 Nozzle 只作为喷射原点用。
    /// 注意：这些部件的 transform 都在原点，几何烘在网格里——位置一律经 PropGeometry 按包围盒取。</summary>
    private const string CarryHandleNodeName = "CarryHandle";
    private const string SqueezeLeverNodeName = "SqueezeLever";
    private const string NozzleBodyNodeName = "NozzleBody";
    private const string NozzleNodeName = "Nozzle";

    /// <summary>灭火器持握的**朝向**偏移初值（度）。位置不用人给：由 CarryHandle/SqueezeLever 几何中点反推。
    /// 运行时由 ExtinguisherCarryRig 保持瓶身直立，喷嘴独立对齐左手。</summary>
    private static readonly Vector3 HoldRotationOffset = Vector3.zero;

    // ---- Pose.Extinguisher 的臂角/腕角/指节（度）。本工具独占写入 ----
    // 为什么工具要写：这些是 HandPoseController 上的 [SerializeField]，组件已经进了场景，
    //   **改脚本里的初始值对已存在的组件无效**（"脚本默认值陷阱"，项目已用 followFactor 先例验证过）。
    //
    // 左右手各自持握：瓶身由右手保持直立，喷嘴由左手控制；软管不再视为刚体。
    private static readonly Vector3 ExtRightForearm = new Vector3(-18f, 0f, 0f);
    private static readonly Vector3 ExtRightWrist = new Vector3(-4f, -8f, -14f);
    private static readonly Vector3 ExtRightFingers = new Vector3(-30f, -45f, -25f);
    private static readonly Vector3 ExtRightThumb = new Vector3(-14f, -16f, -8f);
    private static readonly Vector3 ExtLeftForearm = new Vector3(-18f, 0f, 0f);
    private static readonly Vector3 ExtLeftWrist = new Vector3(-18f, 18f, -8f);
    private static readonly Vector3 ExtLeftFingers = new Vector3(-24f, -38f, -20f);
    private static readonly Vector3 ExtLeftThumb = new Vector3(-12f, -14f, -6f);

    private const string Marker = "Library/ExtinguisherSetup.v4.done";
    private const string ReportPath = "Library/ExtinguisherSetup.txt";

    private static int importAttempts;
    private static int materialAttempts;

    static ExtinguisherSetup()
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

    [MenuItem("Tools/Props/部署灭火器（导入 + 进第三场景）")]
    public static void ForceRun()
    {
        if (File.Exists(Marker)) File.Delete(Marker);
        importAttempts = 0;
        Build();
    }

    [MenuItem("Tools/Props/检查灭火器装配")]
    public static void Inspect()
    {
        var sb = new StringBuilder();
        sb.AppendLine("源 FBX: " + SourceModelRel + " exists=" + File.Exists(SourcePath(SourceModelRel)));
        sb.AppendLine("成品 FBX: " + AssetFbxPath + " exists=" + File.Exists(AssetFbxPath));
        var importer = AssetImporter.GetAtPath(AssetFbxPath) as ModelImporter;
        if (importer != null)
            sb.AppendLine("globalScale=" + importer.globalScale + " useFileScale=" + importer.useFileScale);
        if (File.Exists(AssetFbxPath))
        {
            var model = AssetDatabase.LoadMainAssetAtPath(AssetFbxPath) as GameObject;
            sb.AppendLine("模型资产: " + (model != null ? "已加载" : "**加载失败**"));
            int mats = AssetDatabase.LoadAllAssetsAtPath(AssetFbxPath).OfType<Material>().Count();
            sb.AppendLine("模型内材质数: " + mats);
        }
        sb.AppendLine("Interactable 层: " + LayerMask.NameToLayer(InteractableLayerName));
        sb.AppendLine("第三场景: " + File.Exists(TargetScenePath));
        sb.AppendLine("标记: " + Marker + " exists=" + File.Exists(Marker));

        if (File.Exists(TargetScenePath))
        {
            bool openedByUs = false;
            UnityEngine.SceneManagement.Scene scene =
                UnityEngine.SceneManagement.SceneManager.GetSceneByPath(TargetScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                scene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Additive);
                openedByUs = true;
            }
            try
            {
                Transform hit = FindDeepInScene(scene, PropName);
                sb.AppendLine("场景内 " + PropName + ": " + (hit != null
                    ? hit.position.ToString("F2") + " 世界尺寸=" + WorldBounds(hit.gameObject).size.ToString("F3")
                    : "MISSING"));
                if (hit != null)
                {
                    sb.AppendLine("  PickupItem=" + (hit.GetComponent<PickupItem>() != null)
                                  + " BoxCollider=" + (hit.GetComponent<BoxCollider>() != null)
                                  + " Rigidbody=" + (hit.GetComponent<Rigidbody>() != null)
                                  + " layer=" + LayerMask.LayerToName(hit.gameObject.layer));
                }
            }
            finally
            {
                if (openedByUs) EditorSceneManager.CloseScene(scene, true);
            }
        }

        File.WriteAllText("Library/ExtinguisherSetupInspect.txt", sb.ToString());
        Debug.Log("[灭火器] Inspect → Library/ExtinguisherSetupInspect.txt\n" + sb);
    }

    public static void Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        var log = new List<string>();
        string error = null;
        bool finished = false;
        try
        {
            finished = BuildInternal(log);
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            Debug.LogException(e);
        }

        try { WriteReport(log, error, finished); }
        catch (Exception e) { Debug.LogException(e); }

        if (finished && error == null)
        {
            Directory.CreateDirectory("Library");
            File.WriteAllText(Marker, DateTime.UtcNow.ToString("o"));
        }

        var sb = new StringBuilder();
        foreach (string line in log) sb.AppendLine(line);
        Debug.Log("[灭火器] 装配 " + (error != null ? "失败：" + error : (finished ? "完成" : "未完成（见下）"))
                  + "\n" + sb + "报告：" + ReportPath);
    }

    /// <returns>本轮是否已真正跑完（决定要不要落标记）</returns>
    private static bool BuildInternal(List<string> log)
    {
        // 0) 源件检查：低模尚未交付时不落标记，等文件到位后自动补跑
        string srcFbx = SourcePath(SourceModelRel);
        if (!File.Exists(srcFbx))
        {
            log.Add("SKIP: 源件尚未就位 " + srcFbx);
            log.Add("      把低模放到该路径（文件名保持 SM_Extinguisher_01.fbx）后，工具会自动补跑；");
            return false;
        }

        // 1) 贴图：源目录 → Assets/Props/Textures
        Directory.CreateDirectory(AssetTexFolder);
        string srcTexDir = SourcePath(SourceTexRel);
        int texCopied = 0;
        if (Directory.Exists(srcTexDir))
        {
            foreach (string jpg in Directory.GetFiles(srcTexDir)
                .Where(p => p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            {
                string dst = AssetTexFolder + "/" + Path.GetFileName(jpg);
                if (NeedsCopy(jpg, dst)) { File.Copy(jpg, dst, true); texCopied++; }
                else if (!File.Exists(dst)) { File.Copy(jpg, dst, true); texCopied++; }
                // 关键顺序：新复制的文件在资产库注册前，AssetImporter.GetAtPath / LoadAssetAtPath 都是 null。
                // 必须先强制同步导入，再改导入设置；否则后面材质那步拿不到贴图，会生成没贴图的白模材质。
                AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);
                EnsureTextureSettings(dst);
            }
        }
        log.Add("贴图：" + texCopied + " 张复制/更新，其余已是最新");

        // 2) 模型：源 → Assets/Props/Models（File.Copy 保留 .meta，GUID 不变 → 换模不丢引用）
        Directory.CreateDirectory("Assets/Props/Models");
        bool modelCopied = NeedsCopy(srcFbx, AssetFbxPath);
        if (modelCopied) File.Copy(srcFbx, AssetFbxPath, true);
        AssetDatabase.ImportAsset(AssetFbxPath, ImportAssetOptions.ForceSynchronousImport);
        log.Add(modelCopied ? "模型文件已复制/覆盖：SM_Extinguisher_01.fbx" : "模型文件已是最新");

        // 3) 等导入完成（大模型导入是异步的，LoadAssetAtPath 可能先返回 null）
        var model = AssetDatabase.LoadMainAssetAtPath(AssetFbxPath) as GameObject;
        if (model == null)
        {
            importAttempts++;
            if (importAttempts < 60)
            {
                log.Add("模型仍在导入中（第 " + importAttempts + " 次等待）…");
                EditorApplication.delayCall += Build;
                return false;
            }
            log.Add("FAIL: 模型导入超时，未能加载 " + AssetFbxPath);
            return false;
        }
        importAttempts = 0;

        // 3.5) 导入设置：**不导相机/灯光**。模型若自带相机，Unity 会一并导入，而新建相机 depth 默认 0
        //      比玩家主相机的 -1 大 → 渲染在后、清屏覆盖，Play 后画面就被固定在这个相机上
        //      （第三场景曾因 FBX 里混进预览相机与两盏太阳灯而"相机被固定对着灭火器"）。
        var modelImporter = AssetImporter.GetAtPath(AssetFbxPath) as ModelImporter;
        if (modelImporter != null && (modelImporter.importCameras || modelImporter.importLights || !modelImporter.isReadable))
        {
            modelImporter.importCameras = false;
            modelImporter.importLights = false;
            modelImporter.isReadable = true; // Runtime hose deformation uses a private mesh copy.
            modelImporter.SaveAndReimport();
            log.Add("导入设置：importCameras/importLights -> false（防模型自带相机盖住玩家相机）");
        }

        // 4) 真实尺寸校正
        EnsureRealSize(log);

        // 5) 材质：按模型内材质名里的 part 序号配对贴图，建 URP Lit 材质并重映射
        if (!EnsureMaterials(log))
        {
            materialAttempts++;
            if (materialAttempts < 30)
            {
                log.Add("贴图/材质尚未就绪（第 " + materialAttempts + " 次等待），稍后重试…");
                EditorApplication.delayCall += Build;
                return false;
            }
            log.Add("FAIL: 贴图或材质始终未就绪，已中止（避免留下无贴图的白模材质）");
            return false;
        }
        materialAttempts = 0;

        // 6) 部署进第三场景
        DeployIntoThirdScene(log);
        return true;
    }

    // ==== 尺寸 ====

    private static void EnsureRealSize(List<string> log)
    {
        Vector3 size = MeasureModelSize(out bool ok);
        if (!ok)
        {
            log.Add("WARNING: 无法测量模型尺寸，跳过尺寸校正");
            return;
        }
        log.Add("模型实测尺寸（校正前）：" + size.ToString("F3") + " m");

        float height = size.y;                       // Unity 导入后 Y 向上
        if (height <= 0.0001f)
        {
            log.Add("WARNING: Y 向尺寸为 0，跳过尺寸校正");
            return;
        }
        if (Mathf.Abs(height - TargetHeightM) / TargetHeightM <= 0.03f)
        {
            log.Add("尺寸已接近目标高度 " + TargetHeightM + " m，不再缩放");
            return;
        }

        var importer = AssetImporter.GetAtPath(AssetFbxPath) as ModelImporter;
        if (importer == null)
        {
            log.Add("WARNING: 取不到 ModelImporter，跳过尺寸校正");
            return;
        }
        float factor = TargetHeightM / height;
        importer.globalScale *= factor;
        importer.SaveAndReimport();
        log.Add("尺寸校正：globalScale ×" + factor.ToString("F4") + " → " + importer.globalScale.ToString("F4"));

        Vector3 after = MeasureModelSize(out bool ok2);
        if (ok2)
        {
            log.Add("模型实测尺寸（校正后）：" + after.ToString("F3") + " m"
                    + (Mathf.Abs(after.y - TargetHeightM) / TargetHeightM <= 0.05f
                        ? "  ✓ 接近目标" : "  ⚠ 仍偏离目标，请人工确认轴向"));
            if (after.y < Mathf.Max(after.x, after.z) * 1.05f)
                log.Add("  提示：Y 不是最大边，模型可能不是竖直站姿（交付件轴向需确认）");
        }
    }

    /// <summary>把模型资产临时实例化到空场景里量包围盒（不留痕迹）</summary>
    private static Vector3 MeasureModelSize(out bool ok)
    {
        ok = false;
        var model = AssetDatabase.LoadMainAssetAtPath(AssetFbxPath) as GameObject;
        if (model == null) return Vector3.zero;

        UnityEngine.SceneManagement.Scene temp =
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        try
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, temp);
            if (instance == null) return Vector3.zero;
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            Bounds b = WorldBounds(instance);
            ok = b.size.sqrMagnitude > 0f;
            return b.size;
        }
        finally
        {
            EditorSceneManager.CloseScene(temp, true);
        }
    }

    // ==== 材质 ====

    /// <returns>false = 贴图尚未就绪，调用方应稍后重试（绝不留下无贴图的白模材质）</returns>
    private static bool EnsureMaterials(List<string> log)
    {
        Directory.CreateDirectory(AssetMatFolder);
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) { log.Add("WARNING: 找不到 URP Lit 着色器，跳过材质"); return true; }

        // 以「贴图文件」为准枚举部件：T_Extinguisher_01_<suffix>_BaseColor.jpg → MAT_Extinguisher_01_<suffix>.mat。
        // 不能以模型内嵌材质为准——.meta 里一旦写过重映射，Unity 重导入后就不再生成内嵌材质子资产；
        // 早期版本因此第二次运行读到 0 个材质、整步被跳过，贴图没刷上 → 灭火器白模。
        string[] texFiles = Directory.Exists(AssetTexFolder)
            ? Directory.GetFiles(AssetTexFolder, "T_Extinguisher_01_*_BaseColor.jpg")
            : new string[0];
        if (texFiles.Length == 0) { log.Add("WARNING: 找不到 T_Extinguisher_01_*_BaseColor.jpg，跳过材质"); return true; }
        Array.Sort(texFiles, StringComparer.Ordinal);

        var existing = ExistingRemaps();                    // 源材质名 → 项目材质 GUID（.meta 已有）
        var guidToSource = new Dictionary<string, string>();
        foreach (var kv in existing)
            if (!guidToSource.ContainsKey(kv.Value)) guidToSource[kv.Value] = kv.Key;
        var embeddedNames = AssetDatabase.LoadAllAssetsAtPath(AssetFbxPath).OfType<Material>()
                                        .Select(m => m.name).ToList();   // 仅首次导入时非空

        var importer = AssetImporter.GetAtPath(AssetFbxPath) as ModelImporter;
        var pending = new List<KeyValuePair<string, Material>>();
        int missing = 0;

        foreach (string texFile in texFiles)
        {
            string baseName = Path.GetFileNameWithoutExtension(texFile);
            string suffix = baseName.Replace("T_Extinguisher_01_", "").Replace("_BaseColor", "");
            string matPath = AssetMatFolder + "/MAT_Extinguisher_01_" + suffix + ".mat";

            var tex = LoadTexture(texFile.Replace('\\', '/'), out bool texExists);
            if (texExists && tex == null) { missing++; log.Add("贴图尚未导入：" + texFile + "（稍后重试）"); continue; }

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.shader = shader;
            if (tex != null) mat.SetTexture("_BaseMap", tex);
            ApplyPbrMaps(mat, suffix, log);
            EditorUtility.SetDirty(mat);

            string matGuid = AssetDatabase.AssetPathToGUID(matPath);
            string sourceName = guidToSource.TryGetValue(matGuid, out string known)
                ? known : GuessSourceName(suffix, embeddedNames);
            log.Add("材质 " + matPath + (tex != null ? " ← " + Path.GetFileName(texFile) : "  ⚠ 贴图缺失")
                    + (sourceName != null ? "   源材质 " + sourceName : "   （无对应源材质，只刷新贴图）"));
            if (sourceName != null) pending.Add(new KeyValuePair<string, Material>(sourceName, mat));
        }
        if (missing > 0) return false;

        if (importer != null && pending.Count > 0)
        {
            int added = 0;
            foreach (var pair in pending)
            {
                string matGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(pair.Value));
                if (existing.TryGetValue(pair.Key, out string have) && have == matGuid) continue;
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), pair.Key), pair.Value);
                added++;
            }
            if (added > 0) importer.SaveAndReimport();
            log.Add("材质重映射：" + pending.Count + " 条（本轮新增 " + added + "，其余 .meta 已有）");
        }
        AssetDatabase.SaveAssets();
        log.Add("PBR：Normal + AO + MetallicSmoothness（R=Metallic，A=1-Roughness），原始 Roughness/Metallic 单独保留");
        return true;
    }

    private static void ApplyPbrMaps(Material mat, string suffix, List<string> log)
    {
        string prefix = AssetTexFolder + "/T_Extinguisher_01_" + suffix;
        Texture2D normal = LoadTexture(prefix + "_Normal.png", out _);
        Texture2D packed = LoadTexture(prefix + "_MetallicSmoothness.png", out _);
        Texture2D ao = LoadTexture(prefix + "_AO.png", out _);
        mat.SetTexture("_BumpMap", normal);
        mat.SetTexture("_MetallicGlossMap", packed);
        mat.SetTexture("_OcclusionMap", ao);
        SetKeyword(mat, "_NORMALMAP", normal != null);
        SetKeyword(mat, "_METALLICSPECGLOSSMAP", packed != null);
        SetKeyword(mat, "_OCCLUSIONMAP", ao != null);
        mat.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
        mat.SetFloat("_SmoothnessTextureChannel", 0f);
        mat.SetFloat("_Metallic", packed != null ? 1f : 0.15f);
        mat.SetFloat("_Smoothness", packed != null ? 1f : 0.45f);
        mat.SetFloat("_BumpScale", 1f);
        mat.SetFloat("_OcclusionStrength", 0.8f);
        log.Add(suffix + " PBR: normal=" + (normal != null) + " packed=" + (packed != null) + " ao=" + (ao != null));
    }

    private static void SetKeyword(Material mat, string keyword, bool enabled)
    {
        if (enabled) mat.EnableKeyword(keyword); else mat.DisableKeyword(keyword);
    }

    /// <summary>推断源材质名：先按编号匹配模型内嵌材质名，取不到再退回 Tripo 命名约定</summary>
    private static string GuessSourceName(string suffix, List<string> embeddedNames)
    {
        Match m = Regex.Match(suffix, @"(\d+)$");
        if (!m.Success) return null;
        string digits = int.Parse(m.Groups[1].Value).ToString();
        foreach (string name in embeddedNames)
        {
            Match n = Regex.Match(name, @"(\d+)\s*$");
            if (n.Success && int.Parse(n.Groups[1].Value).ToString() == digits) return name;
        }
        return "Material_tripo_part_" + digits;
    }

    /// <summary>取贴图；文件存在但资产库还没注册时强制同步导入一次再取（返回 tex=null, exists=true 表示"稍后重试"）</summary>
    private static Texture2D LoadTexture(string texPath, out bool fileExists)
    {
        fileExists = texPath != null && File.Exists(texPath);
        if (!fileExists) return null;

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        if (tex != null) return tex;

        AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
    }

    /// <summary>读 .meta 里已有的「源材质名 → 项目材质 GUID」映射（externalObjects 段），用于幂等判断</summary>
    private static Dictionary<string, string> ExistingRemaps()
    {
        var map = new Dictionary<string, string>();
        string metaPath = AssetFbxPath + ".meta";
        if (!File.Exists(metaPath)) return map;

        string text = File.ReadAllText(metaPath);
        foreach (Match m in Regex.Matches(text,
            @"name:\s*(?<src>[^\r\n]+)\s*\r?\n\s*second:\s*\{fileID:\s*2100000,\s*guid:\s*(?<guid>[0-9a-f]{32})"))
            map[m.Groups["src"].Value.Trim()] = m.Groups["guid"].Value;
        return map;
    }

    // ==== 部署 ====

    private static void DeployIntoThirdScene(List<string> log)
    {
        if (!File.Exists(TargetScenePath)) { log.Add("SKIP 部署：找不到 " + TargetScenePath); return; }

        bool openedByUs = false;
        UnityEngine.SceneManagement.Scene scene =
            UnityEngine.SceneManagement.SceneManager.GetSceneByPath(TargetScenePath);
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Additive);
            openedByUs = true;
        }
        try
        {
            GameObject content = FindRoot(scene, ContentRootName);
            if (content == null)
            {
                content = new GameObject(ContentRootName);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(content, scene);
                log.Add("内容根 '" + ContentRootName + "' 不存在，已新建");
            }

            Transform existing = FindDeep(content.transform, PropName);
            GameObject go = existing != null ? existing.gameObject : null;
            if (go == null)
            {
                var model = AssetDatabase.LoadMainAssetAtPath(AssetFbxPath) as GameObject;
                if (model == null) { log.Add("FAIL: 模型资产加载失败，无法部署"); return; }
                go = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                go.name = PropName;
                go.transform.SetParent(content.transform, true);
                log.Add("已在第三场景实例化 " + PropName);
            }
            else log.Add("场景里已有 " + PropName + "，只刷新位置与接线");

            // 6.0 兜底清掉模型自带的相机/灯光/音频监听（导入设置已关，但已部署的旧实例里可能还留着）
            StripNonPropComponents(go, log);

            // 6.1 摆位：SM_Desk_02 旁的空地，且离火源更远的一侧
            Vector3 spot = FindPlacementSpot(content, go, log);
            go.transform.position = new Vector3(spot.x, spot.y, spot.z);
            go.transform.rotation = Quaternion.identity;
            Bounds b = WorldBounds(go);
            go.transform.position += new Vector3(0f, spot.y - b.min.y, 0f);   // 底部贴地面
            log.Add("摆放位置：" + go.transform.position.ToString("F2")
                    + "　世界尺寸：" + WorldBounds(go).size.ToString("F3") + " m");

            // 6.2 交互接线（对齐 InteractionSetup.SetupPickups 的做法）
            foreach (MeshCollider mc in go.GetComponents<MeshCollider>()) UnityEngine.Object.DestroyImmediate(mc);
            BoxCollider box = go.GetComponent<BoxCollider>();
            if (box == null) box = go.AddComponent<BoxCollider>();
            Bounds local = AggregateLocalBounds(go);
            box.center = local.center;
            box.size = local.size;
            log.Add("BoxCollider center=" + local.center.ToString("F3") + " size=" + local.size.ToString("F3"));

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            var pickup = go.GetComponent<PickupItem>();
            if (pickup == null) pickup = go.AddComponent<PickupItem>();
            var so = new SerializedObject(pickup);

            // 持握点：原点是底部中心，直接用手部锚点会让 0.5m 的瓶身从手上向上顶到脸前。
            // v1 用"包围盒中心 + 前推 0.12m"是权宜；v2 改成**扶手式持握**（右手握提把/压把、左手握喷头），
            // 抓握点必须落在**两个红色手柄的几何中点**上。
            // 关键：必须用**几何包围盒**求部件位置，不能用节点 transform ——
            // 本模型的部件节点 transform 全在 (0,0,0)、几何偏移烘在网格里，
            // 按 transform 反推会得到 holdPositionOffset=(0,0,0)，瓶身被"穿"在手腕上、整瓶向上盖住脸，
            // 表现为"拿起灭火器后手臂直接不见了"（2026-09-22 实测踩过）。
            Vector3 gripLocal;
            string gripHow;
            TryResolveGripLocal(go.transform, local, out gripLocal, out gripHow);
            Vector3 holdOffset = -(Quaternion.Euler(HoldRotationOffset)
                                   * Vector3.Scale(gripLocal, go.transform.lossyScale));

            so.FindProperty("carryMode").enumValueIndex = (int)PickupItem.CarryMode.HandleAndNozzle;
            so.FindProperty("holdAlignsToView").boolValue = false;
            // 两点持握（右拳提把 + 左掌喷头）必须与手臂支点严格同幅俯仰，否则左手会随俯仰滑开
            so.FindProperty("holdPitchSyncsArms").boolValue = false;
            so.FindProperty("holdPitchFactor").floatValue = 1f;   // 同幅模式忽略此值，写 1 免得留下误导性数字
            so.FindProperty("holdPositionOffset").vector3Value = holdOffset;
            so.FindProperty("holdRotationOffset").vector3Value = HoldRotationOffset;
            so.FindProperty("interactPromptOverride").stringValue = "E 拿起灭火器";
            so.ApplyModifiedPropertiesWithoutUndo();

            // 原始收纳姿态的部件间距，仅用于资产诊断：左臂能不能够到喷头就看它。
            // 与手部「右抓握点→左抓握点」的距离比，差得多说明要改 Pose.Extinguisher 的臂角，而不是掰持握偏移。
            string jointGap = "?";
            if (PropGeometry.TryGetPartCenterLocal(go.transform, NozzleBodyNodeName, out Vector3 nozzleLocal, out _)
                || PropGeometry.TryGetPartCenterLocal(go.transform, NozzleNodeName, out nozzleLocal, out _))
                jointGap = Vector3.Distance(Vector3.Scale(gripLocal, go.transform.lossyScale),
                                            Vector3.Scale(nozzleLocal, go.transform.lossyScale)).ToString("F3") + " m";

            log.Add("PickupItem：carryMode=HandleAndNozzle（右拳拎提把 + 左手握喷头）"
                    + "　holdAlignsToView=false（瓶身直立；喷嘴独立随左手）");
            log.Add("  持握点由几何包围盒反推：" + gripHow + " → 局部 " + gripLocal.ToString("F3")
                    + "　holdPositionOffset=" + holdOffset.ToString("F3")
                    + "　holdRotationOffset=" + HoldRotationOffset.ToString("F0"));
            log.Add("  右手抓握点→左手抓握点 " + jointGap
                    + "（收纳状态；运行时软管会弯曲，不能据此横转整瓶）");

            // Pose.Extinguisher 的臂角也由本工具写入：这些是 HandPoseController 的 [SerializeField]，
            // 组件已入场景，改代码初始值对它无效（脚本默认值陷阱）。
            ApplyExtinguisherPose(scene, log);

            int layer = LayerMask.NameToLayer(InteractableLayerName);
            if (layer < 0)
                log.Add("WARNING: 没有 '" + InteractableLayerName + "' 层（先跑一次 InteractionSetup 注册），层未设置");
            else { go.layer = layer; log.Add("层 = " + InteractableLayerName + "(" + layer + ")"); }

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            log.Add("第三场景已保存：" + saved);
        }
        finally
        {
            if (openedByUs) EditorSceneManager.CloseScene(scene, true);
        }
    }

    /// <summary>把 Pose.Extinguisher 的 8 组角度写进场景里的 HandPoseController。
    /// 必须由工具写而不是靠脚本初始值：组件已经进了场景，Unity 只认序列化值（脚本默认值陷阱）。</summary>
    private static void ApplyExtinguisherPose(UnityEngine.SceneManagement.Scene scene, List<string> log)
    {
        HandPoseController hands = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            hands = root.GetComponentInChildren<HandPoseController>(true);
            if (hands != null) break;
        }
        if (hands == null)
        {
            log.Add("  ⚠ 场景里找不到 HandPoseController（手部姿态组件），Pose.Extinguisher 臂角未刷新");
            return;
        }

        var so = new SerializedObject(hands);
        SetPoseField(so, "extinguisherRightForearm", ExtRightForearm);
        SetPoseField(so, "extinguisherRightWrist", ExtRightWrist);
        SetPoseField(so, "extinguisherRightFingers", ExtRightFingers);
        SetPoseField(so, "extinguisherRightThumb", ExtRightThumb);
        SetPoseField(so, "extinguisherLeftForearm", ExtLeftForearm);
        SetPoseField(so, "extinguisherLeftWrist", ExtLeftWrist);
        SetPoseField(so, "extinguisherLeftFingers", ExtLeftFingers);
        SetPoseField(so, "extinguisherLeftThumb", ExtLeftThumb);
        so.ApplyModifiedPropertiesWithoutUndo();

        log.Add("  Pose.Extinguisher 臂角已刷新：右前臂 " + ExtRightForearm.ToString("F0")
                + "、左前臂 " + ExtLeftForearm.ToString("F0") + "（其余 6 项腕/指节一并写入）");
    }

    private static void SetPoseField(SerializedObject so, string name, Vector3 value)
    {
        SerializedProperty p = so.FindProperty(name);
        if (p == null)
        {
            Debug.LogWarning("[灭火器] HandPoseController 上没有字段 " + name + "（改名了？）");
            return;
        }
        p.vector3Value = value;
    }

    /// <summary>解析「右手抓握点」在物品局部空间里的位置（几何口径，见 PropGeometry 的说明）。
    /// 优先级：提把与压把的几何中点（对应用户要求"右手握住 2 个红色手柄"）→ 提把几何中心 → 包围盒中心兜底。
    /// 防呆：抓握点不可能贴在瓶底——算出的 y 若落在包围盒下 1/4 内（退化节点给出的 0 就是这种），
    /// 一律判为不可信并走兜底，绝不写进场景（2026-09-22 实测踩过 holdPositionOffset=(0,0,0)）。</summary>
    private static bool TryResolveGripLocal(Transform root, Bounds propLocal, out Vector3 gripLocal, out string how)
    {
        Vector3? handle = PartCenter(root, CarryHandleNodeName);
        Vector3? lever = PartCenter(root, SqueezeLeverNodeName);
        Vector3? candidate = null;
        if (handle.HasValue && lever.HasValue)
        {
            candidate = (handle.Value + lever.Value) * 0.5f;
            how = "提把与压把几何中点";
        }
        else if (handle.HasValue)
        {
            candidate = handle.Value;
            how = "提把几何中心（未找到 " + SqueezeLeverNodeName + "）";
        }
        else how = "未找到 " + CarryHandleNodeName + " 语义部件";

        if (candidate.HasValue && candidate.Value.y - propLocal.min.y > propLocal.size.y * 0.25f)
        {
            gripLocal = candidate.Value;
            return true;
        }

        gripLocal = new Vector3(0f, propLocal.center.y, 0f);
        how += " → 不可信（落在瓶底附近），改用包围盒中心兜底";
        return false;
    }

    /// <summary>部件几何中心（物品局部空间）；找不到返回 null。</summary>
    private static Vector3? PartCenter(Transform root, string name)
    {
        return PropGeometry.TryGetPartCenterLocal(root, name, out Vector3 c, out _) ? c : (Vector3?)null;
    }

    /// <summary>在 SM_Desk_02 四周挑空位；多个空位时取离火源更远的那个（玩家得走过去拿）</summary>
    private static Vector3 FindPlacementSpot(GameObject content, GameObject self, List<string> log)
    {
        Transform desk = FindDeep(content.transform, DeskBName) ?? FindDeep(content.transform, DeskAName);
        if (desk == null)
        {
            log.Add("WARNING: 找不到工位桌，灭火器放在内容根原点");
            return content.transform.position;
        }
        Bounds db = WorldBounds(desk.gameObject);
        float floorY = db.min.y;

        var candidates = new List<Vector3>
        {
            new Vector3(db.min.x - 0.6f, floorY, db.center.z),
            new Vector3(db.max.x + 0.6f, floorY, db.center.z),
            new Vector3(db.center.x, floorY, db.max.z + 0.6f),
            new Vector3(db.center.x, floorY, db.min.z - 0.6f),
        };

        Transform fire = FindDeep(content.transform, FireRootName) ?? FindRoot(content.scene, FireRootName)?.transform;
        Vector3 firePos = fire != null ? fire.position : db.center;

        var blockers = new List<Bounds>();
        foreach (Renderer r in content.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled) continue;
            if (r.transform.IsChildOf(desk)) continue;      // 桌子自身不算障碍
            if (self != null && r.transform.IsChildOf(self.transform)) continue;  // 灭火器自己也不算（否则重跑会换位置）
            blockers.Add(r.bounds);
        }

        Vector3 best = candidates[0];
        float bestDist = -1f;
        foreach (Vector3 c in candidates)
        {
            Vector3 probe = new Vector3(c.x, floorY + 0.25f, c.z);
            bool blocked = blockers.Any(bl => bl.Contains(probe));
            if (blocked) continue;
            float d = Vector3.Distance(new Vector3(c.x, 0f, c.z), new Vector3(firePos.x, 0f, firePos.z));
            if (d > bestDist) { bestDist = d; best = c; }
        }
        log.Add("选点：离火源 " + bestDist.ToString("F2") + " m（基准桌 " + desk.name + "）");
        return best;
    }

    /// <summary>把整棵子树的渲染包围盒折算到根物体的局部空间（子物体有旋转/缩放也正确）。
    /// 实现在 PropGeometry 里（运行时也会用同一套换算，见那儿的说明）。</summary>
    private static Bounds AggregateLocalBounds(GameObject root)
    {
        return PropGeometry.LocalBoundsOf(root.GetComponentsInChildren<Renderer>(true), root.transform);
    }

    // ==== 工具 ====

    private static string SourcePath(string rel) =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", rel));

    /// <summary>清掉模型自带的相机/灯光/音频监听：相机 depth 默认 0 &gt; 玩家主相机 -1，会把画面整个抢走</summary>
    private static void StripNonPropComponents(GameObject go, List<string> log)
    {
        int n = 0;
        foreach (Camera c in go.GetComponentsInChildren<Camera>(true))
        {
            log.Add("清除模型自带相机：" + c.gameObject.name);
            UnityEngine.Object.DestroyImmediate(c);
            n++;
        }
        foreach (Light l in go.GetComponentsInChildren<Light>(true))
        {
            log.Add("清除模型自带灯光：" + l.gameObject.name);
            UnityEngine.Object.DestroyImmediate(l);
            n++;
        }
        foreach (AudioListener a in go.GetComponentsInChildren<AudioListener>(true))
        {
            log.Add("清除模型自带音频监听：" + a.gameObject.name);
            UnityEngine.Object.DestroyImmediate(a);
            n++;
        }
        if (n == 0) log.Add("模型未带相机/灯光/音频监听 ✓");
    }

    /// <summary>目标文件缺失，或源文件比目标新（按 UTC 修改时间，留 1 秒余量）</summary>
    private static bool NeedsCopy(string src, string dst)
    {
        if (!File.Exists(dst)) return true;
        return File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dst) + TimeSpan.FromSeconds(1);
    }

    private static void EnsureTextureSettings(string assetPath)
    {
        var ti = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (ti == null) return;
        bool dirty = false;
        bool isNormal = assetPath.EndsWith("_Normal.png", StringComparison.OrdinalIgnoreCase);
        bool isColor = assetPath.Contains("_BaseColor");
        var type = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        if (ti.textureType != type) { ti.textureType = type; dirty = true; }
        if (ti.sRGBTexture != isColor) { ti.sRGBTexture = isColor; dirty = true; }
        if (isNormal && ti.convertToNormalmap) { ti.convertToNormalmap = false; dirty = true; }
        if (ti.alphaSource != TextureImporterAlphaSource.FromInput) { ti.alphaSource = TextureImporterAlphaSource.FromInput; dirty = true; }
        if (ti.alphaIsTransparency) { ti.alphaIsTransparency = false; dirty = true; }
        if (ti.maxTextureSize != 2048) { ti.maxTextureSize = 2048; dirty = true; }
        if (ti.wrapMode != TextureWrapMode.Clamp) { ti.wrapMode = TextureWrapMode.Clamp; dirty = true; }
        if (!ti.mipmapEnabled) { ti.mipmapEnabled = true; dirty = true; }
        if (dirty) ti.SaveAndReimport();
    }

    private static Bounds WorldBounds(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
        return b;
    }

    private static GameObject FindRoot(UnityEngine.SceneManagement.Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            if (root.name == name) return root;
        return null;
    }

    private static Transform FindDeep(Transform parent, string name)
    {
        if (parent == null) return null;
        if (parent.name == name) return parent;
        foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    private static Transform FindDeepInScene(UnityEngine.SceneManagement.Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform hit = FindDeep(root.transform, name);
            if (hit != null) return hit;
        }
        return null;
    }

    private static void WriteReport(List<string> log, string error, bool finished)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"unityVersion\": \"").Append(Application.unityVersion).Append("\",\n");
        sb.Append("  \"prop\": \"").Append(PropName).Append("\",\n");
        sb.Append("  \"source\": \"").Append(SourceModelRel).Append("\",\n");
        sb.Append("  \"scene\": \"").Append(TargetScenePath).Append("\",\n");
        sb.Append("  \"targetHeightM\": ").Append(TargetHeightM.ToString("F3")).Append(",\n");
        sb.Append("  \"finished\": ").Append(finished ? "true" : "false").Append(",\n");
        sb.Append("  \"error\": ");
        sb.Append(error == null ? "null" : "\"" + error.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
        sb.Append(",\n  \"steps\": [\n");
        for (int i = 0; i < log.Count; i++)
        {
            sb.Append("    \"").Append(log[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"");
            sb.Append(i == log.Count - 1 ? "\n" : ",\n");
        }
        sb.Append("  ]\n}\n");
        Directory.CreateDirectory("Library");
        File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
    }
}
