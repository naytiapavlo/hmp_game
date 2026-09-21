using System;
using System.IO;
using System.Linq;
using HMProtection.Quiz;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 选择题限时进度条一次性装配：
//   1. 从仓库 ui/通用 取进度条与火焰图标，裁剪/缩放后落到 Assets/UI/QuizTimer/；
//   2. 在「Scene Choices」根上搭 QuizTimerHUD 独立 Canvas（进度条 + 末端火焰 + 1/3 里程碑火焰 + 倒计时）；
//   3. 接线 QuizTimerHUD 与 OfficeChoiceInteraction.timer。
// 报告写入 Library/QuizTimerSetup.txt；菜单 Tools/Scene Choices/Install Quiz Timer 可强制重跑（幂等）。
[InitializeOnLoad]
public static class QuizTimerSetup
{
    const string Folder = "Assets/UI/QuizTimer";
    const string Marker = "Library/QuizTimer.v1.done";
    const string ScenePath = "Assets/Scenes/办公室场景.unity";
    const float TrackHeight = 40f;      // 轨道 RectTransform 高（含贴图透明边）
    const float TrackWidth = 640f;
    const float BarBottomOffset = 46f;  // 进度条底边距屏幕底部
    const float LeadingFlameSize = 56f;
    const float MilestoneFlameSize = 48f;
    static readonly Color FillRed = new Color(.94f, .12f, .10f);

