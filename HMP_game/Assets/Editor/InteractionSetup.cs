// One-shot interaction framework setup: registers the "Interactable" layer, attaches the
// interaction components to the office scene (door controllers on the 4 interior swing doors
// with hinge positions read from the hinge meshes, seat controllers with computed sitting
// facing on all chairs and the reception sofa, pickup components + rigidbodies + box
// colliders on the temporary test items), wires the Interactor on the player body and the
// HandPoseController on the FPHands instance, removes door components that are no longer
// configured (e.g. the exit double doors), then saves the scene. Writes
// Library/InteractionSetup.json. A marker file prevents it from running twice; use the menu
// item to force a re-run after changing the tables below (e.g. swapping temporary test items
// for the real cup/extinguisher models).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class InteractionSetup
{
    private const string ReportPath = "Library/InteractionSetup.json";
    private const string LayerName = "Interactable";
    private const int PreferredLayerIndex = 6;

    // ---- 配置表：平开人行门（门板名）。
    //      出口双开门 SM_Door_EntryLeft/Right_01 不配置：培训中保持关闭，
    //      打开会让玩家走出场景边界掉入虚空（第三关逃生阶段再做正式处理）。
    //      配件命名规则：门板 SM_Door_X_01 的把手/铰链是 SM_Door_X_Handle_01 / SM_Door_X_Hinge_01
    //      （去掉门板名最后一段编号，已逐一核对 FBX 网格名）。 ----
    private static readonly string[] DoorPanelNames =
    {
        "SM_Door_Manager_01",
        "SM_Door_Meeting_01",
        "SM_Door_Pantry_01",
        "SM_Door_Support_01",
    };

    // ---- 配置表：可抓取物品。
    //      SM_Cup_01：正式单手道具（Astra 交付的 PolyHaven jug_01），场景没有时会自动
    //      实例化，优先摆到 placeNear 指定的参考物（SM_NotebookPages_01）旁边，
    //      参考物不存在时退回「离玩家最近的桌面中央」；
    //      SM_Keyboard_01：临时双手测试物，待 SM_Extinguisher_01 灭火器到位后替换。 ----
    private static readonly PickupConfig[] PickupConfigs =
    {
        // HoldRotation：水杯模型的开口轴沿自身 +Z，持握/摆放时绕 X 转 -90° 让杯口朝上
        // AlignsToView：水杯关（世界直立）——它原点在底部、抓握点在掌心凹槽，跟视角俯仰会让
        //               几何落进手部网格/近裁面里而看不见；长条物（灭火器等）才开
        // HoldPosition：可选；null = 不动（由谁部署谁负责）。水杯给 +3cm 让杯身相对手部抬高一点，
        //               减少杯底与手指/掌心的穿模
        new PickupConfig("SM_Cup_01", PickupItem.CarryMode.OneHand, true, "SM_NotebookPages_01",
                         new Vector3(-90f, 0f, 0f), false, new Vector3(0f, 0.03f, 0f)),
        new PickupConfig("SM_Keyboard_01", PickupItem.CarryMode.TwoHands, false, null,
                         Vector3.zero, true, null),
        // 灭火器：由 Assets/Editor/ExtinguisherSetup.cs 部署（尺寸/材质/持握点都在那边），
        // 这里只登记配置——关键作用是让 CleanupStalePickups 认得它，否则重跑本工具会把它的
        // PickupItem/Rigidbody/BoxCollider 当"陈旧道具"剥掉。
        // 持握点居中偏移（holdPositionOffset）由 ExtinguisherSetup 按包围盒写入 → 这里传 null 不覆盖。
        new PickupConfig("SM_Extinguisher_01", PickupItem.CarryMode.TwoHands, false, null,
                         Vector3.zero, true, null),
    };

    private class PickupConfig
    {
        public readonly string ObjectName;
        public readonly PickupItem.CarryMode Mode;
        public readonly bool EnsureInScene;
        public readonly string PlaceNearName;      // 摆放参考物（放在它旁边）；null = 桌面中央
        public readonly Vector3 HoldRotation;      // 持握与摆放时的旋转补偿（欧拉角）
        public readonly bool AlignsToView;         // 持握朝向是否跟随摄像机视角（长条物 true / 水杯 false）
        public readonly Vector3? HoldPosition;     // 持握位置偏移；null = 保持现值（不动）
        public PickupConfig(string objectName, PickupItem.CarryMode mode, bool ensureInScene,
                            string placeNearName, Vector3 holdRotation, bool alignsToView, Vector3? holdPosition)
        {
            ObjectName = objectName;
            Mode = mode;
            EnsureInScene = ensureInScene;
            PlaceNearName = placeNearName;
            HoldRotation = holdRotation;
            AlignsToView = alignsToView;
            HoldPosition = holdPosition;
        }
    }

    static InteractionSetup()
    {
        EditorApplication.delayCall += Run;
        // 多场景支持：项目现有 初始界面 / 办公室场景 等多个场景，每次打开场景后
        // 检查「当前场景」是否已装配（标记按场景名区分），新场景打开即自动装配
        EditorSceneManager.sceneOpened += (scene, mode) => Run();
    }

    // 每个场景独立的完成标记：Library/InteractionSetup.<场景名>.done
    private static string MarkerPathFor(Scene scene)
    {
        return "Library/InteractionSetup." + scene.name + ".done";
    }

    public static void Run()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(scene.name)) return; // 未保存的临时场景不处理
        string markerPath = MarkerPathFor(scene);
        if (File.Exists(markerPath)) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += Run;
            return;
        }
        if (EditorApplication.isPlaying)
        {
            // 播放中不改场景，等退出播放后再试
            EditorApplication.delayCall += Run;
            return;
        }

        var log = new List<string>();
        bool processed = false;
        string error = null;
        try
        {
            processed = ProcessScene(log);
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            Debug.LogException(e);
        }

        try
        {
            WriteReport(processed, error, log);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }

        if (processed) File.WriteAllText(markerPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Debug.Log("InteractionSetup finished. scene=" + scene.name + " processed=" + processed + " error=" + (error ?? "none")
                  + " report=" + ReportPath);
    }

    [MenuItem("Tools/Interactable/Re-run Interaction Setup")]
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

    private static bool ProcessScene(List<string> log)
    {
        Scene scene = SceneManager.GetActiveScene();
        Transform sceneRoot = FindRoot(scene, "场景");
        Transform characterRoot = FindRoot(scene, "人物");
        if (sceneRoot == null || characterRoot == null)
        {
            log.Add("root objects not found (场景=" + (sceneRoot != null) + " 人物=" + (characterRoot != null)
                    + ") in scene '" + scene.name + "'");
            return false;
        }
        Transform body = FindDescendant(characterRoot, "body");
        if (body == null)
        {
            log.Add("no object named 'body' under 人物");
            return false;
        }

        int layer = EnsureLayer(log);
        int mask = 1 << layer;
        log.Add("interactable layer '" + LayerName + "' = " + layer + " (mask " + mask + ")");

        // 1) 玩家交互器 + 手部姿态控制
        SetupInteractor(body.gameObject, mask, log);
        SetupHandPose(characterRoot, log);

        // 2) 门
        int doors = SetupDoors(sceneRoot, layer, log);

        // 3) 椅子 + 沙发
        int seats = SetupSeats(sceneRoot, layer, log);

        // 4) 可抓取物品（含新道具自动入场景 + 旧临时物清理）
        int pickups = SetupPickups(sceneRoot, layer, log, body);

        log.Add("doors configured: " + doors + ", seats configured: " + seats + ", pickups configured: " + pickups);

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveOpenScenes();
        log.Add("scene saved: " + saved + " (" + scene.path + ")");
        return true;
    }

    // 注册 Interactable 层：优先用第 6 层，被占用则向后找空位；已注册则直接复用
    private static int EnsureLayer(List<string> log)
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets == null || assets.Length == 0) throw new InvalidOperationException("TagManager.asset not found");
        var tagManager = new SerializedObject(assets[0]);
        var layers = tagManager.FindProperty("layers");
        if (layers.arraySize < 32) layers.arraySize = 32;

        for (int i = 6; i < 32; i++)
        {
            string existing = layers.GetArrayElementAtIndex(i).stringValue;
            if (existing == LayerName)
            {
                log.Add("layer already registered at index " + i);
                return i;
            }
        }
        int target = PreferredLayerIndex;
        while (target < 32 && !string.IsNullOrEmpty(layers.GetArrayElementAtIndex(target).stringValue)) target++;
        if (target >= 32) throw new InvalidOperationException("no free user layer slot for '" + LayerName + "'");

        layers.GetArrayElementAtIndex(target).stringValue = LayerName;
        tagManager.ApplyModifiedPropertiesWithoutUndo();
        log.Add("layer '" + LayerName + "' registered at index " + target);
        return target;
    }

    private static void SetupInteractor(GameObject bodyGo, int mask, List<string> log)
    {
        var interactor = bodyGo.GetComponent<Interactor>();
        if (interactor == null) interactor = bodyGo.AddComponent<Interactor>();

        var so = new SerializedObject(interactor);
        so.FindProperty("interactableMask").intValue = mask;
        so.ApplyModifiedPropertiesWithoutUndo();
        log.Add("Interactor on " + GetHierarchyPath(bodyGo.transform) + ", mask=" + mask);
    }

    private static void SetupHandPose(Transform characterRoot, List<string> log)
    {
        Transform fpHands = FindDescendant(characterRoot, "FPHands_01");
        if (fpHands == null)
        {
            log.Add("WARNING: FPHands_01 not found under 人物, hand poses disabled");
            return;
        }
        if (fpHands.GetComponent<HandPoseController>() == null) fpHands.gameObject.AddComponent<HandPoseController>();
        // 手臂俯仰跟随：低头时手臂转出视野、不露末端断面（见 ArmsPitchFollow.cs）
        var armsFollow = fpHands.GetComponent<ArmsPitchFollow>();
        if (armsFollow == null) armsFollow = fpHands.gameObject.AddComponent<ArmsPitchFollow>();
        // 已挂组件不会吃到代码里的新默认值（场景序列化值优先），重跑时同步刷新一次；
        // armTuckOffset / gripNormalDist 为实测调优值（用户 Play 模式调定后固化）
        var followSo = new SerializedObject(armsFollow);
        followSo.FindProperty("followFactor").floatValue = 0.85f;
        followSo.FindProperty("sinkPerDegree").floatValue = 0.0015f;
        followSo.FindProperty("deadZone").floatValue = 6f;
        followSo.FindProperty("armTuckOffset").vector3Value = new Vector3(0f, -0.2f, -0.2f);
        followSo.FindProperty("gripNormalDist").floatValue = -0.035f;
        followSo.ApplyModifiedPropertiesWithoutUndo();
        log.Add("HandPoseController + ArmsPitchFollow (factor=0.85, tuck=(0,-0.2,-0.2), gripNormal=-0.035) on " + GetHierarchyPath(fpHands));
    }

    private static int SetupDoors(Transform sceneRoot, int layer, List<string> log)
    {
        int configured = 0;
        foreach (string panelName in DoorPanelNames)
        {
            Transform panel = FindDescendant(sceneRoot, panelName);
            if (panel == null)
            {
                log.Add("WARNING: door panel '" + panelName + "' not found, skipped");
                continue;
            }

            // 收集同一组门的把手与铰链网格：配件前缀 = 门板名去掉最后的编号段
            //（SM_Door_Manager_01 → SM_Door_Manager_Handle_01 / SM_Door_Manager_Hinge_01）
            string accessoryPrefix = panelName.Substring(0, panelName.LastIndexOf('_'));
            var handles = new List<Transform>();
            var hinges = new List<Transform>();
            CollectPrefixed(sceneRoot, accessoryPrefix + "_Handle_", handles);
            CollectPrefixed(sceneRoot, accessoryPrefix + "_Hinge_", hinges);
            if (handles.Count == 0) log.Add("WARNING: no handles found for " + panelName);
            if (hinges.Count == 0) log.Add("WARNING: no hinges found for " + panelName + ", hinge estimated from panel bounds");

            // 铰链世界位置：优先取铰链网格包围盒中心（多铰链沿门边竖排，xz 相同）；
            // 没有铰链网格时按「把手对侧的门板边缘」水平推算（只用水平分量，
            // FBX 的 -90° 根旋转会让 panel.right 竖直，之前的中心轴旋转就是这么来的）
            Renderer hingeRenderer = hinges.Count > 0 ? hinges[0].GetComponent<Renderer>() : null;
            Renderer panelRenderer = panel.GetComponent<Renderer>();
            Vector3 hingeWorld = hingeRenderer != null
                ? hingeRenderer.bounds.center
                : FallbackHingeWorld(panel, panelRenderer, handles);

            // 门控制器挂门板，把手网格挂中继指向它
            var door = panel.GetComponent<DoorController>();
            if (door == null) door = panel.gameObject.AddComponent<DoorController>();

            var parts = new List<Transform> { panel };
            parts.AddRange(handles);
            var so = new SerializedObject(door);
            so.FindProperty("hingeLocalPosition").vector3Value = panel.InverseTransformPoint(hingeWorld);
            var partsProp = so.FindProperty("rotatingParts");
            partsProp.arraySize = parts.Count;
            for (int i = 0; i < parts.Count; i++)
                partsProp.GetArrayElementAtIndex(i).objectReferenceValue = parts[i];
            so.FindProperty("openAngle").floatValue = 100f;
            so.ApplyModifiedPropertiesWithoutUndo();

            panel.gameObject.layer = layer;
            foreach (Transform handle in handles)
            {
                handle.gameObject.layer = layer;
                var relay = handle.GetComponent<InteractableRelay>();
                if (relay == null) relay = handle.gameObject.AddComponent<InteractableRelay>();
                var relaySo = new SerializedObject(relay);
                relaySo.FindProperty("targetComponent").objectReferenceValue = door;
                relaySo.ApplyModifiedPropertiesWithoutUndo();
            }

            configured++;
            log.Add("door: " + panelName + " parts=" + parts.Count + " hinges=" + hinges.Count +
                    " hingeLocal=" + panel.InverseTransformPoint(hingeWorld).ToString("F3"));
        }

        CleanupStaleDoors(sceneRoot, log);
        return configured;
    }

    // 清理已从配置表移除的门（如出口双开门）：销毁门控制器与中继、还原层级为 Default
    private static void CleanupStaleDoors(Transform sceneRoot, List<string> log)
    {
        var configured = new HashSet<string>(DoorPanelNames);
        var staleComponents = new List<Component>();
        var staleObjects = new List<GameObject>();

        foreach (Transform t in sceneRoot.GetComponentsInChildren<Transform>(true))
        {
            DoorController door = t.GetComponent<DoorController>();
            if (door != null && !configured.Contains(t.name))
            {
                staleComponents.Add(door);
                staleObjects.Add(t.gameObject);
            }
            InteractableRelay relay = t.GetComponent<InteractableRelay>();
            if (relay != null)
            {
                var relaySo = new SerializedObject(relay);
                UnityEngine.Object target = relaySo.FindProperty("targetComponent").objectReferenceValue;
                if (target == null || !configured.Contains(target.name))
                {
                    staleComponents.Add(relay);
                    staleObjects.Add(t.gameObject);
                }
            }
        }

        foreach (Component c in staleComponents) UnityEngine.Object.DestroyImmediate(c);
        foreach (GameObject go in staleObjects)
            if (go.GetComponent<IInteractable>() == null) go.layer = 0;
        if (staleComponents.Count > 0)
            log.Add("stale door components removed: " + staleComponents.Count + " (doors no longer configured)");
    }

    // 铰链网格缺失时的兜底：门轴在「把手对侧」的门板边缘。全部用水平方向计算，
    // 门板包围盒沿把手方向的水平半宽 = |dir.x|*extents.x + |dir.z|*extents.z（AABB 投影）
    private static Vector3 FallbackHingeWorld(Transform panel, Renderer panelRenderer, List<Transform> handles)
    {
        Bounds bounds = panelRenderer != null ? panelRenderer.bounds : new Bounds(panel.position, Vector3.one);
        Vector3 center = bounds.center;

        Vector3 dir = Vector3.zero;
        if (handles.Count > 0)
        {
            foreach (Transform handle in handles) dir += handle.position;
            dir /= handles.Count;
            dir -= center;
        }
        if (dir.sqrMagnitude < 1e-6f) dir = panel.right;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.right;

        float halfWidth = Mathf.Abs(dir.x) * bounds.extents.x + Mathf.Abs(dir.z) * bounds.extents.z;
        Vector3 hinge = center - dir * Mathf.Max(halfWidth - 0.03f, 0f);
        hinge.y = center.y;
        return hinge;
    }

    private static int SetupSeats(Transform sceneRoot, int layer, List<string> log)
    {
        // 坐姿朝向参考物：办公室 FBX 的椅子网格节点旋转多为恒等旋转（transform 朝向不可靠），
        // 改为按几何关系计算——坐下面朝「最近的桌面/桌子/接待台/茶水台」方向。
        // 不含 SM_Credenza_*（多在座位身后，会把朝向带反）。
        var surfaces = new List<KeyValuePair<string, Bounds>>();
        foreach (Transform t in sceneRoot.GetComponentsInChildren<Transform>(true))
        {
            bool isSurface = t.name.StartsWith("SM_Desk_", StringComparison.Ordinal)
                          || t.name.StartsWith("SM_Table_", StringComparison.Ordinal)
                          || t.name.StartsWith("SM_Reception_", StringComparison.Ordinal)
                          || t.name.StartsWith("SM_Pantry_", StringComparison.Ordinal);
            if (!isSurface) continue;
            Renderer r = t.GetComponent<Renderer>();
            if (r != null) surfaces.Add(new KeyValuePair<string, Bounds>(t.name, r.bounds));
        }
        log.Add("seat facing reference surfaces: " + surfaces.Count + " (SM_Desk_/SM_Table_/SM_Reception_/SM_Pantry_)");

        const float maxFacingDistance = 4f; // 超过这个距离认为旁边没有可面朝的台面
        int configured = 0;
        int facingComputed = 0;
        foreach (Transform t in sceneRoot.GetComponentsInChildren<Transform>(true))
        {
            bool isSeat = t.name.StartsWith("SM_Chair_", StringComparison.Ordinal)
                          || t.name == "SM_Sofa_Reception_01";
            if (!isSeat) continue;

            var seat = t.GetComponent<SeatController>();
            if (seat == null) seat = t.gameObject.AddComponent<SeatController>();
            t.gameObject.layer = layer;

            // 计算坐姿朝向：椅子中心 → 最近参考物包围盒（水平方向）
            bool facingSet = false;
            Renderer chairRenderer = t.GetComponent<Renderer>();
            if (chairRenderer != null && surfaces.Count > 0)
            {
                Vector3 chairPos = chairRenderer.bounds.center;
                string bestName = null;
                Vector3 bestDir = Vector3.zero;
                float bestDist = float.MaxValue;
                foreach (KeyValuePair<string, Bounds> kv in surfaces)
                {
                    Vector3 nearest = ClosestPointOnXZ(kv.Value, chairPos);
                    Vector3 dir = nearest - chairPos;
                    dir.y = 0f;
                    float dist = dir.magnitude;
                    if (dist < 0.001f) dir = kv.Value.center - chairPos; // 椅子被推到台面正下方
                    dir.y = 0f;
                    if (dist < bestDist) { bestDist = dist; bestDir = dir; bestName = kv.Key; }
                }

                if (bestName != null && bestDist <= maxFacingDistance && bestDir.sqrMagnitude > 1e-6f)
                {
                    Vector3 dirN = bestDir.normalized;
                    float yaw = Mathf.Atan2(dirN.x, dirN.z) * Mathf.Rad2Deg;
                    var so = new SerializedObject(seat);
                    so.FindProperty("hasComputedFacing").boolValue = true;
                    so.FindProperty("facingYawDeg").floatValue = yaw;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    log.Add("seat: " + t.name + " -> faces " + bestName + " yaw=" + yaw.ToString("F1"));
                    facingSet = true;
                    facingComputed++;
                }
            }
            if (!facingSet) log.Add("seat: " + t.name + " -> facing fallback (transform.forward)");
            configured++;
        }
        log.Add("seats: " + configured + " (" + facingComputed + " with computed facing)");
        return configured;
    }

    // 目标点在包围盒 XZ 平面上的最近点（Y 保持原值，只做水平方向计算）
    private static Vector3 ClosestPointOnXZ(Bounds b, Vector3 pos)
    {
        return new Vector3(Mathf.Clamp(pos.x, b.min.x, b.max.x), pos.y, Mathf.Clamp(pos.z, b.min.z, b.max.z));
    }

    // 物体（含子物体）的世界包围盒
    private static Bounds WorldBounds(GameObject go)
    {
        Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) bounds.Encapsulate(r.bounds);
        return bounds;
    }

    private static int SetupPickups(Transform sceneRoot, int layer, List<string> log, Transform playerBody)
    {
        int configured = 0;
        foreach (PickupConfig config in PickupConfigs)
        {
            Transform item = FindDescendant(sceneRoot, config.ObjectName);

            // 场景里还没有这个道具（新交付的正式道具）：从 Assets 实例化并摆放
            if (item == null && config.EnsureInScene)
                item = EnsurePickupInScene(config.ObjectName, config.PlaceNearName, sceneRoot, playerBody, log);

            if (item == null)
            {
                log.Add("WARNING: pickup item '" + config.ObjectName + "' not found, skipped");
                continue;
            }
            GameObject go = item.gameObject;

            // PickupItem 组件与持握模式 + 持握旋转补偿
            var pickup = go.GetComponent<PickupItem>();
            if (pickup == null) pickup = go.AddComponent<PickupItem>();
            var so = new SerializedObject(pickup);
            so.FindProperty("carryMode").enumValueIndex = (int)config.Mode;
            so.FindProperty("holdRotationOffset").vector3Value = config.HoldRotation;
            so.FindProperty("holdAlignsToView").boolValue = config.AlignsToView;
            if (config.HoldPosition.HasValue)
                so.FindProperty("holdPositionOffset").vector3Value = config.HoldPosition.Value;
            so.ApplyModifiedPropertiesWithoutUndo();

            // 场景摆放旋转与持握一致（水杯立起来）；旋转后把最低点贴回原放置面，避免悬空/下陷
            Quaternion holdRot = Quaternion.Euler(config.HoldRotation);
            if (Quaternion.Angle(item.rotation, holdRot) > 1f)
            {
                float bottomBefore = WorldBounds(go).min.y;
                item.rotation = holdRot;
                float bottomAfter = WorldBounds(go).min.y;
                item.position += Vector3.up * (bottomBefore - bottomAfter);
            }

            // 碰撞体：换成按网格包围盒生成的 BoxCollider（非凸 MeshCollider 无法配刚体）
            foreach (MeshCollider mc in go.GetComponents<MeshCollider>())
                UnityEngine.Object.DestroyImmediate(mc);
            if (go.GetComponent<BoxCollider>() == null) go.AddComponent<BoxCollider>();
            BoxCollider box = go.GetComponent<BoxCollider>();
            Renderer rend = go.GetComponent<Renderer>();
            if (rend != null)
            {
                box.center = rend.localBounds.center;
                box.size = rend.localBounds.size;
            }

            // 刚体：默认 kinematic（只有被放下时才交给物理）
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            go.layer = layer;
            configured++;
            // 记录世界尺寸，方便核对模型比例（水杯应约 0.10m 高）
            Renderer[] rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends.Length > 0)
            {
                Bounds world = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++) world.Encapsulate(rends[i].bounds);
                log.Add("pickup: " + config.ObjectName + " mode=" + config.Mode +
                        " worldSize=" + world.size.ToString("F3") + " pos=" + go.transform.position.ToString("F2"));
            }
        }

        CleanupStalePickups(sceneRoot, log);
        return configured;
    }

    // 从 Assets 实例化道具模型并摆放：
    //   1) 配了 placeNearName（如水杯 → SM_NotebookPages_01）：放在参考物旁边（+X 侧，同高度）；
    //   2) 否则放离玩家最近的桌面中央。
    private static Transform EnsurePickupInScene(string objectName, string placeNearName, Transform sceneRoot, Transform playerBody, List<string> log)
    {
        string[] guids = AssetDatabase.FindAssets(objectName + " t:Model");
        if (guids.Length == 0)
        {
            log.Add("WARNING: no model asset named '" + objectName + "' in Assets, cannot auto-place");
            return null;
        }
        string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
        GameObject asset = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject;
        if (asset == null)
        {
            log.Add("WARNING: failed to load model asset at " + assetPath);
            return null;
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, sceneRoot);
        if (instance == null)
        {
            log.Add("WARNING: InstantiatePrefab failed for " + assetPath);
            return null;
        }
        instance.name = objectName;
        instance.transform.rotation = Quaternion.identity;

        // 道具自身的世界包围盒（算半径用）
        Bounds self = new Bounds(instance.transform.position, Vector3.zero);
        foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true)) self.Encapsulate(r.bounds);

        // 1) 参考物旁边（+X 侧，留 3cm 间隙，高度贴参考物顶面——笔记本很薄，顶面≈桌面）
        Transform placedNear = null;
        if (!string.IsNullOrEmpty(placeNearName))
        {
            Transform near = FindDescendant(sceneRoot, placeNearName);
            Renderer nearRend = near != null ? near.GetComponent<Renderer>() : null;
            if (nearRend != null)
            {
                Bounds nb = nearRend.bounds;
                float cupRadius = Mathf.Max(self.extents.x, self.extents.z);
                instance.transform.position = new Vector3(nb.max.x + cupRadius + 0.03f, nb.max.y + 0.002f, nb.center.z);
                placedNear = near;
                log.Add("placed " + objectName + " beside " + placeNearName + " at " + instance.transform.position.ToString("F2"));
            }
            else log.Add("WARNING: place-near reference '" + placeNearName + "' not found, falling back to nearest desk");
        }

        // 2) 兜底：离玩家最近的桌面中央
        if (placedNear == null)
        {
            Transform bestDesk = null;
            Bounds bestBounds = new Bounds();
            float bestDist = float.MaxValue;
            Vector3 playerPos = playerBody != null ? playerBody.position : sceneRoot.position;
            foreach (Transform t in sceneRoot.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("SM_Desk_", StringComparison.Ordinal)) continue;
                Renderer r = t.GetComponent<Renderer>();
                if (r == null) continue;
                Vector2 dxz = new Vector2(r.bounds.center.x - playerPos.x, r.bounds.center.z - playerPos.z);
                float dist = dxz.magnitude;
                if (dist < bestDist) { bestDist = dist; bestDesk = t; bestBounds = r.bounds; }
            }
            if (bestDesk != null)
            {
                instance.transform.position = new Vector3(bestBounds.center.x, bestBounds.max.y + 0.002f, bestBounds.center.z);
                log.Add("placed " + objectName + " on " + bestDesk.name + " (desk dist " + bestDist.ToString("F1") + "m)");
            }
            else
            {
                instance.transform.position = playerPos + Vector3.up * 1.2f;
                log.Add("WARNING: no desk found, " + objectName + " placed near player");
            }
        }

        // 正式道具顺带准备材质（当前只有水杯需要；灭火器到位后扩展）
        EnsureCupMaterial(item: instance.transform, log: log);
        return instance.transform;
    }

    // 生成/更新水杯材质 MAT_Cup_01（URP Lit + PolyHaven 贴图）并套到实例的全部渲染器上。
    // 陶瓷件：Metallic=0、Smoothness 用标量近似（Roughness→Smoothness 通道打包待 Astra 正式材质）。
    private static void EnsureCupMaterial(Transform item, List<string> log)
    {
        const string matPath = "Assets/Props/Materials/MAT_Cup_01.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
            {
                log.Add("WARNING: URP Lit shader not found, MAT_Cup_01 not created");
                return;
            }
            mat = new Material(lit);
            AssetDatabase.CreateAsset(mat, matPath);
        }

        Texture2D FindTex(string name)
        {
            string[] guids = AssetDatabase.FindAssets(name, new[] { "Assets/Props/Textures" });
            return guids.Length == 0 ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        Texture2D baseTex = FindTex("T_Cup_01_BaseColor");
        Texture2D normalTex = FindTex("T_Cup_01_Normal");
        Texture2D aoTex = FindTex("T_Cup_01_AO");
        if (baseTex != null) mat.SetTexture("_BaseMap", baseTex);
        if (normalTex != null)
        {
            // 法线贴图必须标记为 NormalMap 导入类型，否则会当颜色图渲染（发紫）
            string path = AssetDatabase.GetAssetPath(normalTex);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.textureType != TextureImporterType.NormalMap)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.SaveAndReimport();
            }
            mat.SetTexture("_BumpMap", normalTex);
        }
        if (aoTex != null) mat.SetTexture("_OcclusionMap", aoTex);
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0.35f);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();

        int assigned = 0;
        foreach (Renderer r in item.GetComponentsInChildren<Renderer>(true))
        {
            r.sharedMaterial = mat;
            assigned++;
        }
        log.Add("MAT_Cup_01 ready (base=" + (baseTex != null) + " normal=" + (normalTex != null) +
                " ao=" + (aoTex != null) + ") applied to " + assigned + " renderer(s)");
    }

    // 清理已从配置表移除的抓取物（如被正式道具替换的临时测试物 SM_Pen_01）：
    // 拆 PickupItem/Rigidbody/BoxCollider，恢复场景原本的静态 MeshCollider，图层还原
    private static void CleanupStalePickups(Transform sceneRoot, List<string> log)
    {
        var configured = new HashSet<string>();
        foreach (PickupConfig c in PickupConfigs) configured.Add(c.ObjectName);

        var stale = new List<PickupItem>();
        foreach (Transform t in sceneRoot.GetComponentsInChildren<Transform>(true))
        {
            PickupItem p = t.GetComponent<PickupItem>();
            if (p != null && !configured.Contains(t.name)) stale.Add(p);
        }

        foreach (PickupItem p in stale)
        {
            GameObject go = p.gameObject;
            UnityEngine.Object.DestroyImmediate(p);
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null) UnityEngine.Object.DestroyImmediate(rb);
            foreach (BoxCollider bc in go.GetComponents<BoxCollider>())
                UnityEngine.Object.DestroyImmediate(bc);
            MeshFilter mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && go.GetComponent<MeshCollider>() == null)
            {
                MeshCollider mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
            }
            if (go.GetComponent<IInteractable>() == null) go.layer = 0;
            log.Add("stale pickup removed: " + go.name);
        }
    }

    // ==== 查找工具 ====

    private static Transform FindRoot(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            if (root.name == name) return root.transform;
        return null;
    }

    private static Transform FindDescendant(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform child in root)
        {
            Transform result = FindDescendant(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private static void CollectPrefixed(Transform root, string prefix, List<Transform> results)
    {
        foreach (Transform child in root)
        {
            if (child.name.StartsWith(prefix, StringComparison.Ordinal)) results.Add(child);
            CollectPrefixed(child, prefix, results);
        }
    }

    private static string GetHierarchyPath(Transform t)
    {
        var sb = new StringBuilder(t.name);
        while (t.parent != null)
        {
            t = t.parent;
            sb.Insert(0, t.name + "/");
            sb.Insert(0, "/");
        }
        return sb.ToString();
    }

    // ==== JSON 报告（与 PlayerBodySetup 同款手写格式） ====

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

    private static void WriteReport(bool processed, string error, List<string> log)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"unityVersion\": ").Append(J(Application.unityVersion)).Append(",\n");
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
