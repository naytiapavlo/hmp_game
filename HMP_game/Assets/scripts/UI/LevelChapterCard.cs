using System.Collections;
using HMProtection.Quiz;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HMProtection.UI
{
    /// <summary>Chapter announcement inside the existing CG canvas, before any video/audio starts.</summary>
    public sealed class LevelChapterCard : MonoBehaviour
    {
        public bool IsShowing { get; private set; }
        GameObject layer;
        CanvasGroup group;
        RectTransform content;

        public IEnumerator Play(float holdSeconds)
        {
            if (layer == null) Build();
            IsShowing = true; layer.SetActive(true); layer.transform.SetAsLastSibling();
            yield return Fade(true);
            yield return new WaitForSecondsRealtime(Mathf.Max(0f, holdSeconds));
            yield return Fade(false);
            Hide();
        }

        IEnumerator Fade(bool entering)
        {
            const float duration = .35f;
            float started = Time.unscaledTime;
            while (Time.unscaledTime - started < duration)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - started) / duration);
                float ease = 1f - Mathf.Pow(1f - t, 3f);
                group.alpha = entering ? ease : 1f - t;
                content.anchoredPosition = new Vector2(0f, entering ? -16f * (1f - ease) : 10f * t);
                yield return null;
            }
            group.alpha = entering ? 1f : 0f;
            content.anchoredPosition = Vector2.zero;
        }

        public void Hide()
        {
            IsShowing = false;
            if (layer != null) layer.SetActive(false);
        }
        void OnDisable() => Hide();

        void Build()
        {
            var root = Rect("Level Chapter", transform, Vector2.zero, Vector2.zero);
            root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one; root.offsetMin = root.offsetMax = Vector2.zero;
            layer = root.gameObject; group = layer.AddComponent<CanvasGroup>();
            var background = Rect("Backdrop", root, Vector2.zero, Vector2.zero);
            background.anchorMin = Vector2.zero; background.anchorMax = Vector2.one;
            background.offsetMin = background.offsetMax = Vector2.zero;
            background.gameObject.AddComponent<InstructorBackdropGraphic>();

            var safeArea = Rect("Safe Area", root, Vector2.zero, new Vector2(1920, 1080));
            var fit = safeArea.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent; fit.aspectRatio = 16f / 9f;
            content = Rect("Title Content", safeArea, Vector2.zero, new Vector2(1920, 1080));
            content.gameObject.AddComponent<InstructorLessonLayout>();

            Text("Brand", content, 205, 20, "HM PROTECTION  /  FIRE SAFETY TRAINING").color = new Color(.6f, .65f, .71f);
            var logo = Rect("Brand Logo", content, new Vector2(0, 90), new Vector2(165, 165)).gameObject.AddComponent<RawImage>();
            logo.texture = Resources.Load<Texture2D>("Branding/ChapterLogo"); logo.raycastTarget = false;
            Text("Level", content, -35, 30, "LEVEL 1").color = new Color(1f, .29f, .27f);
            var title = Text("Chapter Title", content, -123, 72, "EARLY-STAGE FIRE"); title.fontStyle = FontStyles.Bold;
            var line = Rect("Accent", content, new Vector2(0, -215), new Vector2(118, 4)).gameObject.AddComponent<Image>();
            line.color = new Color(.96f, .14f, .17f); line.raycastTarget = false;
            layer.SetActive(false);
        }
        static RectTransform Rect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = position; rect.sizeDelta = size; return rect;
        }
        static TMP_Text Text(string name, Transform parent, float y, float size, string value)
        {
            var text = Rect(name, parent, new Vector2(0, y), new Vector2(1540, 105)).gameObject.AddComponent<TextMeshProUGUI>();
            text.font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            text.text = value; text.fontSize = size; text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white; text.raycastTarget = false; return text;
        }
    }
}