    static QuizTimerSetup() { EditorApplication.delayCall += Auto; }
    static void Auto()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += Auto; return; }
        if (!File.Exists(Marker)) Install();
    }
    static string UiSourceDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "ui", "通用"));

    [MenuItem("Tools/Scene Choices/Install Quiz Timer")]
    public static void Install()
    {
        var scene = SceneManager.GetActiveScene();
        if (EditorApplication.isPlayingOrWillChangePlaymode || scene.path != ScenePath) return;
        try
        {
            if (scene.isDirty) EditorSceneManager.SaveScene(scene);
            var flow = SceneChoiceSetup.Find<OfficeFireChoiceFlow>(scene);
            if (flow == null) throw new Exception("Office question flow missing");
            Directory.CreateDirectory(Folder);
            if (!File.Exists(Path.Combine(UiSourceDir, "进度条.png"))) throw new Exception("进度条.png not found under " + UiSourceDir);
            if (!File.Exists(Path.Combine(UiSourceDir, "火焰图标.png"))) throw new Exception("火焰图标.png not found under " + UiSourceDir);
            AssetDatabase.Refresh();

            var track = BuildTrackSprite();
            var fire = ImportFireSprite();
            var fillCapsule = BuildFillCapsuleSprite();
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            if (font == null) throw new Exception("TMP font missing");

            var hud = flow.GetComponent<QuizTimerHUD>(); if (hud == null) hud = flow.gameObject.AddComponent<QuizTimerHUD>();
            hud.flow = flow; hud.durationSeconds = 45f; hud.urgentSeconds = 10f;

            var canvasGo = flow.transform.Find("Quiz Timer Canvas")?.gameObject;
            if (canvasGo == null)
            {
                canvasGo = new GameObject("Quiz Timer Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
                canvasGo.transform.SetParent(flow.transform, false);
            }
            foreach (var child in canvasGo.transform.Cast<Transform>().ToArray()) UnityEngine.Object.DestroyImmediate(child.gameObject);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 800; // 气泡画布 700 之上、结算反馈 31000 之下
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;

            var bar = Rect("Timer Bar", canvasGo.transform, new Vector2(.5f, 0f), new Vector2(.5f, 0f), new Vector2(0f, BarBottomOffset), new Vector2(TrackWidth, TrackHeight));
            var trackImage = bar.gameObject.AddComponent<Image>();
            trackImage.sprite = track; trackImage.type = Image.Type.Sliced; trackImage.raycastTarget = false;
            var border = ((TextureImporter)AssetImporter.GetAtPath(Folder + "/T_TrackBar.png")).spriteBorder; // 裁剪透明边+胶囊半径，见 BuildTrackSprite
            var fillArea = Rect("Fill Area", bar, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            fillArea.anchorMin = Vector2.zero; fillArea.anchorMax = Vector2.one;
            fillArea.offsetMin = new Vector2(border.x + 2f, 5f);
            fillArea.offsetMax = new Vector2(-(border.z + 2f), -5f);

            var fill = Rect("Fill", fillArea, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero).gameObject.AddComponent<Image>();
            fill.rectTransform.anchorMin = new Vector2(0f, 0f); fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            fill.rectTransform.offsetMin = Vector2.zero; fill.rectTransform.offsetMax = Vector2.zero;
            fill.sprite = fillCapsule; fill.type = Image.Type.Sliced; fill.color = FillRed; fill.raycastTarget = false;

            var leading = Rect("Leading Flame", fillArea, new Vector2(0f, .5f), new Vector2(.5f, .5f), Vector2.zero, new Vector2(LeadingFlameSize, LeadingFlameSize)).gameObject.AddComponent<Image>();
            leading.sprite = fire; leading.preserveAspect = true; leading.raycastTarget = false;
            var milestones = new Image[3];
            for (int i = 0; i < milestones.Length; i++)
            {
                var m = Rect("Milestone Flame " + (i + 1), fillArea, new Vector2(0f, .5f), new Vector2(.5f, .5f), Vector2.zero, new Vector2(MilestoneFlameSize, MilestoneFlameSize));
                var image = m.gameObject.AddComponent<Image>();
                image.sprite = fire; image.preserveAspect = true; image.raycastTarget = false; m.gameObject.SetActive(false);
                milestones[i] = image;
                m.SetSiblingIndex(1); // 越靠后的火焰越先绘制，互相遮挡。
            }
            leading.transform.SetAsLastSibling(); // 原始末端火焰保持在最前层。

            var label = Text("Countdown", bar, font, 32, FontStyles.Bold);
            label.rectTransform.anchorMin = new Vector2(1f, 0f); label.rectTransform.anchorMax = new Vector2(1f, 1f);
            label.rectTransform.pivot = new Vector2(0f, .5f);
            label.rectTransform.anchoredPosition = new Vector2(16f, 0f); label.rectTransform.sizeDelta = new Vector2(130f, 0f);
            label.alignment = TextAlignmentOptions.Left; label.color = Color.white;
            label.rectTransform.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, .7f);

            hud.canvas = canvas; hud.fillArea = fillArea; hud.fill = fill;
            hud.leadingFlame = leading.rectTransform; hud.milestoneFlames = milestones; hud.countdownLabel = label;

            if (flow.GetComponent<OfficeChoiceInteraction>() == null) OfficeChoiceActionSetup.Install();
            var interaction = flow.GetComponent<OfficeChoiceInteraction>();
            if (interaction == null) throw new Exception("OfficeChoiceInteraction missing after install");
            interaction.timer = hud;

            canvasGo.SetActive(false);
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
            File.WriteAllText(Marker, DateTime.UtcNow.ToString("o"));
            File.WriteAllText("Library/QuizTimerSetup.txt", "PASS: quiz timer HUD installed (45s, overlapping moving flames, countdown).");
        }
        catch (Exception e) { File.WriteAllText("Library/QuizTimerSetup.txt", e.ToString()); Debug.LogException(e); }
    }

    // 进度条原图 2170x725，绘制区约 2080x113：裁掉透明边后按高缩放到 UI 尺寸，并按胶囊半径写入 9 宫格边框。
    static Sprite BuildTrackSprite()
    {
        string path = Folder + "/T_TrackBar.png";
        if (!File.Exists(path))
        {
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            source.LoadImage(File.ReadAllBytes(Path.Combine(UiSourceDir, "进度条.png")));
            var pixels = source.GetPixels32();
            int w = source.width, h = source.height, minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                if (pixels[y * w + x].a > 25)
                { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
            if (maxX < 0) throw new Exception("进度条.png has no visible pixels");
            const int pad = 2;
            float drawn = (maxY - minY + 1) / (float)(TrackHeight - pad * 2);
            int cropX = Mathf.Max(0, minX - pad), cropY = Mathf.Max(0, minY - pad);
            int cropW = Mathf.Min(w - cropX, maxX - minX + 1 + pad * 2), cropH = Mathf.Min(h - cropY, maxY - minY + 1 + pad * 2);
            int outW = Mathf.RoundToInt(cropW / drawn), outH = Mathf.RoundToInt(cropH / drawn);
            File.WriteAllBytes(path, ResizeArea(pixels, w, h, cropX, cropY, cropW, cropH, outW, outH).EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(source);
        }
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true; importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed; importer.isReadable = false;
        float cap = (TrackHeight - 4f) * .5f; // 胶囊半径（缩放后 18px）+ 2px 透明边
        importer.spriteBorder = new Vector4(cap + 2f, 2f, cap + 2f, 2f);
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }
    static Sprite ImportFireSprite()
    {
        string path = Folder + "/T_Fire.png";
        if (!File.Exists(path)) File.Copy(Path.Combine(UiSourceDir, "火焰图标.png"), path, true);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true; importer.mipmapEnabled = false; importer.maxTextureSize = 2048;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }
    // 红色进度用白色胶囊贴图（运行时由 Image.color 染红），避免把红框轨道图整体染色后内部发暗。
    static Sprite BuildFillCapsuleSprite()
    {
        string path = Folder + "/T_TimerFill.png";
        if (!File.Exists(path))
        {
            const int size = 64, h = 32, r = 15;
            var tex = new Texture2D(size, h, TextureFormat.RGBA32, false);
            var pixels = new Color[size * h];
            var half = new Vector2(size * .5f, h * .5f);
            var inner = new Vector2(half.x - r, half.y - r);
            for (int y = 0; y < h; y++) for (int x = 0; x < size; x++)
            {
                var p = new Vector2(Mathf.Abs(x + .5f - half.x), Mathf.Abs(y + .5f - half.y));
                var q = new Vector2(Mathf.Max(p.x - inner.x, 0f), Mathf.Max(p.y - inner.y, 0f));
                float d = new Vector2(q.x, q.y).magnitude - r;
                pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(.5f - d));
            }
            tex.SetPixels(pixels); tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true; importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed; importer.isReadable = false;
        importer.spriteBorder = new Vector4(16f, 16f, 16f, 16f);
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }
    // 盒式滤波缩放（预乘 alpha 累加再反预乘），避免直接双线性在细红边上的锯齿与黑晕。
    static Texture2D ResizeArea(Color32[] src, int w, int h, int cx, int cy, int cw, int ch, int tw, int th)
    {
        var dst = new Texture2D(tw, th, TextureFormat.RGBA32, false);
        var output = new Color[tw * th];
        for (int y = 0; y < th; y++)
        {
            float y0 = cy + y * ch / (float)th, y1 = cy + (y + 1) * ch / (float)th;
            int yi0 = Mathf.Clamp(Mathf.FloorToInt(y0), cy, cy + ch - 1), yi1 = Mathf.Clamp(Mathf.Max(yi0 + 1, Mathf.CeilToInt(y1)), cy + 1, cy + ch);
            for (int x = 0; x < tw; x++)
            {
                float x0 = cx + x * cw / (float)tw, x1 = cx + (x + 1) * cw / (float)tw;
                int xi0 = Mathf.Clamp(Mathf.FloorToInt(x0), cx, cx + cw - 1), xi1 = Mathf.Clamp(Mathf.Max(xi0 + 1, Mathf.CeilToInt(x1)), cx + 1, cx + cw);
                float accR = 0f, accG = 0f, accB = 0f, accA = 0f, area = 0f;
                for (int yy = yi0; yy < yi1; yy++) for (int xx = xi0; xx < xi1; xx++)
                {
                    float wx = Mathf.Min(x1, xx + 1) - Mathf.Max(x0, xx);
                    float wy = Mathf.Min(y1, yy + 1) - Mathf.Max(y0, yy);
                    float weight = wx * wy; if (weight <= 0f) continue;
                    var c = src[yy * w + xx];
                    accR += c.r * c.a * weight; accG += c.g * c.a * weight; accB += c.b * c.a * weight;
                    accA += c.a * weight; area += weight;
                }
                if (area <= 0f) { output[y * tw + x] = Color.clear; continue; }
                float a = accA / area / 255f;
                output[y * tw + x] = a <= 0f ? Color.clear
                    : new Color(accR / accA / 255f, accG / accA / 255f, accB / accA / 255f, a);
            }
        }
        dst.SetPixels(output); dst.Apply();
        return dst;
    }
    static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
    {
        var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        r.SetParent(parent, false);
        r.anchorMin = r.anchorMax = anchor; r.pivot = pivot;
        r.anchoredPosition = position; r.sizeDelta = size;
        return r;
    }
    static TextMeshProUGUI Text(string name, Transform parent, TMP_FontAsset font, float size, FontStyles style)
    {
        var g = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        g.transform.SetParent(parent, false);
        var t = g.GetComponent<TextMeshProUGUI>();
        t.font = font; t.fontSize = size; t.fontStyle = style; t.text = "0:45";
        t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false;
        return t;
    }
}
