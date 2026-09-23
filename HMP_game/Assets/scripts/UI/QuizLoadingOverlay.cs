using System.Collections;
using HMProtection.Quiz;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HMProtection.UI
{
    /// <summary>One continuous cover from CG/review handoff until the next overview is ready.</summary>
    public sealed class QuizLoadingOverlay : MonoBehaviour
    {
        static QuizLoadingOverlay instance;
        public static bool IsVisible => instance != null && instance.gameObject.activeSelf;
        public static float Progress => instance != null ? instance.displayed : 0f;
        CanvasGroup group;
        Image fill;
        TMP_Text heading, detail, percentage;
        float target, displayed, openedAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() => instance = null;

        public static void Show(string title = "PREPARING TRAINING")
        {
            if (instance != null) { instance.heading.text = title; return; }
            var root = new GameObject("Quiz Loading", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster), typeof(CanvasGroup));
            instance = root.AddComponent<QuizLoadingOverlay>();
            DontDestroyOnLoad(root);
            instance.Build(title);
            instance.openedAt = Time.realtimeSinceStartup;
        }

        public static void SetProgress(float value, string status = null)
        {
            if (instance == null) return;
            instance.target = Mathf.Max(instance.target, Mathf.Clamp01(value));
            if (status != null) instance.detail.text = status;
        }

        // Only the prepared overview calls this. Answer input and its timer remain off until it ends.
        public static IEnumerator Reveal()
        {
            var view = instance;
            if (view == null) yield break;
            SetProgress(1f, "Ready");
            while (view != null && view.displayed < 1f) yield return null;
            if (view == null) yield break;
            yield return new WaitForSecondsRealtime(.1f);
            float start = Time.unscaledTime;
            while (view != null && Time.unscaledTime - start < .2f)
            {
                view.group.alpha = 1f - Mathf.Clamp01((Time.unscaledTime - start) / .2f);
                yield return null;
            }
            if (instance == view) Hide();
        }

        public static void Hide()
        {
            if (instance == null) return;
            var old = instance; instance = null;
            old.gameObject.SetActive(false);
            Destroy(old.gameObject);
        }

        void Update()
        {
            displayed = Mathf.MoveTowards(displayed, target, Time.unscaledDeltaTime * 1.6f);
            fill.fillAmount = displayed;
            fill.rectTransform.sizeDelta = new Vector2(720f * displayed, 10f);
            percentage.text = Mathf.FloorToInt(displayed * 100f) + "%";
            // A failed scene/bootstrap must not leave a persistent input-blocking canvas forever.
            if (Time.realtimeSinceStartup - openedAt > 75f)
            { Debug.LogWarning("[QuizLoading] Preparation timed out; releasing transition cover."); Hide(); }
        }
        void Build(string title)
        {
            var canvas = GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31090; // Above gameplay, below the instructor's exit animation.
            var scaler = GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            group = GetComponent<CanvasGroup>();
            var background = Rect("Backdrop", transform, Vector2.zero, Vector2.zero);
            background.anchorMin = Vector2.zero; background.anchorMax = Vector2.one;
            background.offsetMin = background.offsetMax = Vector2.zero;
            background.gameObject.AddComponent<InstructorBackdropGraphic>();
            var panel = Rect("Loading Panel", transform, Vector2.zero, new Vector2(780, 250));
            Text("Brand", panel, new Vector2(0, 130), new Vector2(780, 30), 19, "HM PROTECTION  /  FIRE SAFETY").color = new Color(.6f, .65f, .71f);
            heading = Text("Title", panel, new Vector2(0, 64), new Vector2(780, 58), 38, title);
            heading.fontStyle = FontStyles.Bold;
            detail = Text("Status", panel, new Vector2(0, 8), new Vector2(780, 36), 23, "Preparing your next decision...");
            detail.color = new Color(.73f, .77f, .82f);
            var track = Rect("Progress Track", panel, new Vector2(0, -56), new Vector2(720, 10));
            track.gameObject.AddComponent<Image>().color = new Color(.19f, .21f, .25f);
            fill = Rect("Progress", track, Vector2.zero, new Vector2(720, 10)).gameObject.AddComponent<Image>();
            fill.color = new Color(.95f, .13f, .16f); fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal; fill.fillOrigin = 0; fill.fillAmount = 0f;
            fill.rectTransform.anchorMin = fill.rectTransform.anchorMax = fill.rectTransform.pivot = new Vector2(0f, .5f);
            fill.rectTransform.sizeDelta = new Vector2(0f, 10f);
            percentage = Text("Percentage", panel, new Vector2(0, -102), new Vector2(780, 32), 21, "0%");
        }
        static RectTransform Rect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            r.SetParent(parent, false); r.anchorMin = r.anchorMax = r.pivot = new Vector2(.5f, .5f);
            r.anchoredPosition = position; r.sizeDelta = size; return r;
        }
        static TMP_Text Text(string name, Transform parent, Vector2 position, Vector2 size, float fontSize, string value)
        {
            var text = Rect(name, parent, position, size).gameObject.AddComponent<TextMeshProUGUI>();
            text.font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            text.text = value; text.fontSize = fontSize; text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white; text.raycastTarget = false; return text;
        }
    }
}
