// One-shot diagnostic: dumps the object hierarchy of the active scene (names up to
// depth 4) to Library/SceneDump.json. Read-only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class SceneDump
{
    private const string ReportPath = "Library/SceneDump.json";
    private const string MarkerPath = "Library/SceneDump.done";

    static SceneDump()
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
        if (EditorApplication.isPlaying) return;

        var log = new List<string>();
        try
        {
            Scene scene = SceneManager.GetActiveScene();
            log.Add("scene: '" + scene.name + "' path=" + scene.path + " dirty=" + scene.isDirty);
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                log.Add("ROOT " + root.name + (root.activeSelf ? "" : " [inactive]")
                        + " comps=" + root.GetComponents<Component>().Length);
                DumpChildren(root.transform, 1, log);
            }
        }
        catch (Exception e)
        {
            log.Add("ERROR " + e.GetType().Name + ": " + e.Message);
        }

        var sb = new StringBuilder();
        sb.Append("{\n  \"log\": [\n    ").Append(string.Join(",\n    ", log.ConvertAll(J).ToArray())).Append("\n  ]\n}\n");
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
        File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Debug.Log("SceneDump done. report=" + ReportPath);
    }

    private static void DumpChildren(Transform t, int depth, List<string> log)
    {
        if (depth > 4) return;
        foreach (Transform c in t)
        {
            log.Add(new string(' ', depth * 2) + c.name + (c.gameObject.activeSelf ? "" : " [inactive]"));
            DumpChildren(c, depth + 1, log);
        }
    }

    private static string J(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 32) sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }
}
