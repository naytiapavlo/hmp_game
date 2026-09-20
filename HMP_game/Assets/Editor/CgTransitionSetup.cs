// One-shot setup (touch): imports the fire CG video (ui/cg动画/起火.mp4 at the repository
// root) into Assets/CG, creates a matching RenderTexture, builds the root-level
// "CG转场" canvas (overlay, above all UI, no raycaster) with a CgTransitionPlayer,
// wires it into SceneSelectUI.cgTransition and saves the scene.
// Writes Library/CgTransitionSetup.json. A marker file prevents it from running
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
using UnityEngine.UI;
using UnityEngine.Video;

[InitializeOnLoad]
public static class CgTransitionSetup
{
    private const string SourceVideo = "ui/cg动画/起火.mp4"; // 相对仓库根（工程目录的上一级）
    private const string VideoPath = "Assets/CG/起火.mp4";
    private const string RtPath = "Assets/CG/CG_RT.renderTexture";
    private const string RootName = "CG转场";
    private const string ReportPath = "Library/CgTransitionSetup.json";
    private const string MarkerPath = "Library/CgTransitionSetup.done";

    static CgTransitionSetup()
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
        if (EditorApplication.isPlaying)
        {
            EditorApplication.playModeStateChanged += RetryAfterPlayMode;
            Debug.Log("CgTransitionSetup: waiting for play mode to exit...");
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
        Debug.Log("CgTransitionSetup finished. processed=" + processed + " error=" + (error ?? "none")
                  + " report=" + ReportPath);
    }

    private static void RetryAfterPlayMode(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= RetryAfterPlayMode;
        Run();
    }

