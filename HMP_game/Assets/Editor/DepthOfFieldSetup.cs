// One-shot setup: enables URP post-processing on the main camera of the currently
// active scene and adds a Global Volume with a Gaussian Depth of Field override
// (background blur for the menu backdrop). uGUI in Screen Space - Overlay mode is
// drawn after post-processing and therefore stays sharp. Writes
// Library/DepthOfFieldSetup.json. A marker file prevents it from running twice;
// use the menu item to force a re-run.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class DepthOfFieldSetup
{
    private const string ProfilePath = "Assets/Settings/InitUI_VolumeProfile.asset";
    private const string VolumeObjectName = "Global Volume";
    private const string ReportPath = "Library/DepthOfFieldSetup.json";
    private const string MarkerPath = "Library/DepthOfFieldSetup.done";

    static DepthOfFieldSetup()
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
        Debug.Log("DepthOfFieldSetup finished. processed=" + processed + " error=" + (error ?? "none")
                  + " report=" + ReportPath);
    }

    [MenuItem("Tools/Scene/Re-run Depth Of Field Setup")]
    public static void ForceRerun()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        Run();
    }

    private static bool ProcessScene(List<string> log)
    {
        Scene scene = SceneManager.GetActiveScene();
        log.Add("active scene: '" + scene.name + "' path=" + scene.path);

        // 1) 找主相机：优先 MainCamera 标签，退而求其次取第一个启用的相机
        Camera cam = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Camera c in root.GetComponentsInChildren<Camera>(true))
            {
                if (cam == null || c.CompareTag("MainCamera")) cam = c;
                if (c.CompareTag("MainCamera")) break;
            }
            if (cam != null && cam.CompareTag("MainCamera")) break;
        }
        if (cam == null)
        {
            log.Add("no camera found in scene, abort");
            return false;
        }
        log.Add("camera: " + cam.name + " pos=" + cam.transform.position.ToString("F2"));

        // 2) 打开相机的后处理开关（URP 的 UniversalAdditionalCameraData）
        UniversalAdditionalCameraData camData = cam.GetUniversalAdditionalCameraData();
        camData.renderPostProcessing = true;
        log.Add("camera post-processing enabled: " + camData.renderPostProcessing);

        // 3) 创建/复用 Volume Profile 资产，加入高斯景深（背景虚化）参数
        VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = Path.GetFileNameWithoutExtension(ProfilePath);
            AssetDatabase.CreateAsset(profile, ProfilePath);
            log.Add("volume profile created: " + ProfilePath);
        }
        else log.Add("volume profile reused: " + ProfilePath);

        if (!profile.TryGet<DepthOfField>(out var dof))
        {
            // 清掉历史遗留的空引用条目（components: {fileID: 0} 那种），再补新覆盖
            profile.components.RemoveAll(c => c == null);
            dof = profile.Add<DepthOfField>(true);
            // 关键：覆盖项是独立的 ScriptableObject，必须显式登记为资产子对象，
            // 否则保存后引用为空，域重载一发生景深就消失
            AssetDatabase.AddObjectToAsset(dof, profile);
            log.Add("Depth of Field override created and embedded into asset");
        }
        else log.Add("Depth of Field override already present");
        dof.mode.value = DepthOfFieldMode.Gaussian;   // 高斯虚化：便宜、平滑，适合菜单背景
        dof.mode.overrideState = true;
        // 距相机 1m 内保持清晰，6m 起完全虚化（办公室背景约 2~18m，整体呈柔和虚化）
        dof.gaussianStart.value = 1f;
        dof.gaussianStart.overrideState = true;
        dof.gaussianEnd.value = 6f;
        dof.gaussianEnd.overrideState = true;
        dof.gaussianMaxRadius.value = 1.2f;
        dof.gaussianMaxRadius.overrideState = true;
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        // 从磁盘强制重载一次并验证覆盖项真实存在，防止再次出现“内存里有、文件里没有”
        AssetDatabase.ImportAsset(ProfilePath);
        var reloaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        bool ok = reloaded != null && reloaded.TryGet<DepthOfField>(out var check) && check.mode.value == DepthOfFieldMode.Gaussian;
        log.Add("verify after reload from disk: DoF persisted=" + ok);
        if (!ok) log.Add("WARNING: DoF override still not persisted, check " + ProfilePath);
        log.Add("Depth of Field (Gaussian) start=1 end=6 maxRadius=1.2");

        // 4) 创建/复用全局 Volume 物体，挂上 Profile（只影响本场景）
        Volume volume = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Volume v in root.GetComponentsInChildren<Volume>(true))
            {
                if (v.gameObject.name == VolumeObjectName) { volume = v; break; }
            }
            if (volume != null) break;
        }
        bool addedVolume = volume == null;
        if (addedVolume)
        {
            var go = new GameObject(VolumeObjectName);
            Undo.RegisterCreatedObjectUndo(go, "Create Global Volume");
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            volume = go.AddComponent<Volume>();
        }
        volume.isGlobal = true;
        volume.priority = 0;
        volume.sharedProfile = profile;
        log.Add("Global Volume " + (addedVolume ? "created" : "reused") + ", isGlobal=" + volume.isGlobal);

        // 5) UI 保护检查：Screen Space - Overlay 画布绘制在后处理之后，天然不受影响；
        //    Screen Space - Camera / World Space 画布会被虚化，只提示不强改（保持用户设计）
        int overlay = 0, affected = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Canvas cv in root.GetComponentsInChildren<Canvas>(true))
            {
                if (cv.renderMode == RenderMode.ScreenSpaceOverlay) overlay++;
                else
                {
                    affected++;
                    log.Add("WARNING: Canvas '" + cv.name + "' uses " + cv.renderMode
                            + " — 会被后处理影响，如需 UI 保持清晰请改为 Screen Space - Overlay");
                }
            }
        }
        log.Add("canvases: overlay=" + overlay + " would-be-affected=" + affected
                + (overlay + affected == 0 ? "（场景暂无 UI；以后新建 Canvas 默认就是 Overlay 模式，不受影响）" : ""));

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        log.Add("scene saved: " + saved);
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
