using System;
using System.Collections;
using System.Collections.Generic;
using HMProtection.Core;
using HMProtection.Quiz;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace HMProtection.UI
{
    /// <summary>Persistent session report, sharing the instructor's visual language and score records.</summary>
    [DisallowMultipleComponent]
    public sealed class TrainingSettlementView : MonoBehaviour
    {
        public bool IsShowing { get; private set; }
        public TMP_Text CorrectText { get; private set; }
        public TMP_Text TimeText { get; private set; }
        public Button RetryButton { get; private set; }
        public Button MenuButton { get; private set; }
        GameObject overlay, fallbackSystem;
        CanvasGroup group;
        RectTransform presentation;
        TMP_Text summary, completion, coach;
        readonly List<TMP_Text> answerStatus = new List<TMP_Text>();
        readonly List<Image> answerStripes = new List<Image>();
        readonly Dictionary<Behaviour, bool> controls = new Dictionary<Behaviour, bool>();
        readonly Dictionary<GameObject, bool> hud = new Dictionary<GameObject, bool>();
        Action retry, menu;
        Coroutine entrance;
        CursorLockMode previousLock;
        bool previousCursor;
        GameObject previousSelection;
        static readonly Color Muted = new Color(.63f, .68f, .74f);
        static readonly Color Green = new Color(.48f, .95f, .72f);
        static readonly Color Red = new Color(1f, .43f, .39f);

        public static string FormatTime(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds)) seconds = 0f;
            int total = Mathf.CeilToInt(Mathf.Max(0f, seconds));
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        public void Show(ScoreBoard score, Action onRetry, Action onMenu)
        {
            if (score == null || IsShowing) return;
            if (overlay == null) Build();
            retry = onRetry; menu = onMenu; IsShowing = true;
            previousLock = Cursor.lockState; previousCursor = Cursor.visible;
            previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            foreach (var root in gameObject.scene.GetRootGameObjects())
            {
                foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
                    if (b is body || b is Interactor || b is GuidanceSystem || b is FireEffectController)
                    { controls[b] = b.enabled; b.enabled = false; }
                foreach (var h in root.GetComponentsInChildren<InteractionHUD>(true))
                { hud[h.gameObject] = h.gameObject.activeSelf; h.gameObject.SetActive(false); }
            }
            if (EventSystem.current == null)
            {
                if (fallbackSystem == null)
                {
                    fallbackSystem = new GameObject("Settlement EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                    fallbackSystem.transform.SetParent(transform, false);
                }
                fallbackSystem.SetActive(true);
            }
            EventSystem.current?.SetSelectedGameObject(null);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            CorrectText.text = score.CorrectCount + " / " + score.TotalQuestions;
            TimeText.text = FormatTime(score.TotalElapsedSeconds);
            completion.text = score.AskedCount.ToString("00") + " / " + score.TotalQuestions.ToString("00") + "  COMPLETED";
            summary.text = score.CorrectCount == score.TotalQuestions ? "EXCELLENT RESPONSE" : "KEEP BUILDING YOUR SKILLS";
            summary.color = score.CorrectCount == score.TotalQuestions ? Green : new Color(1f, .74f, .43f);
            coach.text = score.CorrectCount == score.TotalQuestions
                ? "Great work. You made the right call in every scenario.\nKeep these decisions in mind when it matters."
                : "Every decision is a chance to learn.\nReview the lessons and try again to build safer habits.";
            for (int i = 0; i < answerStatus.Count; i++)
            {
                var answer = i < score.Answers.Count ? score.Answers[i] : null;
                Color tint = answer == null ? Muted : answer.correct ? Green : Red;
                answerStatus[i].text = answer == null ? "NOT ANSWERED" : (answer.timedOut ? "TIME OUT" : answer.correct ? "CORRECT" : "INCORRECT")
                    + "  /  " + FormatTime(answer.elapsedSeconds);
                answerStatus[i].color = tint; answerStripes[i].color = tint;
            }
            group.interactable = false; overlay.SetActive(true);
            entrance = StartCoroutine(Enter());
        }

        IEnumerator Enter()
        {
            float start = Time.unscaledTime;
            group.alpha = 0f;
            while (Time.unscaledTime - start < .35f)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - start) / .35f);
                float ease = 1f - Mathf.Pow(1f - t, 3f);
                group.alpha = ease; presentation.anchoredPosition = new Vector2(0f, -20f * (1f - ease));
                yield return null;
            }
            group.alpha = 1f; presentation.anchoredPosition = Vector2.zero;
            // Releasing the instructor's final key must not submit the default retry button.
            while (QuizLoadingOverlay.IsVisible || (Keyboard.current?.anyKey.isPressed ?? false)
                || (Mouse.current?.leftButton.isPressed ?? false) || (Gamepad.current?.buttonSouth.isPressed ?? false)) yield return null;
            group.interactable = true; EventSystem.current?.SetSelectedGameObject(RetryButton.gameObject);
            entrance = null;
        }
        void Invoke(Action callback)
        {
            if (!IsShowing || !group.interactable || callback == null) return;
            group.interactable = false;
            callback();
        }
        public void Dismiss()
        {
            if (!IsShowing) return;
            if (entrance != null) StopCoroutine(entrance);
            entrance = null; overlay.SetActive(false);
            foreach (var item in controls) if (item.Key != null) item.Key.enabled = item.Value;
            foreach (var item in hud) if (item.Key != null) item.Key.SetActive(item.Value);
            controls.Clear(); hud.Clear();
            if (fallbackSystem != null) fallbackSystem.SetActive(false);
            Cursor.lockState = previousLock; Cursor.visible = previousCursor;
            EventSystem.current?.SetSelectedGameObject(previousSelection != null && previousSelection.activeInHierarchy ? previousSelection : null);
            IsShowing = false; retry = menu = null;
        }
        void OnDisable() => Dismiss();

        void Build()
        {
            overlay = new GameObject("Training Settlement", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            overlay.transform.SetParent(transform, false);
            var canvas = overlay.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 31080;
            var scaler = overlay.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            group = overlay.GetComponent<CanvasGroup>();
            var background = Rect("Backdrop", overlay.transform, 0, 0, 0, 0);
            background.anchorMin = Vector2.zero; background.anchorMax = Vector2.one;
            background.offsetMin = background.offsetMax = Vector2.zero;
            background.gameObject.AddComponent<InstructorBackdropGraphic>();
            var stage = Rect("Layout", overlay.transform, 0, 0, 1920, 1080);
            stage.anchorMin = stage.anchorMax = stage.pivot = new Vector2(.5f, .5f); stage.anchoredPosition = Vector2.zero;
            var fit = stage.gameObject.AddComponent<AspectRatioFitter>(); fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent; fit.aspectRatio = 16f / 9f;
            var reference = Rect("Reference", stage, 0, 0, 1920, 1080); reference.gameObject.AddComponent<InstructorLessonLayout>();
            presentation = Rect("Presentation", reference, 0, 0, 1920, 1080);
            Text("Brand", presentation, 120, 991, 1200, 32, 20, "HM PROTECTION  /  OFFICE FIRE SAFETY").color = Muted;
            Text("Title", presentation, 116, 900, 1360, 82, 64, "TRAINING COMPLETE").fontStyle = FontStyles.Bold;
            completion = Text("Completion", presentation, 1350, 927, 450, 36, 21, ""); completion.alignment = TextAlignmentOptions.MidlineRight; completion.color = Muted;
            Bar("Header Accent", presentation, 120, 874, 1680, 3, new Color(.65f, .08f, .1f));

            Panel("Instructor Card", presentation, 120, 279, 530, 562, true);
            Artwork("HAIMO", presentation, "InstructorLesson/Instructor", 143, 281, 320, 573);
            Artwork("Fire Safety Badge", presentation, "Settlement/FireSafetyBadge", 446, 644, 185, 185);
            Text("Instructor Name", presentation, 454, 558, 165, 48, 29, "HAIMO").fontStyle = FontStyles.Bold;
            Text("Instructor Role", presentation, 455, 503, 163, 59, 17, "YOUR SAFETY\nINSTRUCTOR").color = Muted;

            var scoreCard = Panel("Performance", presentation, 696, 497, 1104, 344, false);
            summary = Text("Summary", scoreCard, 43, 272, 1018, 35, 22, ""); summary.fontStyle = FontStyles.Bold;
            Text("Correct Label", scoreCard, 44, 209, 470, 35, 20, "CORRECT ANSWERS").color = Muted;
            Text("Time Label", scoreCard, 606, 209, 458, 35, 20, "TOTAL ANSWERING TIME").color = Muted;
            CorrectText = Text("Correct Count", scoreCard, 35, 77, 510, 136, 105, "0 / 3"); CorrectText.fontStyle = FontStyles.Bold;
            TimeText = Text("Time", scoreCard, 594, 77, 470, 136, 105, "00:00"); TimeText.fontStyle = FontStyles.Bold;
            Bar("Divider", scoreCard, 550, 58, 2, 190, new Color(.27f, .29f, .33f));
            Text("Time Note", scoreCard, 44, 24, 1016, 36, 19, "Decisions + actions only. Videos, lessons and loading are excluded.").color = Muted;

            Text("Review Label", presentation, 700, 432, 1080, 35, 20, "YOUR THREE DECISIONS").color = Muted;
            string[] names = { "FIRST RESPONSE", "NO EXTINGUISHER", "DAMAGED EQUIPMENT" };
            for (int i = 0; i < 3; i++)
            {
                var card = Panel("Question " + (i + 1), presentation, 696 + i * 375, 279, 354, 135, true);
                answerStripes.Add(Bar("Result Accent", card, 22, 29, 3, 76, Muted));
                Text("Question Title", card, 42, 76, 290, 32, 18, "0" + (i + 1) + "  " + names[i]).fontStyle = FontStyles.Bold;
                answerStatus.Add(Text("Answer Status", card, 42, 28, 290, 38, 20, ""));
            }

            coach = Text("Coach Note", presentation, 126, 150, 1668, 86, 29, "");
            coach.color = new Color(.83f, .86f, .90f);
            Text("Footer", presentation, 125, 63, 850, 38, 18, "PRACTICE TODAY. RESPOND WITH CONFIDENCE.").color = Muted;
            RetryButton = Button("Train Again", presentation, 1150, 50, 326, 76, "TRAIN AGAIN", true, () => Invoke(retry));
            MenuButton = Button("Main Menu", presentation, 1500, 50, 300, 76, "MAIN MENU", false, () => Invoke(menu));
            overlay.SetActive(false);
        }
        static RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); r.SetParent(parent, false);
            r.anchorMin = r.anchorMax = r.pivot = Vector2.zero; r.anchoredPosition = new Vector2(x, y); r.sizeDelta = new Vector2(width, height); return r;
        }
        static RectTransform Panel(string name, Transform parent, float x, float y, float w, float h, bool quiet)
        {
            var rect = Rect(name, parent, x, y, w, h); var panel = rect.gameObject.AddComponent<SettlementPanelGraphic>(); panel.Accented = !quiet; return rect;
        }
        static Image Bar(string name, Transform parent, float x, float y, float w, float h, Color tint)
        { var image = Rect(name, parent, x, y, w, h).gameObject.AddComponent<Image>(); image.color = tint; image.raycastTarget = false; return image; }
        static TMP_Text Text(string name, Transform parent, float x, float y, float w, float h, float size, string value)
        {
            var text = Rect(name, parent, x, y, w, h).gameObject.AddComponent<TextMeshProUGUI>();
            text.font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF"); text.fontSize = size;
            text.text = value; text.color = Color.white; text.raycastTarget = false; text.alignment = TextAlignmentOptions.MidlineLeft; return text;
        }
        static void Artwork(string name, Transform parent, string resource, float x, float y, float w, float h)
        {
            var image = Rect(name, parent, x, y, w, h).gameObject.AddComponent<RawImage>();
            image.texture = Resources.Load<Texture2D>(resource); image.raycastTarget = false;
        }
        static Button Button(string name, Transform parent, float x, float y, float w, float h, string caption, bool primary, UnityEngine.Events.UnityAction action)
        {
            var rect = Rect(name, parent, x, y, w, h); var graphic = rect.gameObject.AddComponent<MenuButtonGraphic>();
            graphic.Style = primary ? MenuButtonGraphic.ButtonStyle.Primary : MenuButtonGraphic.ButtonStyle.Secondary;
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = graphic; button.onClick.AddListener(action);
            var colors = button.colors; colors.highlightedColor = new Color(1f, .86f, .81f); colors.pressedColor = new Color(.72f, .72f, .72f); colors.fadeDuration = .12f; button.colors = colors;
            var label = Text("Label", rect, 12, 8, w - 24, h - 16, 22, caption); label.alignment = TextAlignmentOptions.Center; label.fontStyle = FontStyles.Bold;
            return button;
        }
    }
}
