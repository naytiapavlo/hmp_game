// One-shot setup: attaches the AutoRotate component to the object named "旋转轴"
// in the currently active scene and saves. If the editor is in play mode it waits
// for exit first (components added during play do not persist).
// Writes Library/AutoRotateSetup.json. A marker file prevents it from running
// twice; use the menu item to force a re-run.
// force editor asm rebuild
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
public static class AutoRotateSetup
{
    // 场景里的物体实际名为「旋轴转」（字序颠倒），同时兼容正确写法「旋转轴」
    private static readonly string[] TargetNames = { "旋轴转", "旋转轴" };
    private const string ReportPath = "Library/AutoRotateSetup.json";
    private const string MarkerPath = "Library/AutoRotateSetup.done";

    static AutoRotateSetup()
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

        // Play 模式下挂的组件退出后不会保留，等退出 Play 再执行
        if (EditorApplication.isPlaying)
        {
            EditorApplication.playModeStateChanged += RetryAfterPlayMode;
            Debug.Log("AutoRotateSetup: waiting for play mode to exit...");
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
        Debug.Log("AutoRotateSetup finished. processed=" + processed + " error=" + (error ?? "none")
                  + " report=" + ReportPath);
    }

    private static void RetryAfterPlayMode(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= RetryAfterPlayMode;
        Run();
    }

    [MenuItem("Tools/Scene/Re-run AutoRotate Setup")]
    public static void ForceRerun()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        Run();
    }

    private static bool ProcessScene(List<string> log)
    {
        Scene scene = SceneManager.GetActiveScene();
        log.Add("active scene: '" + scene.name + "' path=" + scene.path);

        GameObject target = null;
        int matches = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (Array.IndexOf(TargetNames, t.name) < 0) continue;
                matches++;
                if (target == null) target = t.gameObject;
            }
        }
        if (target == null)
        {
            log.Add("no object named '" + string.Join("' or '", TargetNames) + "' found");
            return false;
        }
        if (matches > 1) log.Add("multiple candidate objects (" + matches + "), using the first");

        log.Add("target: " + GetPath(target.transform) + " childCount=" + target.transform.childCount
                + " pos=" + target.transform.position.ToString("F2"));

        AutoRotate rotator = target.GetComponent<AutoRotate>();
        bool added = rotator == null;
        if (added) rotator = target.AddComponent<AutoRotate>();
        log.Add("AutoRotate added: " + added + " (rotating=true speed=5°/s axis=Y)");

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        log.Add("scene saved: " + saved);
        return true;
    }

    private static string GetPath(Transform t)
    {
        var sb = new StringBuilder(t.name);
        while (t.parent != null)
        {
            t = t.parent;
            sb.Insert(0, t.name + "/");
        }
        return sb.ToString();
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
