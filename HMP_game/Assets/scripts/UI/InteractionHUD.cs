// 交互提示 HUD（一期临时版）：屏幕中央准星圆点 + 底部交互提示文字。
// 由 Interactor 在 Awake 时通过 EnsureExists() 创建，纯代码搭建，
// 不依赖场景预置、不依赖 TextMeshPro 资源导入（用内置 LegacyRuntime.ttf），
// 退出 Play 模式后随场景一起销毁，不会残留。正式视觉稿由 UI 阶段替换。
using UnityEngine;
using UnityEngine.UI;

public class InteractionHUD : MonoBehaviour
{
    private static readonly Color IdleColor = new Color(1f, 1f, 1f, 0.65f);   // 平时：半透明白点
    private static readonly Color FocusColor = new Color(1f, 0.72f, 0.25f, 0.95f); // 对准可交互物：琥珀色

    private Image crosshair;
    private Text promptLabel;

    // 场景里没有 HUD 时创建一个（已存在则复用）
    public static InteractionHUD EnsureExists()
    {
        InteractionHUD existing = Object.FindFirstObjectByType<InteractionHUD>();
        if (existing != null) return existing;

        var hudGo = new GameObject("InteractionHUD");
        InteractionHUD hud = hudGo.AddComponent<InteractionHUD>();
        hud.Build();
        return hud;
    }

    // 准星是否高亮（对准可交互物）
    public void SetFocus(bool focused)
    {
        if (crosshair != null)
        {
            crosshair.color = focused ? FocusColor : IdleColor;
            crosshair.rectTransform.localScale = focused ? Vector3.one * 1.6f : Vector3.one;
        }
    }

    // 底部提示文案；null/空 = 隐藏
    public void SetPrompt(string text)
    {
        if (promptLabel == null) return;
        bool show = !string.IsNullOrEmpty(text);
        promptLabel.gameObject.SetActive(show);
        if (show) promptLabel.text = text;
    }

    private void Build()
    {
        // Canvas：屏幕覆盖层，1920×1080 基准缩放（与后续正式 UI 一致）
        var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // 准星：程序生成的圆点贴图，居中
        var crossGo = new GameObject("Crosshair", typeof(Image));
        crossGo.transform.SetParent(canvasGo.transform, false);
        crosshair = crossGo.GetComponent<Image>();
        crosshair.sprite = BuildCircleSprite(16, 16, 6.2f);
        crosshair.raycastTarget = false; // 永远不挡射线/点击
        RectTransform rect = crosshair.rectTransform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(10f, 10f);

        // 提示文字：底部居中，带黑色投影保证亮场景下可读
        var labelGo = new GameObject("Prompt", typeof(Text), typeof(Shadow));
        labelGo.transform.SetParent(canvasGo.transform, false);
        promptLabel = labelGo.GetComponent<Text>();
        promptLabel.font = LoadBuiltInFont();
        promptLabel.fontSize = 34;
        promptLabel.alignment = TextAnchor.MiddleCenter;
        promptLabel.color = Color.white;
        promptLabel.raycastTarget = false;
        RectTransform labelRect = promptLabel.rectTransform;
        labelRect.anchorMin = new Vector2(0.5f, 0f);
        labelRect.anchorMax = new Vector2(0.5f, 0f);
        labelRect.pivot = new Vector2(0.5f, 0.5f);
        labelRect.anchoredPosition = new Vector2(0f, 260f);
        labelRect.sizeDelta = new Vector2(1400f, 60f);
        var shadow = labelGo.GetComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(1.5f, -1.5f);
        labelGo.SetActive(false);
    }

    // Unity 6 的内置字体叫 LegacyRuntime.ttf（旧名 Arial.ttf 已移除，做兼容尝试）
    private static Font LoadBuiltInFont()
    {
        Font font = null;
        try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (font == null)
        {
            try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
        }
        if (font == null)
            Debug.LogError("[InteractionHUD] 找不到内置字体，交互提示文字将无法显示。");
        return font;
    }

    // 生成一张实心圆贴图用作准星
    private static Sprite BuildCircleSprite(int width, int height, float radius)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        var center = new Vector2(width * 0.5f - 0.5f, height * 0.5f - 0.5f);
        var pixels = new Color[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), center);
                // 边缘 1px 做抗锯齿过渡
                float alpha = Mathf.Clamp01(radius - dist + 0.5f);
                pixels[y * width + x] = new Color(1f, 1f, 1f, alpha);
            }
        tex.SetPixels(pixels);
        tex.Apply(false, false);
        return Sprite.Create(tex, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 16f);
    }
}
