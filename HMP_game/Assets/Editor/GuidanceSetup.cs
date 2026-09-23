// One-shot guidance system setup: for every level scene (identified by a root object named
// "场景"), creates the "路线指引" root with GuidanceSystem + SceneObstacleScanner and children
// "路线渲染" (FloorArrowRenderer) / "目的地标记" (DestinationMarker), generates the guidance
// materials (Assets/Guidance/MAT_GuidanceArrow.mat, MAT_GuidanceMarker.mat from shader
// HMProtection/GuidanceArrow) if missing, wires all serialized references and saves the scene.
// Writes Library/GuidanceSetup.json; a per-scene marker file prevents re-runs; use the menu
// item to force a re-run. Same conventions as InteractionSetup / PlayerBodySetup.
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
public static class GuidanceSetup
{
    private const string ReportPath = "Library/GuidanceSetup.json";
    private const string LayerSceneRootName = "场景";      // 有关卡内容的场景才有此根物体
    private const string GuidanceRootName = "路线指引";
    private const string ArrowMatPath = "Assets/Guidance/MAT_GuidanceArrow.mat";
    private const string MarkerMatPath = "Assets/Guidance/MAT_GuidanceMarker.mat";
    private const string ShaderName = "HMProtection/GuidanceArrow";
    private const string LegacyOfficeScene = "Assets/Scenes/办公室场景.unity";
    private const string LegacyThirdScene = "Assets/Scenes/第三场景.unity";

    static GuidanceSetup()
    {
        EditorApplication.delayCall += Run;
        // 多场景：打开关卡场景时自动检查装配
        EditorSceneManager.sceneOpened += (scene, mode) => Run();
    }

    // 每个场景独立的完成标记：Library/GuidanceSetup.<场景名>.done
    private static string MarkerPathFor(Scene scene) => "Library/GuidanceSetup." + scene.name + ".done";

    public static void Run()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(scene.name)) return;
        if (!IsLegacyOwnedScene(scene)) return;
        string markerPath = MarkerPathFor(scene);
        if (File.Exists(markerPath)) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += Run; return; }
        if (EditorApplication.isPlaying) { EditorApplication.delayCall += Run; return; }

        var log = new List<string>();
        bool processed = false;
        string error = null;
        try
        {
            processed = ProcessScene(scene, log);
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            Debug.LogException(e);
        }

        try { WriteReport(scene, processed, error, log); }
        catch (Exception e) { Debug.LogException(e); }

        if (processed) File.WriteAllText(markerPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Debug.Log("GuidanceSetup finished. scene=" + scene.name + " processed=" + processed
                  + " error=" + (error ?? "none") + " report=" + ReportPath);
    }

    private static bool IsLegacyOwnedScene(Scene scene) => scene.path == LegacyOfficeScene || scene.path == LegacyThirdScene;

    [MenuItem("Tools/Guidance/Re-run Guidance Setup")]
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
        // 只装配关卡场景（有「场景」内容根）；主菜单等非关卡场景跳过
        Transform sceneRoot = FindRoot(scene, LayerSceneRootName);
        if (sceneRoot == null)
        {
            log.Add("no root '" + LayerSceneRootName + "' in scene '" + scene.name + "' (非关卡场景，跳过)");
            return false;
        }

        // 1) 材质资产（两个关卡场景共用，只生成一次）
        Material arrowMat = EnsureMaterial(ArrowMatPath, log);
        Material markerMat = EnsureMaterial(MarkerMatPath, log);

        // 2) 指引系统根物体与组件
        Transform guidanceRoot = FindRoot(scene, GuidanceRootName);
        if (guidanceRoot == null)
        {
            var go = new GameObject(GuidanceRootName);
            SceneManager.MoveGameObjectToScene(go, scene);
            guidanceRoot = go.transform;
            log.Add("created root " + GuidanceRootName);
        }
        var system = guidanceRoot.GetComponent<GuidanceSystem>();
        if (system == null) system = guidanceRoot.gameObject.AddComponent<GuidanceSystem>();
        var scanner = guidanceRoot.GetComponent<SceneObstacleScanner>();
        if (scanner == null) scanner = guidanceRoot.gameObject.AddComponent<SceneObstacleScanner>();

        // 3) 渲染子物体
        var rendererChild = guidanceRoot.Find("路线渲染");
        if (rendererChild == null) rendererChild = new GameObject("路线渲染").transform;
        rendererChild.SetParent(guidanceRoot, false);
        var pathRenderer = rendererChild.GetComponent<FloorArrowRenderer>();
        if (pathRenderer == null) pathRenderer = rendererChild.gameObject.AddComponent<FloorArrowRenderer>();

        var markerChild = guidanceRoot.Find("目的地标记");
        if (markerChild == null) markerChild = new GameObject("目的地标记").transform;
        markerChild.SetParent(guidanceRoot, false);
        var marker = markerChild.GetComponent<DestinationMarker>();
        if (marker == null) marker = markerChild.gameObject.AddComponent<DestinationMarker>();

        // 4) 接线（SerializedObject 写私有字段，与 PlayerBodySetup 同法）
        var so = new SerializedObject(system);
        so.FindProperty("scanner").objectReferenceValue = scanner;
        so.FindProperty("pathRenderer").objectReferenceValue = pathRenderer;
        so.FindProperty("destinationMarker").objectReferenceValue = marker;
        so.FindProperty("guidanceMaterial").objectReferenceValue = arrowMat;
        so.ApplyModifiedPropertiesWithoutUndo();

        var rendererSo = new SerializedObject(pathRenderer);
        rendererSo.FindProperty("arrowMaterial").objectReferenceValue = arrowMat;
        rendererSo.ApplyModifiedPropertiesWithoutUndo();

        var markerSo = new SerializedObject(marker);
        markerSo.FindProperty("markerMaterial").objectReferenceValue = markerMat;
        markerSo.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveOpenScenes();
        log.Add("wired GuidanceSystem on /" + GuidanceRootName + " (scene saved: " + saved + ")");
        return true;
    }

    // 生成指引材质（着色器 HMProtection/GuidanceArrow；箭头/标记各一份，便于分别替换升级）
    private static Material EnsureMaterial(string path, List<string> log)
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            log.Add("WARNING: shader " + ShaderName + " not found (Assets/Guidance/Sh_GuidanceArrow.shader 应已导入)");
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var mat = new Material(shader);
        AssetDatabase.CreateAsset(mat, path);
        log.Add("created material " + path);
        return mat;
    }

    // ==== 工具 ====

    private static Transform FindRoot(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            if (root.name == name) return root.transform;
        return null;
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
