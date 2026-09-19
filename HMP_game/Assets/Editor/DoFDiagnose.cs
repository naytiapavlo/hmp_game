// One-shot diagnostic: dumps the post-processing-relevant state of the active
// scene (cameras, volumes, depth-of-field override, canvases and their top-level
// UI elements) to Library/DoFDiagnose.json. Read-only, changes nothing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[InitializeOnLoad]
public static class DoFDiagnose
{
    private const string ReportPath = "Library/DoFDiagnose.json";
    private const string MarkerPath = "Library/DoFDiagnose.done";

    static DoFDiagnose()
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
        try
        {
            Dump(log);
        }
        catch (Exception e)
        {
            log.Add("ERROR " + e.GetType().Name + ": " + e.Message);
        }

        var sb = new StringBuilder();
        sb.Append("{\n  \"unityVersion\": ").Append(J(Application.unityVersion))
          .Append(",\n  \"log\": [\n    ").Append(string.Join(",\n    ", log.ConvertAll(J).ToArray())).Append("\n  ]\n}\n");
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
        File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Debug.Log("DoFDiagnose done. report=" + ReportPath);
    }

    [MenuItem("Tools/Scene/Re-run DoF Diagnose")]
    public static void ForceRerun()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        Run();
    }

    private static void Dump(List<string> log)
    {
        Scene scene = SceneManager.GetActiveScene();
        log.Add("scene: '" + scene.name + "' path=" + scene.path + " dirty=" + scene.isDirty);

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Camera c in root.GetComponentsInChildren<Camera>(true))
            {
                var data = c.GetUniversalAdditionalCameraData();
                log.Add("CAMERA '" + c.name + "' enabled=" + c.enabled + " activeInHierarchy=" + c.gameObject.activeInHierarchy
                        + " tag=" + c.tag + " depth=" + c.depth + " clearFlags=" + c.clearFlags
                        + " renderPostProcessing=" + data.renderPostProcessing
                        + " pos=" + c.transform.position.ToString("F2"));
            }
            foreach (Volume v in root.GetComponentsInChildren<Volume>(true))
            {
                string dofInfo = "none";
                if (v.sharedProfile != null && v.sharedProfile.TryGet<DepthOfField>(out var dof))
                {
                    dofInfo = "mode=" + dof.mode.value + "(override=" + dof.mode.overrideState + ")"
                              + " start=" + dof.gaussianStart.value + " end=" + dof.gaussianEnd.value
                              + " radius=" + dof.gaussianMaxRadius.value
                              + " startOvr=" + dof.gaussianStart.overrideState + " endOvr=" + dof.gaussianEnd.overrideState;
                }
                log.Add("VOLUME '" + v.name + "' enabled=" + v.enabled + " isGlobal=" + v.isGlobal
                        + " priority=" + v.priority + " profile=" + (v.sharedProfile != null ? v.sharedProfile.name : "null")
                        + " DoF[" + dofInfo + "]");
            }
            foreach (Canvas cv in root.GetComponentsInChildren<Canvas>(true))
            {
                log.Add("CANVAS '" + cv.name + "' enabled=" + cv.enabled + " active=" + cv.gameObject.activeInHierarchy
                        + " renderMode=" + cv.renderMode
                        + " worldCamera=" + (cv.worldCamera != null ? cv.worldCamera.name : "null")
                        + " sortingOrder=" + cv.sortingOrder);
                // 画布直接子物体：名字 + 锚点 + Image 颜色（检测全屏不透明底图盖住 3D 背景的情况）
                foreach (RectTransform child in cv.transform)
                {
                    var img = child.GetComponent<Image>() ?? child.GetComponentInChildren<Image>(true);
                    string imgInfo = "no-image";
                    if (img != null)
                    {
                        var rt = img.rectTransform;
                        imgInfo = "img color=" + ColorUtils(img.color)
                                  + " anchors=(" + rt.anchorMin.x.ToString("F1") + "," + rt.anchorMin.y.ToString("F1")
                                  + ")-(" + rt.anchorMax.x.ToString("F1") + "," + rt.anchorMax.y.ToString("F1") + ")"
                                  + " size=" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0")
                                  + " active=" + img.gameObject.activeInHierarchy;
                    }
                    log.Add("  UI-child '" + child.name + "' " + imgInfo);
                }
            }
        }
    }

    private static string ColorUtils(Color c)
    {
        return "(" + c.r.ToString("F2") + "," + c.g.ToString("F2") + "," + c.b.ToString("F2") + ",a=" + c.a.ToString("F2") + ")";
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
