// 第二关（灭火器扑救）接入——《关卡架构设计（骨架版）》§二「加一个新关卡的三步」：
//   ① 做关卡场景 + 放 LevelBootstrapper（含锚点子物体）：第三场景早就有（Scene3Setup 生成）；
//   ② 写 Resources/Configs/Levels/level_2_extinguisher_fire.json：已入库；
//   ③ levels.json 的 levels 数组加一条：已入库（选关窗口会自己多出一张卡，见 SceneSelectUI）。
// 所以「加关三步」里只剩两件既不是数据、也不该写死在代码里的事，由本工具补：
//   a) 把第三场景加进 EditorBuildSettings（不加则运行时按场景名找不到，选关窗口会明确报错）；
//   b) 第三场景 LevelBootstrapper 接线：defaultLevelId=level_2_…、autoStart=true
//      （它原本是测试场景，autoStart 故意关着；出生点锚点缺失时按场景里的玩家位置补一个）。
//
// 与 LevelSetup（第一关）同规：InitializeOnLoad + Library 标记 + 菜单强制重跑；
// 第三场景以 Additive 方式打开、改完保存关闭，**不会保存/改动你当前打开的场景**；Play 模式下不动作。
//
// 不管的事：不动第三场景的起火点位置、不动 FireEffectController.debugMode（F9 归属见下方日志）、
// 不动协作者的选关预制体与主菜单。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HMProtection.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class Level2Setup
{
    private const string ScenePath = "Assets/Scenes/第三场景.unity";
    private const string Marker = "Library/Level2Setup.v1.done";
    private const string ReportPath = "Library/Level2Setup.txt";
    private const string InspectPath = "Library/Level2SetupInspect.txt";
    private const string LevelId = "level_2_extinguisher_fire";
    private const string BootRootName = "LevelBootstrapper";
    private const string SpawnAnchorName = "锚点_出生点";

    static Level2Setup()
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
        // Play 中不改场景，退出播放后再补（与 Scene3Setup / FireSetup 同法）
        if (EditorApplication.isPlaying)
        {
            EditorApplication.delayCall += Auto;
            return;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (File.Exists(Marker)) return;
        Install();
    }

    [MenuItem("Tools/Level2/接入第二关（构建列表 + 场景接线）")]
    public static void ForceInstall()
    {
        if (File.Exists(Marker)) File.Delete(Marker);
        Install();
    }

    public static void Install()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var log = new StringBuilder();
        string error = null;
        try { InstallInternal(log); }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            Debug.LogException(e);
        }

        try { Report(error == null ? "SUCCESS\n" + log : "FAIL\n" + log + "\n" + error); }
        catch (Exception e) { Debug.LogException(e); }

        if (error == null)
        {
            Directory.CreateDirectory("Library");
            File.WriteAllText(Marker, DateTime.UtcNow.ToString("o"));
            Debug.Log("[Level2] 第二关接入完成：\n" + log);
        }
    }

    private static void InstallInternal(StringBuilder log)
    {
        // 1) 数据侧核对：注册表里必须有这一关（JSON 是手写的，工具只读不写）
        if (ConfigLoader.TryLoadRegistry(out LevelRegistry registry, out string registryError))
        {
            LevelEntry entry = null;
            for (int i = 0; i < registry.levels.Length; i++)
                if (registry.levels[i] != null && registry.levels[i].id == LevelId) entry = registry.levels[i];
            if (entry == null) log.AppendLine("WARNING: levels.json 里没有 " + LevelId + "，第二关不会出现在选关窗口");
            else log.AppendLine("注册表：" + entry.id + " → 场景「" + entry.sceneName + "」／卡片 " + entry.cardTitle
                                + "（order " + entry.order + "，status " + entry.status + "）");
        }
        else log.AppendLine("WARNING: 关卡注册表读取失败：" + registryError);

        // 2) 构建列表（运行时按场景名反查路径，没进列表就找不到）
        log.AppendLine(EnsureInBuildSettings());

        // 3) 场景接线
        bool openedByUs = false;
        UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
        if (!scene.IsValid() || !scene.isLoaded)
        {
            if (!File.Exists(ScenePath)) { log.AppendLine("FAIL: 找不到场景 " + ScenePath); return; }
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            openedByUs = true;
        }
        try
        {
            bool changed = false;
            GameObject root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == BootRootName);
            if (root == null)
            {
                root = new GameObject(BootRootName);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                log.AppendLine("根物体 '" + BootRootName + "' 不存在 → 已创建");
                changed = true;
            }
            var boot = root.GetComponent<LevelBootstrapper>();
            if (boot == null) { boot = root.AddComponent<LevelBootstrapper>(); changed = true; }
            if (root.GetComponent<LevelFlowRunner>() == null) { root.AddComponent<LevelFlowRunner>(); changed = true; }
            if (root.GetComponent<StageTimer>() == null) { root.AddComponent<StageTimer>(); changed = true; }
            log.AppendLine("根物体 '" + BootRootName + "'：LevelBootstrapper + LevelFlowRunner + StageTimer 就位");

            Transform anchor = root.transform.Find(SpawnAnchorName);
            if (anchor == null)
            {
                var player = FindInScene<body>(scene);
                if (player == null)
                    log.AppendLine("WARNING: 出生点锚点缺失，且场景里没有玩家（body），未创建——进关时玩家不会被定位");
                else
                {
                    var go = new GameObject(SpawnAnchorName);
                    go.transform.SetParent(root.transform, false);
                    go.transform.position = player.transform.position;
                    anchor = go.transform;
                    changed = true;
                    log.AppendLine("出生点锚点已按场景里的玩家位置创建：" + anchor.position.ToString("F2"));
                }
            }
            else log.AppendLine("出生点锚点已在：" + anchor.position.ToString("F2"));

            // 只在值不对时才写：避免无意义地重存整个场景（Unity 重序列化会制造大段 diff 噪声）
            var bootSo = new SerializedObject(boot);
            SerializedProperty idProp = bootSo.FindProperty("defaultLevelId");
            SerializedProperty spawnProp = bootSo.FindProperty("spawnAnchorPath");
            SerializedProperty autoProp = bootSo.FindProperty("autoStart");
            SerializedProperty teleportProp = bootSo.FindProperty("teleportPlayerToSpawn");
            bool bootNeedsWrite = idProp.stringValue != LevelId || spawnProp.stringValue != SpawnAnchorName
                                  || !autoProp.boolValue || !teleportProp.boolValue;
            if (bootNeedsWrite)
            {
                idProp.stringValue = LevelId;
                spawnProp.stringValue = SpawnAnchorName;
                autoProp.boolValue = true;
                teleportProp.boolValue = true;
                bootSo.ApplyModifiedPropertiesWithoutUndo();
                changed = true;
                log.AppendLine("LevelBootstrapper 已写入：defaultLevelId=" + LevelId + "，spawn=" + SpawnAnchorName + "，autoStart=true");
            }
            else log.AppendLine("LevelBootstrapper 已是目标值（defaultLevelId=" + LevelId + "，autoStart=true），未改动");

            var fire = FindInScene<FireEffectController>(scene);
            log.AppendLine("起火点 FireEffectController：" + (fire != null ? Name(fire) : "MISSING")
                           + (fire != null ? "，debugMode=" + fire.DebugKeyEnabled + " → F9 归"
                                             + (fire.DebugKeyEnabled ? "火源自己（LevelFlowRunner 自动让出）" : "LevelFlowRunner") : ""));
            var guide = FindInScene<GuidanceSystem>(scene);
            log.AppendLine("路线指引 GuidanceSystem：" + (guide != null ? Name(guide) : "MISSING（freeRoam 段仍可正常跑）"));

            if (changed)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                log.AppendLine("场景已保存：" + EditorSceneManager.SaveScene(scene));
            }
            else log.AppendLine("场景无需改动，未重新保存（避免重序列化噪声）");
        }
        finally
        {
            if (openedByUs) EditorSceneManager.CloseScene(scene, true);
        }
    }

    /// <summary>诊断：报告第二关接入现状，不改动任何东西。</summary>
    [MenuItem("Tools/Level2/检查第二关接入")]
    public static void Inspect()
    {
        var sb = new StringBuilder();
        sb.AppendLine("level id: " + LevelId);
        sb.AppendLine("marker: " + Marker + " exists=" + File.Exists(Marker));

        var scenes = EditorBuildSettings.scenes;
        int index = Array.FindIndex(scenes, s => s != null && s.path == ScenePath);
        sb.AppendLine("build settings: " + (index < 0 ? "MISSING" : "index " + index + "，enabled=" + scenes[index].enabled));
        sb.AppendLine("构建列表共 " + scenes.Length + " 个场景");
        for (int i = 0; i < scenes.Length; i++)
            if (scenes[i] != null) sb.AppendLine("  [" + i + "] " + (scenes[i].enabled ? "on " : "off") + "  " + scenes[i].path);

        if (ConfigLoader.TryLoadRegistry(out LevelRegistry registry, out string registryError))
        {
            sb.AppendLine("levels.json: " + registry.levels.Length + " 条");
            for (int i = 0; i < registry.levels.Length; i++)
            {
                LevelEntry e = registry.levels[i];
                if (e == null) continue;
                sb.AppendLine("  " + e.order + ". " + e.id + "  " + e.displayName + "  场景=" + e.sceneName
                              + "  状态=" + e.status + "  卡片=" + e.cardTitle);
                if (!ConfigLoader.TryLoadLevel(e.configPath, out LevelConfigDto cfg, out string cfgError))
                { sb.AppendLine("     配置读取失败：" + cfgError); continue; }
                var kinds = new List<string>();
                for (int s = 0; s < cfg.stages.Length; s++) kinds.Add(cfg.stages[s] != null ? cfg.stages[s].kind : "?");
                sb.AppendLine("     阶段 " + cfg.stages.Length + " 段 [" + string.Join(",", kinds) + "]，火势 cue "
                              + cfg.fireCues.Length + " 条");
            }
        }
        else sb.AppendLine("levels.json FAILED: " + registryError);

        sb.AppendLine(DescribeScene());
        File.WriteAllText(InspectPath, sb.ToString());
        Debug.Log("[Level2] Inspect → " + InspectPath + "\n" + sb);
    }

    private static string DescribeScene()
    {
        var sb = new StringBuilder();
        if (!File.Exists(ScenePath)) return "场景 " + ScenePath + " 不存在";
        bool openedByUs = false;
        UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            openedByUs = true;
        }
        try
        {
            GameObject root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == BootRootName);
            sb.AppendLine("场景 " + ScenePath + "：根物体 " + scene.GetRootGameObjects().Length + " 个");
            if (root == null) { sb.AppendLine("  " + BootRootName + ": MISSING"); return sb.ToString(); }
            var boot = root.GetComponent<LevelBootstrapper>();
            if (boot == null) sb.AppendLine("  LevelBootstrapper: MISSING");
            else
            {
                var so = new SerializedObject(boot);
                sb.AppendLine("  LevelBootstrapper.defaultLevelId = " + so.FindProperty("defaultLevelId").stringValue);
                sb.AppendLine("  LevelBootstrapper.autoStart     = " + so.FindProperty("autoStart").boolValue);
                sb.AppendLine("  LevelBootstrapper.spawnPath     = " + so.FindProperty("spawnAnchorPath").stringValue);
            }
            Transform anchor = root.transform.Find(SpawnAnchorName);
            sb.AppendLine("  出生点锚点: " + (anchor != null ? anchor.position.ToString("F2") : "MISSING"));
        }
        finally
        {
            if (openedByUs) EditorSceneManager.CloseScene(scene, true);
        }
        return sb.ToString();
    }

    private static string EnsureInBuildSettings()
    {
        if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(ScenePath)))
            return "FAIL: AssetDatabase 里找不到 " + ScenePath;
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        int index = scenes.FindIndex(s => s != null && s.path == ScenePath);
        if (index >= 0)
        {
            if (scenes[index].enabled) return "构建列表：已存在且已启用（index " + index + "）";
            scenes[index].enabled = true;
            EditorBuildSettings.scenes = scenes.ToArray();
            return "构建列表：已存在但被禁用 → 已启用（index " + index + "）";
        }
        scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        return "构建列表：已加入 " + ScenePath + " → index " + (scenes.Count - 1);
    }

    // ==== 工具 ====

    private static T FindInScene<T>(UnityEngine.SceneManagement.Scene scene) where T : Component
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            T hit = root.GetComponentInChildren<T>(true);
            if (hit != null) return hit;
        }
        return null;
    }

    private static string Name(UnityEngine.Object o) => o == null ? "(null)" : o.name;

    private static void Report(string text)
    {
        Directory.CreateDirectory("Library");
        File.WriteAllText(ReportPath, text);
    }
}
