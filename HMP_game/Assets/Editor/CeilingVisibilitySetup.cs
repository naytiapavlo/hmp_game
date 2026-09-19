// One-shot setup: attaches the CeilingVisibility toggle to the root object named
// "场景" in the currently open scene (all SM_Ceiling_* meshes live under it),
// collects the ceiling renderers and saves the scene.
// Writes Library/CeilingVisibilitySetup.json. A marker file prevents it from
// running twice; use the menu item to force a re-run.
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
public static class CeilingVisibilitySetup
{
    private const string RootObjectName = "场景";
    private const string ReportPath = "Library/CeilingVisibilitySetup.json";
    private const string MarkerPath = "Library/CeilingVisibilitySetup.done";

    static CeilingVisibilitySetup()
    {
        EditorApplication.delayCall += Run;
    }

    public static void Run()
    {
        if (File.Exists(MarkerPath)) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
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

        if (processed) File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Debug.Log("CeilingVisibilitySetup finished. processed=" + processed + " error=" + (error ?? "none")
                  + " report=" + ReportPath);
    }

    [MenuItem("Tools/Scene/Re-run Ceiling Toggle Setup")]
    public static void ForceRerun()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        Run();
    }

    private static bool ProcessScene(List<string> log)
    {
        Scene scene = SceneManager.GetActiveScene();

        GameObject root = null;
        int matches = 0;
        foreach (GameObject go in scene.GetRootGameObjects())
        {
            if (go.name != RootObjectName) continue;
            matches++;
            if (root == null) root = go;
        }
        if (root == null)
        {
            log.Add("no root object named '" + RootObjectName + "' in scene '" + scene.name + "'");
            return false;
        }
        if (matches > 1) log.Add("multiple roots named '" + RootObjectName + "' (" + matches + "), using the first");

        CeilingVisibility toggle = root.GetComponent<CeilingVisibility>();
        bool added = toggle == null;
        if (added) toggle = root.AddComponent<CeilingVisibility>();
        log.Add("CeilingVisibility added: " + added);

        // 立即收集一次天花板并按默认开关（可见）同步
        toggle.CollectCeilings();

        // 统计验证：子层级里 SM_Ceiling 前缀物体总数
        int ceilingObjects = 0;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith("SM_Ceiling", StringComparison.Ordinal)) ceilingObjects++;
        log.Add("ceiling objects found under root: " + ceilingObjects);
        log.Add("toggle state: ceilingsVisible=" + toggle.ceilingsVisible);

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveOpenScenes();
        log.Add("scene saved: " + saved + " (" + scene.path + ")");
        return true;
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

    private static void WriteReport(bool processed, string error, List<string> log)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"unityVersion\": ").Append(J(Application.unityVersion)).Append(",\n");
        sb.Append("  \"processed\": ").Append(processed ? "true" : "false").Append(",\n");
        sb.Append("  \"error\": ").Append(J(error)).Append(",\n");
        sb.Append("  \"log\": [\n    ").Append(string.Join(",\n    ", log.ConvertAll(J).ToArray())).Append("\n  ]\n}\n");

        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
    }
}
