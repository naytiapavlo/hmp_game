// One-shot deletion: removes every object whose name starts with "SM_Ceiling"
// from the CURRENTLY ACTIVE scene in the open editor (including unsaved state),
// then saves that scene. If none are found, changes nothing and only reports.
// Writes Library/CeilingDelete.json. A marker file prevents it from running
// twice; use the menu item to force a re-run.
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
public static class CeilingDeleteSetup
{
    private const string CeilingPrefix = "SM_Ceiling";
    private const string ReportPath = "Library/CeilingDelete.json";
    private const string MarkerPath = "Library/CeilingDelete.done";

    static CeilingDeleteSetup()
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
        Debug.Log("CeilingDeleteSetup finished. processed=" + processed + " error=" + (error ?? "none")
                  + " report=" + ReportPath);
    }

    [MenuItem("Tools/Scene/Re-run Ceiling Delete")]
    public static void ForceRerun()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        Run();
    }

    private static bool ProcessScene(List<string> log)
    {
        // 只处理编辑器当前打开的活动场景（含未保存的修改），不动磁盘上其他场景文件
        Scene scene = SceneManager.GetActiveScene();
        log.Add("active scene: '" + scene.name + "' path=" + (scene.path ?? "(untitled)"));

        // 收集所有名字以 SM_Ceiling 开头的物体（先拷贝到列表，边删边遍历会跳项）
        var targets = new List<Transform>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith(CeilingPrefix, StringComparison.Ordinal)) targets.Add(t);
            }
        }
        log.Add("SM_Ceiling objects found: " + targets.Count);

        // 一个都没有：什么都不改、不保存，只留记录
        if (targets.Count == 0)
        {
            log.Add("nothing to delete, scene left untouched");
            return true;
        }

        for (int i = 0; i < targets.Count && i < 10; i++)
            log.Add("sample: " + targets[i].name);
        if (targets.Count > 10) log.Add("... and " + (targets.Count - 10) + " more");

        // 逐个删除；若删了父物体，其子物体随之销毁，后面的空引用直接跳过
        int destroyed = 0;
        foreach (Transform t in targets)
        {
            if (t == null) continue;
            UnityEngine.Object.DestroyImmediate(t.gameObject);
            destroyed++;
        }

        // 校验删干净了
        int remaining = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith(CeilingPrefix, StringComparison.Ordinal)) remaining++;
            }
        }
        log.Add("destroyed: " + destroyed + " remaining: " + remaining);

        if (destroyed > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = scene.path != null && scene.path.Length > 0
                ? EditorSceneManager.SaveScene(scene)
                : EditorSceneManager.SaveOpenScenes();
            log.Add("scene saved: " + saved);
        }
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