    [MenuItem("Tools/Scene/Re-run CG Transition Setup")]
    public static void ForceRerun()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        Run();
    }

    private static bool ProcessScene(List<string> log)
    {
        // 1) 把 CG 视频拷进工程（已存在则复用）
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/初始界面.unity")
        {
            log.Add("Waiting for the main menu scene; no scene modified.");
            return false;
        }
        string repoRoot = Directory.GetParent(Directory.GetParent(Application.dataPath).FullName).FullName;
        string source = Path.Combine(repoRoot, SourceVideo.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(VideoPath))
        {
            if (!File.Exists(source))
            {
                log.Add("ERROR: source video not found: " + source);
                return false;
            }
            Directory.CreateDirectory("Assets/CG");
            File.Copy(source, VideoPath, false);
            AssetDatabase.ImportAsset(VideoPath);
            log.Add("video copied into project: " + VideoPath);
        }
        else log.Add("video already in project: " + VideoPath);

        VideoClip clip = AssetDatabase.LoadAssetAtPath<VideoClip>(VideoPath);
        if (clip == null)
        {
            log.Add("ERROR: video clip failed to import (unsupported codec?)");
            return false;
        }
        log.Add("clip: " + clip.width + "x" + clip.height + " " + clip.length.ToString("F1") + "s " + clip.frameRate.ToString("F0") + "fps");

        // 2) 匹配视频尺寸的 RenderTexture
        RenderTexture rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(RtPath);
        if (rt == null)
        {
            rt = new RenderTexture((int)clip.width, (int)clip.height, 0, RenderTextureFormat.ARGB32)
            {
                name = Path.GetFileNameWithoutExtension(RtPath)
            };
            AssetDatabase.CreateAsset(rt, RtPath);
            log.Add("render texture created: " + RtPath);
        }
        else log.Add("render texture reused: " + RtPath);

        log.Add("active scene: '" + scene.name + "'");

        // 3) 根物体「CG转场」+ Overlay 画布（不挂 GraphicRaycaster，永不挡 UI 点击）
        GameObject root = null;
        foreach (GameObject go in scene.GetRootGameObjects())
            if (go.name == RootName) { root = go; break; }
        bool createdRoot = root == null;
        if (createdRoot)
        {
            root = new GameObject(RootName, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(root, "Create " + RootName);
        }
        Canvas canvas = Ensure<Canvas>(root);
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32760; // 压过所有 UI（含 SceneSelectionUI 的 30）
        if (root.GetComponent<CanvasScaler>() == null) root.AddComponent<CanvasScaler>();
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        var strayRaycaster = root.GetComponent<GraphicRaycaster>();
        if (strayRaycaster != null) UnityEngine.Object.DestroyImmediate(strayRaycaster);

        // 4) Video 全屏 RawImage
        RawImage videoImage;
        Transform videoT = root.transform.Find("Video");
        GameObject videoGo = videoT != null ? videoT.gameObject : new GameObject("Video");
        if (videoT == null) videoGo.transform.SetParent(root.transform, false);
        videoImage = Ensure<RawImage>(videoGo);
        videoImage.texture = rt;
        videoImage.raycastTarget = false;
        StretchFull(videoGo.GetComponent<RectTransform>());
        var aspect = Ensure<AspectRatioFitter>(videoGo);
        aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        aspect.aspectRatio = (float)clip.width / clip.height;

        // 5) Blackout 全屏黑场图
        Image blackout;
        Transform blackT = root.transform.Find("Blackout");
        GameObject blackGo = blackT != null ? blackT.gameObject : new GameObject("Blackout");
        if (blackT == null) blackGo.transform.SetParent(root.transform, false);
        blackout = Ensure<Image>(blackGo);
        blackout.color = new Color(0f, 0f, 0f, 0f);
        blackout.raycastTarget = false;
        StretchFull(blackGo.GetComponent<RectTransform>());
        // The black image supplies letterboxing behind the video, then covers
        // loading once the video image is disabled. It must not obscure playback.
        videoGo.transform.SetAsLastSibling();

        // 6) VideoPlayer（视频渲染到 RenderTexture，音频直出）
        VideoPlayer vp = Ensure<VideoPlayer>(root);
        vp.playOnAwake = false;
        vp.renderMode = VideoRenderMode.RenderTexture;
        vp.targetTexture = rt;
        vp.clip = clip;
        var audio = Ensure<AudioSource>(root);
        audio.playOnAwake = false;
        audio.spatialBlend = 0f;
        audio.volume = 1f;
        vp.audioOutputMode = VideoAudioOutputMode.AudioSource;
        vp.controlledAudioTrackCount = 1;
        vp.EnableAudioTrack(0, true);
        vp.SetTargetAudioSource(0, audio);
        vp.waitForFirstFrame = true;

        // 7) CgTransitionPlayer 组件与引用
        var player = root.GetComponent<HMProtection.UI.CgTransitionPlayer>();
        if (player == null) player = root.AddComponent<HMProtection.UI.CgTransitionPlayer>();
        var so = new SerializedObject(player);
        so.FindProperty("videoPlayer").objectReferenceValue = vp;
        so.FindProperty("videoImage").objectReferenceValue = videoImage;
        so.FindProperty("blackout").objectReferenceValue = blackout;
        so.FindProperty("canvasRoot").objectReferenceValue = root;
        so.ApplyModifiedPropertiesWithoutUndo();

        // 平时保持未激活（转场开始时由脚本自己激活）
        root.SetActive(false);
        log.Add((createdRoot ? "created" : "reused") + " root '" + RootName + "' with canvas/video/blackout/player");

        // 8) 接到 SceneSelectUI 的 cgTransition 字段
        HMProtection.UI.SceneSelectUI selector = null;
        foreach (GameObject go in scene.GetRootGameObjects())
        {
            selector = go.GetComponentInChildren<HMProtection.UI.SceneSelectUI>(true);
            if (selector != null) break;
        }
        if (selector == null)
        {
            log.Add("WARNING: SceneSelectUI not found in scene; wire cgTransition manually");
        }
        else
        {
            var selSo = new SerializedObject(selector);
            selSo.FindProperty("cgTransition").objectReferenceValue = player;
            selSo.ApplyModifiedPropertiesWithoutUndo();
            log.Add("SceneSelectUI.cgTransition wired on '" + selector.gameObject.name + "'");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        log.Add("scene saved: " + saved);
        return true;
    }

    private static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static T Ensure<T>(GameObject go) where T : Component
    {
        T component = go.GetComponent<T>();
        return component != null ? component : go.AddComponent<T>();
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
