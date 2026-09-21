// 关卡装配——《关卡架构设计（骨架版）》§四 文件清单：
//   「Editor/LevelSetup.cs：按项目 InitializeOnLoad + Library marker 模式，
//     给办公室场景装配 LevelBootstrapper 并生成锚点子物体（出生点取当前玩家位）」
//
// 装配内容（全部幂等，已存在的物体/组件不重建）：
//   1. 根物体 LevelBootstrapper（含 LevelBootstrapper + LevelFlowRunner + StageTimer）
//   2. 子物体 锚点_出生点（取当前玩家位置）
//   3. LevelFlowRunner 接线：起火点 / Scene Choices / 路线指引
//   4. OfficeFireChoiceFlow.runnerDriven = true（出题时机交给状态机）
//   5. FireEffectController.debugMode = false（F9 归 LevelFlowRunner，避免两处抢同一件事）
//
// 只处理 Assets/Scenes/办公室场景.unity；不动起火点位置、不动天花板/人物显隐开关。
using System;
using System.IO;
using System.Linq;
using System.Text;
using HMProtection.Core;
using HMProtection.Quiz;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class LevelSetup
{
    private const string TargetScenePath = "Assets/Scenes/办公室场景.unity";
    private const string Marker = "Library/LevelSetup.v1.done";
    private const string ReportPath = "Library/LevelSetup.txt";
    private const string BootRootName = "LevelBootstrapper";
    private const string SpawnAnchorName = "锚点_出生点";
    private const string FireRootName = "起火点";

    // 显式限定，避开 UnityEditor.SceneManagement 与 UnityEngine.SceneManagement 的重名风险
    private static UnityEngine.SceneManagement.Scene ActiveScene =>
        UnityEngine.SceneManagement.SceneManager.GetActiveScene();

    static LevelSetup()
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
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (File.Exists(Marker)) return;
        Install();
    }

    [MenuItem("Tools/Level1/Install Flow Runner")]
    public static void ForceInstall()
    {
        if (File.Exists(Marker)) File.Delete(Marker);
        Install();
    }

    public static void Install()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        UnityEngine.SceneManagement.Scene scene = ActiveScene;
        var log = new StringBuilder();
        try
        {
            if (scene.path != TargetScenePath)
            {
                Report("SKIPPED: 当前场景不是 " + TargetScenePath + "（当前：" + scene.path + "）");
                return;
            }

            // 先落盘用户当前的编辑，避免后续 SaveScene 把未保存改动一起覆盖掉
            if (scene.isDirty) EditorSceneManager.SaveScene(scene);

            // 1) 根物体 + 三个组件
            GameObject root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == BootRootName);
            bool createdRoot = root == null;
            if (createdRoot)
            {
                root = new GameObject(BootRootName);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            }
            var boot = GetOrAdd<LevelBootstrapper>(root);
            var runner = GetOrAdd<LevelFlowRunner>(root);
            GetOrAdd<StageTimer>(root);
            log.AppendLine("root '" + BootRootName + "' " + (createdRoot ? "created" : "already existed"));

            // 2) 出生点锚点（取当前玩家位置）
            Transform spawn = root.transform.Find(SpawnAnchorName);
            if (spawn == null)
            {
                var go = new GameObject(SpawnAnchorName);
                go.transform.SetParent(root.transform, false);
                var player = UnityEngine.Object.FindAnyObjectByType<body>();
                go.transform.position = player != null ? player.transform.position : Vector3.zero;
                spawn = go.transform;
                log.AppendLine("spawn anchor created at " + spawn.position.ToString("F2")
                               + (player != null ? " (from player)" : " (player not found, origin)"));
            }
            else log.AppendLine("spawn anchor already existed at " + spawn.position.ToString("F2"));

            // 3) 接线
            var fire = FindInScene<FireEffectController>(scene);
            var flow = FindInScene<OfficeFireChoiceFlow>(scene);
            var guide = FindInScene<GuidanceSystem>(scene);

            var runnerSo = new SerializedObject(runner);
            if (fire != null) runnerSo.FindProperty("fire").objectReferenceValue = fire;
            if (flow != null) runnerSo.FindProperty("quizFlow").objectReferenceValue = flow;
            if (guide != null) runnerSo.FindProperty("guidance").objectReferenceValue = guide;
            runnerSo.ApplyModifiedPropertiesWithoutUndo();
            log.AppendLine("wired: fire=" + Name(fire) + " quizFlow=" + Name(flow) + " guidance=" + Name(guide));

            var bootSo = new SerializedObject(boot);
            bootSo.FindProperty("defaultLevelId").stringValue = "level_1_initial_fire";
            bootSo.FindProperty("spawnAnchorPath").stringValue = SpawnAnchorName;
            bootSo.FindProperty("autoStart").boolValue = true;
            bootSo.FindProperty("teleportPlayerToSpawn").boolValue = true;
            bootSo.ApplyModifiedPropertiesWithoutUndo();
            log.AppendLine("bootstrapper: defaultLevelId=level_1_initial_fire spawn=" + SpawnAnchorName);

            // 4) 出题时机交给状态机（保持既有行为为默认，这里显式打开）
            if (flow != null)
            {
                flow.runnerDriven = true;
                EditorUtility.SetDirty(flow);
                log.AppendLine("OfficeFireChoiceFlow.runnerDriven = true（出题交给 LevelFlowRunner）");
            }
            else log.AppendLine("WARNING: 场景里没有 OfficeFireChoiceFlow，答题段会被跳过");

            // 5) F9 归 LevelFlowRunner（验收用），关掉 FireEffectController 自带的 F9
            if (fire != null)
            {
                var fireSo = new SerializedObject(fire);
                SerializedProperty debug = fireSo.FindProperty("debugMode");
                if (debug != null && debug.boolValue)
                {
                    debug.boolValue = false;
                    fireSo.ApplyModifiedPropertiesWithoutUndo();
                    log.AppendLine("FireEffectController.debugMode -> false（F9 由 LevelFlowRunner 接管）");
                }
                else log.AppendLine("FireEffectController.debugMode already false");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            log.AppendLine("scene saved: " + saved);

            File.WriteAllText(Marker, DateTime.UtcNow.ToString("o"));
            Report("SUCCESS\n" + log);
            Debug.Log("[LevelSetup] 装配完成：\n" + log);
        }
        catch (Exception e)
        {
            Report("FAIL\n" + log + "\n" + e);
            Debug.LogException(e);
        }
    }

    /// <summary>诊断：报告装配现状，不改动任何东西。</summary>
    [MenuItem("Tools/Level1/Inspect Setup")]
    public static void Inspect()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        UnityEngine.SceneManagement.Scene scene = ActiveScene;
        var sb = new StringBuilder();
        sb.AppendLine("scene: " + scene.path);
        sb.AppendLine("marker exists: " + File.Exists(Marker));

        GameObject root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == BootRootName);
        sb.AppendLine("root '" + BootRootName + "': " + (root != null ? "present" : "MISSING"));
        if (root != null)
        {
            sb.AppendLine("  LevelBootstrapper: " + (root.GetComponent<LevelBootstrapper>() != null));
            sb.AppendLine("  LevelFlowRunner:   " + (root.GetComponent<LevelFlowRunner>() != null));
            sb.AppendLine("  StageTimer:        " + (root.GetComponent<StageTimer>() != null));
            Transform spawn = root.transform.Find(SpawnAnchorName);
            sb.AppendLine("  spawn anchor: " + (spawn != null ? spawn.position.ToString("F2") : "MISSING"));
        }

        var fire = FindInScene<FireEffectController>(scene);
        sb.AppendLine("FireEffectController: " + Name(fire)
                      + (fire != null ? "  CurrentLevel=" + fire.CurrentLevel : ""));
        var flow = FindInScene<OfficeFireChoiceFlow>(scene);
        sb.AppendLine("OfficeFireChoiceFlow: " + Name(flow)
                      + (flow != null ? "  runnerDriven=" + flow.runnerDriven : ""));
        sb.AppendLine("GuidanceSystem: " + Name(FindInScene<GuidanceSystem>(scene)));

        if (HMProtection.Core.ConfigLoader.TryLoadRegistry(out LevelRegistry reg, out string regError))
            sb.AppendLine("levels.json: " + reg.levels.Length + " entry(ies)");
        else sb.AppendLine("levels.json FAILED: " + regError);
        if (HMProtection.Core.ConfigLoader.TryLoadLevel("Configs/Levels/level_1_initial_fire",
                out LevelConfigDto cfg, out string cfgError))
            sb.AppendLine("level_1_initial_fire.json: " + cfg.stages.Length + " stages, "
                          + cfg.fireCues.Length + " fireCues, quiz=" + (cfg.quiz != null));
        else sb.AppendLine("level_1_initial_fire.json FAILED: " + cfgError);

        File.WriteAllText("Library/LevelSetupInspect.txt", sb.ToString());
        Debug.Log("[LevelSetup] Inspect → Library/LevelSetupInspect.txt\n" + sb);
    }

    // ==== 工具 ====

    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        T c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

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
