// 第三场景（测试场景）装配——从办公室场景复制并精简成「玩家 + 两张工位桌 + 火源 + 建筑外壳」。
//
// 保留：
//   人物（body + Interactor + FPHands_01 双手 + Main Camera）
//   技术内容根「场景」：办公室模型外壳（地板/墙/天花板/玻璃/门/灯具）+ 两张工位桌 SM_Desk_01/02 + 水杯 + 做旧插排
//   火源：起火点（FireEffectController，4 个分级 VFX，followTarget 跟随插排）
//   路线指引根：F8 调试（准星指向生成指引路线）挂在它的 GuidanceSystem 上，缺失时自动从办公室场景复制回来
//   光照、Global Volume、LevelBootstrapper（autoStart 关闭）、地面碰撞体 floor
//
// 删除：
//   答题系统根「Scene Choices」、Office Choice Objects、QuizResultFeedback 实例
//   家具与道具组（会议/茶水/接待/座椅/储物/办公道具/绿植/HVAC）、其余 22 张工位桌与桌椅分隔板
//
// 火源摆放：做旧插排落在 SM_Desk_01 旁边的地面上（自动挑一个不与其它几何重叠的方向，插排自身不算障碍），
//          起火点贴插排顶面 +0.02m，并接线 FireEffectController.followTarget 跟随插排。
//
// 调试热键：F9 交给 FireEffectController.debugMode（本场景没有关卡流程，LevelFlowRunner 的 F9 走不到）；
//          F8 由 GuidanceSystem.debugMode 负责，所以路线指引根必须留着。
//
// 幂等：目标场景不存在才从源场景复制；每步都在缺失时才创建；写 Library/Scene3Setup.txt 报告；
//       Library/Scene3Setup.v2.done 标记防重复执行（v1 曾抑制路线指引，bump 版本以自动补跑修复）；菜单可强制重跑。
// 不会影响当前打开的场景：目标场景以 Additive 方式打开、改完保存后关闭。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class Scene3Setup
{
    private const string SourceScenePath = "Assets/Scenes/办公室场景.unity";
    private const string TargetScenePath = "Assets/Scenes/第三场景.unity";
    private const string TargetSceneName = "第三场景";
    private const string Marker = "Library/Scene3Setup.v3.done";
    private const string ReportPath = "Library/Scene3Setup.txt";

    private const string ContentRootName = "场景";
    private const string OfficeModelName = "SCN_Office_01";
    private const string FireRootName = "起火点";
    private const string GuidanceRootName = "路线指引";
    private const string GuidanceSuppressMarker = "Library/GuidanceSetup.第三场景.done";
    private const string SocketObjectName = "SM_Hazard_OverloadSocket_01_Aged";
    private const string SocketAssetPath = "Assets/Props/Models/SM_Hazard_OverloadSocket_01_Aged.fbx";
    private const string LegacySocketName = "SM_Choice_PowerStrip";

    // 只保留这两张工位桌（其余 SM_Desk_* / SM_Chair_* / SM_DeskDivider_* 全删）
    private static readonly string[] KeepDeskNames = { "SM_Desk_01", "SM_Desk_02" };

    // 要删除的根物体（答题系统 / 道具容器 / 结算 UI）
    // 注意：「路线指引」不删——F8（准星指点生成指引路线）挂在它的 GuidanceSystem 上，是测试要用的调试能力。
    private static readonly string[] DropRootNames =
    {
        "Scene Choices", "Office Choice Objects", "QuizResultFeedback",
    };

    // 要删除的办公室模型分组（只留建筑外壳：Architecture_* / Doors / Fixtures_Lighting）
    private static readonly string[] DropGroups =
    {
        "GRP_Furniture_Meeting", "GRP_Furniture_Pantry", "GRP_Furniture_Reception",
        "GRP_Furniture_Seating", "GRP_Furniture_Storage", "GRP_Props_Office",
        "GRP_Props_Plants", "GRP_Fixtures_HVAC",
    };

    // 道具：水杯 SM_Cup_01 故意留着——它是 InteractionSetup 配置的单手抓取测试物（离玩家最近的桌面中央），
    // 删掉也会被 InteractionSetup 重新补回来，所以不列入删除项。

    static Scene3Setup()
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
        // Play 中不改场景，等退出播放后再试（与 FireSetup 同法）
        if (EditorApplication.isPlaying)
        {
            EditorApplication.delayCall += Auto;
            return;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (File.Exists(Marker)) return;
        Build();
    }

    [MenuItem("Tools/Scene3/创建第三场景（测试场景）")]
    public static void ForceRun()
    {
        if (File.Exists(Marker)) File.Delete(Marker);
        Build();
    }

    /// <summary>诊断：报告第三场景现状，不改动任何东西。</summary>
    [MenuItem("Tools/Scene3/检查第三场景")]
    public static void Inspect()
    {
        var sb = new StringBuilder();
        sb.AppendLine("target: " + TargetScenePath + " exists=" + File.Exists(TargetScenePath));
        sb.AppendLine("marker: " + Marker + " exists=" + File.Exists(Marker));
        if (!File.Exists(TargetScenePath))
        {
            File.WriteAllText("Library/Scene3SetupInspect.txt", sb.ToString());
            Debug.Log("[Scene3] Inspect（场景尚未创建）→ Library/Scene3SetupInspect.txt\n" + sb);
            return;
        }

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
            sb.AppendLine("roots:");
            foreach (GameObject root in scene.GetRootGameObjects())
                sb.AppendLine("  " + root.name + "  children=" + root.GetComponentsInChildren<Transform>(true).Length);

            Transform content = FindRoot(scene, ContentRootName)?.transform;
            if (content != null)
            {
                Transform desk1 = FindDeep(content, "SM_Desk_01");
                Transform desk2 = FindDeep(content, "SM_Desk_02");
                sb.AppendLine("场景/SM_Desk_01: " + (desk1 != null ? desk1.position.ToString("F2") : "MISSING"));
                sb.AppendLine("场景/SM_Desk_02: " + (desk2 != null ? desk2.position.ToString("F2") : "MISSING"));
                Transform socket = FindDeep(content, SocketObjectName);
                sb.AppendLine("场景/" + SocketObjectName + ": "
                              + (socket != null ? socket.position.ToString("F2") : "MISSING"));
            }
            else sb.AppendLine("场景: MISSING");

            Transform fire = FindRoot(scene, FireRootName)?.transform
                             ?? (content != null ? FindDeep(content, FireRootName) : null);
            sb.AppendLine(FireRootName + ": " + (fire != null ? fire.position.ToString("F2") : "MISSING"));
        }
        finally
        {
            if (openedByUs) EditorSceneManager.CloseScene(scene, true);
        }

        File.WriteAllText("Library/Scene3SetupInspect.txt", sb.ToString());
        Debug.Log("[Scene3] Inspect → Library/Scene3SetupInspect.txt\n" + sb);
    }

    public static void Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        var log = new List<string>();
        string error = null;
        try
        {
            BuildInternal(log);
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            Debug.LogException(e);
        }

        try { WriteReport(log, error); }
        catch (Exception e) { Debug.LogException(e); }

        if (error == null)
        {
            Directory.CreateDirectory("Library");
            File.WriteAllText(Marker, DateTime.UtcNow.ToString("o"));
        }

        var sb = new StringBuilder();
        foreach (string line in log) sb.AppendLine(line);
        Debug.Log("[Scene3] 第三场景装配 " + (error == null ? "完成" : "失败：" + error)
                  + "\n" + sb + "报告：" + ReportPath);
    }

    private static void BuildInternal(List<string> log)
    {
        if (!File.Exists(SourceScenePath))
        {
            log.Add("SKIP: 源场景不存在 " + SourceScenePath);
            return;
        }

        // 1) 复制场景（已存在则不覆盖，避免抹掉用户改动）
        if (!File.Exists(TargetScenePath))
        {
            if (!AssetDatabase.CopyAsset(SourceScenePath, TargetScenePath))
            {
                log.Add("FAIL: AssetDatabase.CopyAsset 失败 " + SourceScenePath + " -> " + TargetScenePath);
                return;
            }
            AssetDatabase.Refresh();
            log.Add("已复制场景：" + SourceScenePath + " -> " + TargetScenePath);
        }
        else log.Add("目标场景已存在，不覆盖：" + TargetScenePath);

        bool openedByUs = false;
        UnityEngine.SceneManagement.Scene scene =
            UnityEngine.SceneManagement.SceneManager.GetSceneByPath(TargetScenePath);
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Additive);
            openedByUs = true;
        }
        else log.Add("目标场景当前已打开，直接在其上操作（不再另开一份）");
        if (!scene.IsValid() || !scene.isLoaded)
        {
            log.Add("FAIL: 无法打开目标场景 " + TargetScenePath);
            return;
        }

        try
        {
            // 2) 内容根「场景」
            GameObject content = FindRoot(scene, ContentRootName);
            if (content == null)
            {
                content = new GameObject(ContentRootName);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(content, scene);
                log.Add("内容根 '" + ContentRootName + "' 不存在，已新建");
            }

            // 3) 先把做旧插排从「Office Choice Objects」搬进内容根并改名（改名后 FireSetup 认得它），
            //    必须在删除那些根物体之前做，否则会连插排一起删掉、丢掉原始摆位。
            GameObject socket = EnsureSocket(content, scene, log);

            // 4) 删掉流程类根物体（答题系统 / 道具容器 / 结算 UI；路线指引不在此列，F8 要用）
            foreach (string name in DropRootNames)
            {
                GameObject go = FindRoot(scene, name);
                if (go == null) { log.Add("根物体不存在，跳过：" + name); continue; }
                UnityEngine.Object.DestroyImmediate(go);
                log.Add("已删除根物体：" + name);
            }

            // 5) 精简办公室模型：只留建筑外壳 + 两张工位桌
            GameObject office = FindDeep(content.transform, OfficeModelName)?.gameObject;
            if (office == null)
            {
                log.Add("WARNING: 内容根下找不到办公室模型 " + OfficeModelName + "，跳过模型精简");
            }
            else
            {
                int removed = 0;
                foreach (string group in DropGroups)
                {
                    Transform g = FindDeep(office.transform, group);
                    if (g == null) continue;
                    UnityEngine.Object.DestroyImmediate(g.gameObject);
                    removed++;
                    log.Add("已删除家具分组：" + group);
                }

                // 工位组：只留 SM_Desk_01/02
                Transform workstations = FindDeep(office.transform, "GRP_Furniture_Workstations");
                if (workstations != null)
                {
                    int deskRemoved = 0;
                    foreach (Transform child in workstations.Cast<Transform>().ToList())
                    {
                        if (child == null) continue;
                        if (KeepDeskNames.Contains(child.name)) continue;
                        UnityEngine.Object.DestroyImmediate(child.gameObject);
                        deskRemoved++;
                    }
                    log.Add("工位组内删除 " + deskRemoved + " 个节点，保留 " + string.Join("/", KeepDeskNames));
                }
                else log.Add("WARNING: 找不到 GRP_Furniture_Workstations");

                // 兜底：模型里其它残留的桌子/椅子/分隔板
                int sweep = 0;
                foreach (Transform t in office.GetComponentsInChildren<Transform>(true).ToList())
                {
                    if (t == null || t == office.transform) continue;
                    string n = t.name;
                    bool isDesk = n.StartsWith("SM_Desk", StringComparison.Ordinal) && !KeepDeskNames.Contains(n);
                    bool isChair = n.StartsWith("SM_Chair_", StringComparison.Ordinal);
                    if (!isDesk && !isChair) continue;
                    UnityEngine.Object.DestroyImmediate(t.gameObject);
                    sweep++;
                }
                log.Add("兜底清理桌子/椅子节点 " + sweep + " 个");

                // 两张工位桌补 MeshCollider（可站可挡，供交互测试）
                foreach (string deskName in KeepDeskNames)
                {
                    Transform desk = FindDeep(office.transform, deskName);
                    if (desk == null) { log.Add("WARNING: 找不到工位桌 " + deskName); continue; }
                    int added = EnsureMeshColliders(desk.gameObject);
                    log.Add("工位桌 " + deskName + " 位置 " + desk.position.ToString("F2")
                            + "，补 MeshCollider " + added + " 个");
                }
            }

            // 6) 路线指引根（F8 调试用）：缺失时从办公室场景复制回来
            EnsureGuidanceRoot(scene, log);

            // 7) 火源摆到 SM_Desk_01 旁边的地面
            PlaceFireOnGround(content, socket, log);

            // 8) 关卡适配器：本场景尚未登记关卡，先关掉自动开局，避免 Play 时报找不到配置
            Transform boot = FindRoot(scene, "LevelBootstrapper")?.transform;
            if (boot != null)
            {
                var bootstrapper = boot.GetComponent<HMProtection.Core.LevelBootstrapper>();
                if (bootstrapper != null)
                {
                    var so = new SerializedObject(bootstrapper);
                    SerializedProperty auto = so.FindProperty("autoStart");
                    if (auto != null && auto.boolValue)
                    {
                        auto.boolValue = false;
                        so.ApplyModifiedPropertiesWithoutUndo();
                        log.Add("LevelBootstrapper.autoStart -> false（第三场景暂未登记关卡）");
                    }
                    else log.Add("LevelBootstrapper.autoStart 已是 false");
                }
            }
            else log.Add("WARNING: 场景里没有 LevelBootstrapper 根物体");

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            log.Add("场景已保存：" + saved);
        }
        finally
        {
            if (openedByUs) EditorSceneManager.CloseScene(scene, true);
        }
    }

    /// <summary>做旧插排：优先搬运场景里已有的实例（保留原位置），否则从 FBX 资产实例化。</summary>
    private static GameObject EnsureSocket(GameObject content, UnityEngine.SceneManagement.Scene scene, List<string> log)
    {
        // 重复执行：内容根下已经有插排
        Transform existing = FindDeep(content.transform, SocketObjectName);
        if (existing != null)
        {
            log.Add("插排已存在：" + SocketObjectName + " @" + existing.position.ToString("F2"));
            return existing.gameObject;
        }

        // 办公室场景里的原实例挂在「Office Choice Objects」下，叫 SM_Choice_PowerStrip；
        // 直接搬进内容根（worldPositionStays=true 保住世界位姿）并改成 FireSetup 认得的名字。
        Transform legacy = FindDeepInScene(scene, LegacySocketName);
        if (legacy != null)
        {
            legacy.name = SocketObjectName;
            legacy.SetParent(content.transform, true);
            log.Add("已把 " + LegacySocketName + " 搬进内容根并改名为 " + SocketObjectName
                    + " @" + legacy.position.ToString("F2"));
            return legacy.gameObject;
        }

        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(SocketAssetPath);
        if (asset == null)
        {
            log.Add("WARNING: 找不到插排资产 " + SocketAssetPath + "，火源将没有道具底座");
            return null;
        }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
        go.name = SocketObjectName;
        go.transform.SetParent(content.transform, true);
        log.Add("已从资产实例化插排：" + SocketAssetPath);
        return go;
    }

    /// <summary>路线指引根：F8（准星指点生成指引路线）挂在它的 GuidanceSystem 上；缺了就从办公室场景复制一份回来。</summary>
    private static void EnsureGuidanceRoot(UnityEngine.SceneManagement.Scene targetScene, List<string> log)
    {
        // 早期版本曾写抑制标记让 GuidanceSetup 跳过本场景，这里清掉，恢复项目工具的正常自动装配
        if (File.Exists(GuidanceSuppressMarker))
        {
            File.Delete(GuidanceSuppressMarker);
            log.Add("已删除历史抑制标记：" + GuidanceSuppressMarker + "（GuidanceSetup 可正常复核本场景）");
        }

        if (FindRoot(targetScene, GuidanceRootName) != null)
        {
            log.Add(GuidanceRootName + " 已存在，F8 调试可用");
            DedupeGuidanceRoots(targetScene, log);
            return;
        }

        bool openedByUs = false;
        UnityEngine.SceneManagement.Scene source =
            UnityEngine.SceneManagement.SceneManager.GetSceneByPath(SourceScenePath);
        if (!source.IsValid() || !source.isLoaded)
        {
            source = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Additive);
            openedByUs = true;
        }
        try
        {
            GameObject src = FindRoot(source, GuidanceRootName);
            if (src == null)
            {
                log.Add("WARNING: 办公室场景里也没有 " + GuidanceRootName
                        + "，F8 将不可用（可跑菜单 Tools/Guidance/Re-run Guidance Setup 重建）");
                return;
            }
            GameObject clone = UnityEngine.Object.Instantiate(src);
            clone.name = GuidanceRootName;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, targetScene);
            log.Add("已从办公室场景复制回 " + GuidanceRootName
                    + "（GuidanceSystem + 障碍扫描 + 路线渲染 + 目的地标记）→ F8 可用");
            DedupeGuidanceRoots(targetScene, log);
        }
        finally
        {
            if (openedByUs) EditorSceneManager.CloseScene(source, true);
        }
    }

    /// <summary>同名根只保留一份：本工具与 GuidanceSetup 可能在同一轮域重载里各建一份，
    /// 两个 GuidanceSystem 会同时响应 F8、各画一条路线（还会各扫一次障碍）。保留节点最多的那份。</summary>
    private static void DedupeGuidanceRoots(UnityEngine.SceneManagement.Scene scene, List<string> log)
    {
        var found = new List<GameObject>();
        foreach (GameObject root in scene.GetRootGameObjects())
            if (root.name == GuidanceRootName) found.Add(root);
        if (found.Count <= 1) return;

        found.Sort((a, b) => b.GetComponentsInChildren<Transform>(true).Length
                              .CompareTo(a.GetComponentsInChildren<Transform>(true).Length));
        for (int i = 1; i < found.Count; i++)
        {
            UnityEngine.Object.DestroyImmediate(found[i]);
            log.Add("删除重复的 " + GuidanceRootName + "（原有 " + (found.Count)
                    + " 份，保留节点最多的那份，避免 F8 画出两条路线）");
        }
    }

    /// <summary>火源落地：插排贴地面 → 起火点贴插排顶面 → followTarget 跟随插排；并接管 F9。</summary>
    private static void PlaceFireOnGround(GameObject content, GameObject socket, List<string> log)
    {
        Transform desk = FindDeep(content.transform, KeepDeskNames[0]);
        if (desk == null)
        {
            log.Add("WARNING: 找不到 " + KeepDeskNames[0] + "，火源位置未调整");
            return;
        }

        Bounds deskBounds = WorldBounds(desk.gameObject);
        float floorY = deskBounds.min.y;                       // 桌子腿落在楼面上，取其底面当地面高度

        if (socket != null)
        {
            Vector3 spot = FindFreeSpot(content, deskBounds, floorY, socket);
            socket.transform.position = new Vector3(spot.x, floorY, spot.z);
            socket.transform.rotation = Quaternion.identity;
            // 用自身包围盒把底面精确压到地面
            Bounds sb = WorldBounds(socket);
            socket.transform.position += new Vector3(0f, floorY - sb.min.y, 0f);
            log.Add("插排已放到地面：" + socket.transform.position.ToString("F2")
                    + "（地面 y=" + floorY.ToString("F2") + "）");
        }

        Transform fire = FindDeep(content.transform, FireRootName) ?? FindRoot(content.scene, FireRootName)?.transform;
        if (fire == null)
        {
            log.Add("WARNING: 场景里没有 '" + FireRootName + "'，请在办公室场景确认后再复制");
            return;
        }

        var controller = fire.GetComponent<FireEffectController>();
        if (controller == null)
        {
            log.Add("WARNING: '" + FireRootName + "' 上没有 FireEffectController");
            return;
        }

        // F9 归 FireEffectController：第三场景没有关卡流程（LevelFlowRunner 未 Begin，Config 为 null），
        // 它的 F9 走不到 CycleFireLevel；办公室场景里 FireEffectController.debugMode 被 LevelSetup
        // 关掉正是为了把 F9 让给 LevelFlowRunner，这里反过来打开。
        var so = new SerializedObject(controller);
        SerializedProperty debug = so.FindProperty("debugMode");
        if (debug != null && !debug.boolValue)
        {
            debug.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            log.Add("FireEffectController.debugMode -> true（第三场景 F9 直接循环火势分级）");
        }
        else if (debug != null) log.Add("FireEffectController.debugMode 已开启（F9 可用）");

        if (socket != null)
        {
            Bounds sb = WorldBounds(socket);
            fire.position = new Vector3(sb.center.x, sb.max.y + 0.02f, sb.center.z);
            SerializedProperty follow = so.FindProperty("followTarget");
            if (follow != null)
            {
                follow.objectReferenceValue = socket.transform;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            log.Add("起火点已贴插排顶面：" + fire.position.ToString("F2") + "，followTarget 已接线");
        }
        else log.Add("起火点位置未调整（没有插排底座）");
    }

    /// <summary>在桌子四周挑一个不压到其它几何的地面落点（前后左右 0.5m）。ignore = 自身（插排）不算障碍。</summary>
    private static Vector3 FindFreeSpot(GameObject content, Bounds deskBounds, float floorY, GameObject ignore)
    {
        var candidates = new List<Vector3>
        {
            new Vector3(deskBounds.max.x + 0.5f, floorY, deskBounds.center.z),
            new Vector3(deskBounds.min.x - 0.5f, floorY, deskBounds.center.z),
            new Vector3(deskBounds.center.x, floorY, deskBounds.max.z + 0.5f),
            new Vector3(deskBounds.center.x, floorY, deskBounds.min.z - 0.5f),
        };

        var blockers = new List<Bounds>();
        foreach (Renderer r in content.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled) continue;
            // 插排自己也在 content 下：不排除的话重跑时会把自己判成障碍，把火源挪到别的方向
            if (ignore != null && r.transform.IsChildOf(ignore.transform)) continue;
            blockers.Add(r.bounds);
        }

        foreach (Vector3 c in candidates)
        {
            Vector3 probe = new Vector3(c.x, floorY + 0.15f, c.z);
            bool blocked = false;
            foreach (Bounds b in blockers)
            {
                if (b.Contains(probe)) { blocked = true; break; }
            }
            if (!blocked) return c;
        }

        // 四个方向都被占：退回桌子 +X 方向外侧
        return candidates[0];
    }

    private static int EnsureMeshColliders(GameObject root)
    {
        int added = 0;
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter == null || filter.sharedMesh == null) continue;
            if (filter.GetComponent<Collider>() != null) continue;
            filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
            added++;
        }
        return added;
    }

    private static Bounds WorldBounds(GameObject go)
    {
        bool any = false;
        Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled) continue;
            if (!any) { bounds = r.bounds; any = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return bounds;
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

    /// <summary>整个场景里按名字找（含所有根物体）。</summary>
    private static Transform FindDeepInScene(UnityEngine.SceneManagement.Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform hit = FindDeep(root.transform, name);
            if (hit != null) return hit;
        }
        return null;
    }

    private static void WriteReport(List<string> log, string error)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"unityVersion\": \"").Append(Application.unityVersion).Append("\",\n");
        sb.Append("  \"source\": \"").Append(SourceScenePath).Append("\",\n");
        sb.Append("  \"target\": \"").Append(TargetScenePath).Append("\",\n");
        sb.Append("  \"targetName\": \"").Append(TargetSceneName).Append("\",\n");
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
