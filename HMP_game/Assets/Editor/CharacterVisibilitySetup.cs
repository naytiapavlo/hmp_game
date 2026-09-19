// One-shot setup: attaches the generic VisibilityToggle to the root object named
// "人物" (the player rig: body capsule + first-person hands) in the currently open
// scene, collects the renderers and saves the scene.
// Writes Library/CharacterVisibilitySetup.json. A marker file prevents it from
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
public static class CharacterVisibilitySetup
{
    private const string RootObjectName = "人物";
    private const string ReportPath = "Library/CharacterVisibilitySetup.json";
    private const string MarkerPath = "Library/CharacterVisibilitySetup.done";

    static CharacterVisibilitySetup()
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
        Debug.Log("CharacterVisibilitySetup finished. processed=" + processed + " error=" + (error ?? "none")
                  + " report=" + ReportPath);
    }

    [MenuItem("Tools/Scene/Re-run Character Toggle Setup")]
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

        VisibilityToggle toggle = root.GetComponent<VisibilityToggle>();
        bool added = toggle == null;
        if (added) toggle = root.AddComponent<VisibilityToggle>();
        log.Add("VisibilityToggle added: " + added);

        // 立即收集一次并按默认开关（可见）同步
        toggle.Collect();

        // 列出收集到的渲染器明细，验证身体胶囊和双手都被覆盖
        int mesh = 0, skinned = 0, other = 0;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r is SkinnedMeshRenderer) skinned++;
            else if (r is MeshRenderer) mesh++;
            else other++;
        }
        log.Add("renderers under 人物: mesh=" + mesh + " skinned=" + skinned + " other=" + other);
        log.Add("toggle state: visible=" + toggle.visible);

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
