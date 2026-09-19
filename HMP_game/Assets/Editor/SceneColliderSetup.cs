// One-shot scene collider setup: finds the root empty object named "场景" in the
// currently open scene and adds a (non-convex, static) MeshCollider to every descendant
// that renders a mesh and has no collider yet. Organizational transforms without meshes
// are skipped. Saves the scene and writes a verification report to
// Library/SceneColliderSetup.json. A marker file prevents it from running twice;
// use the menu item to force a re-run (idempotent: existing colliders are left alone).
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
public static class SceneColliderSetup
{
    private const string RootObjectName = "场景";
    private const string ReportPath = "Library/SceneColliderSetup.json";
    private const string MarkerPath = "Library/SceneColliderSetup.done";

    private sealed class Stats
    {
        public bool RootFound;
        public string ScenePath = "";
        public string SceneName = "";
        public int TransformsTotal;
        public int MeshNodes;
        public int Added;
        public int SkippedExistingCollider;
        public int SkippedNoMesh;
        public int SkippedRendererWithoutMesh;
        public bool SceneSaved;
        public readonly List<string> ExistingColliderNodes = new List<string>();
        public readonly List<string> RendererWithoutMeshNodes = new List<string>();
        public readonly List<string> Errors = new List<string>();
    }

    static SceneColliderSetup()
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

        Stats stats = null;
        string error = null;
        try
        {
            stats = ProcessScene();
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            Debug.LogException(e);
        }

        try
        {
            WriteReport(stats, error);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }

        // Only mark done when the root was actually found and processed, so the setup
        // retries on the next domain reload if the right scene was not open yet.
        if (stats != null && stats.RootFound) File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Debug.Log("SceneColliderSetup finished. rootFound=" + (stats != null && stats.RootFound)
                  + " added=" + (stats != null ? stats.Added.ToString() : "?")
                  + " error=" + (error ?? "none") + " report=" + ReportPath);
    }

    [MenuItem("Tools/Scene/Re-run Collider Setup")]
    public static void ForceRerun()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        Run();
    }

    private static Stats ProcessScene()
    {
        var stats = new Stats();
        Scene scene = SceneManager.GetActiveScene();
        stats.SceneName = scene.name;
        stats.ScenePath = scene.path;

        Transform root = null;
        int rootsNamed = 0;
        foreach (GameObject go in scene.GetRootGameObjects())
        {
            if (go.name != RootObjectName) continue;
            rootsNamed++;
            if (root == null) root = go.transform;
        }
        if (root == null)
        {
            stats.Errors.Add("no root object named '" + RootObjectName + "' in scene '" + stats.SceneName + "'");
            return stats;
        }
        stats.RootFound = true;
        if (rootsNamed > 1) stats.Errors.Add("multiple roots named '" + RootObjectName + "' (" + rootsNamed + "), using the first");

        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        stats.TransformsTotal = all.Length - 1; // exclude the organizational root itself

        foreach (Transform t in all)
        {
            if (t == root) continue;
            try
            {
                Collider existing = t.GetComponent<Collider>();
                MeshFilter mf = t.GetComponent<MeshFilter>();
                bool hasMesh = mf != null && mf.sharedMesh != null;

                if (existing != null)
                {
                    stats.SkippedExistingCollider++;
                    if (stats.ExistingColliderNodes.Count < 50)
                        stats.ExistingColliderNodes.Add(t.name + " (" + existing.GetType().Name + ")");
                    continue;
                }
                if (!hasMesh)
                {
                    if (t.GetComponent<Renderer>() != null)
                    {
                        stats.SkippedRendererWithoutMesh++;
                        if (stats.RendererWithoutMeshNodes.Count < 50)
                            stats.RendererWithoutMeshNodes.Add(t.name);
                    }
                    else stats.SkippedNoMesh++;
                    continue;
                }

                stats.MeshNodes++;
                var mc = t.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh; // non-convex static collider, matches the render geometry
                stats.Added++;
            }
            catch (Exception e)
            {
                stats.Errors.Add(t.name + ": " + e.GetType().Name + ": " + e.Message);
            }
        }

        if (stats.Added > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            stats.SceneSaved = EditorSceneManager.SaveOpenScenes();
        }
        return stats;
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

    private static void WriteReport(Stats stats, string error)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"unityVersion\": ").Append(J(Application.unityVersion)).Append(",\n");
        sb.Append("  \"processed\": ").Append(stats != null && stats.RootFound ? "true" : "false").Append(",\n");
        sb.Append("  \"error\": ").Append(J(error)).Append(",\n");
        if (stats != null)
        {
            sb.Append("  \"sceneName\": ").Append(J(stats.SceneName)).Append(",\n");
            sb.Append("  \"scenePath\": ").Append(J(stats.ScenePath)).Append(",\n");
            sb.Append("  \"rootFound\": ").Append(stats.RootFound ? "true" : "false").Append(",\n");
            sb.Append("  \"childTransforms\": ").Append(stats.TransformsTotal).Append(",\n");
            sb.Append("  \"meshNodes\": ").Append(stats.MeshNodes).Append(",\n");
            sb.Append("  \"meshCollidersAdded\": ").Append(stats.Added).Append(",\n");
            sb.Append("  \"skippedExistingCollider\": ").Append(stats.SkippedExistingCollider).Append(",\n");
            sb.Append("  \"skippedNoMeshGroupNodes\": ").Append(stats.SkippedNoMesh).Append(",\n");
            sb.Append("  \"skippedRendererWithoutMesh\": ").Append(stats.SkippedRendererWithoutMesh).Append(",\n");
            sb.Append("  \"sceneSaved\": ").Append(stats.SceneSaved ? "true" : "false").Append(",\n");
            sb.Append("  \"existingColliderNodes\": [")
              .Append(string.Join(", ", stats.ExistingColliderNodes.ConvertAll(J).ToArray())).Append("],\n");
            sb.Append("  \"rendererWithoutMeshNodes\": [")
              .Append(string.Join(", ", stats.RendererWithoutMeshNodes.ConvertAll(J).ToArray())).Append("],\n");
        }
        var errs = new List<string>(stats != null ? stats.Errors : new List<string>());
        if (error != null) errs.Add(error);
        sb.Append("  \"errors\": [").Append(string.Join(", ", errs.ConvertAll(J).ToArray())).Append("]\n}\n");

        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
    }
}
