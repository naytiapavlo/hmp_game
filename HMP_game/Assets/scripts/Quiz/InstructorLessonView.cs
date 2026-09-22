using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.Video;

namespace HMProtection.Quiz
{
    /// <summary>Reusable, JSON-driven review: line 1, line 2, video, line 3.</summary>
    [DisallowMultipleComponent]
    public sealed class InstructorLessonView : MonoBehaviour
    {
        public enum LessonStep { First, Second, Video, Third }
        public bool IsShowing { get; private set; }
        public LessonStep Step { get; private set; }
        public string LastVideoError { get; private set; }
        public TMP_Text InputHint => inputHint;
        public bool IsClosing { get; private set; }
        public TMP_Text DialogueText => dialogue;
        public VideoPlayer Player => video;
        public event Action Closed;
        public event Action Closing;

        GameObject overlay, fallbackSystem;
        TMP_Text dialogue, status, progress, inputHint, outcome, screenKicker;
        CanvasGroup overlayGroup, dialogueGroup;
        RectTransform motionRoot, dialogueRoot;
        Coroutine entrance, lineTransition, closing;
        bool inputArmed;
        int lastAdvanceFrame = -1;
        const float InputGuardSeconds = .22f;
        RawImage videoImage;
        VideoPlayer video;
        RenderTexture videoTexture;
        InstructorLessonConfig config;
        InstructorDialogue lines;
        Coroutine playback;
        bool videoEnded, videoFailed;
        float inputAfter;
        CursorLockMode oldLock;
        bool oldCursor;
        GameObject oldSelection;
        readonly Dictionary<Behaviour, bool> controls = new Dictionary<Behaviour, bool>();
        readonly Dictionary<GameObject, bool> hud = new Dictionary<GameObject, bool>();

        public bool Show(InstructorLessonConfig lesson, bool correct)
        {
            if (IsShowing || !isActiveAndEnabled || lesson == null) return false;
            var branch = correct ? lesson.correct : lesson.incorrect;
            if (branch == null) return false;
            if (overlay == null) BuildView();
            config = lesson; lines = branch; LastVideoError = null;
            IsShowing = true; IsClosing = false;
            oldLock = Cursor.lockState; oldCursor = Cursor.visible;
            oldSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
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
                    fallbackSystem = new GameObject("Instructor EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                    fallbackSystem.transform.SetParent(transform, false);
                }
                fallbackSystem.SetActive(true);
            }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            overlay.SetActive(true);
            EventSystem.current?.SetSelectedGameObject(null);
            outcome.text = correct ? "CORRECT  /  LET'S REVIEW" : "INCORRECT  /  LET'S LEARN";
            outcome.color = correct ? new Color(.48f, .95f, .72f) : new Color(1f, .48f, .42f);
            status.text = "Let's review\nyour decision.";
            screenKicker.text = "FIRE SAFETY  /  FIELD NOTES";
            screenKicker.gameObject.SetActive(true);
            videoImage.gameObject.SetActive(false);
            ShowLine(LessonStep.First);
            entrance = StartCoroutine(AnimateEntrance());
            return true;
        }

        void ShowLine(LessonStep step)
        {
            Step = step;
            dialogue.text = step == LessonStep.First ? lines.first : step == LessonStep.Second ? lines.second : lines.third;
            progress.text = step == LessonStep.First ? "01 / 03" : step == LessonStep.Second ? "02 / 03" : "03 / 03";
            inputHint.text = step == LessonStep.Second ? "PRESS ANY KEY TO PLAY VIDEO" : step == LessonStep.Third ? "PRESS ANY KEY TO CONTINUE TRAINING" : "PRESS ANY KEY TO CONTINUE";
            if (lineTransition != null) StopCoroutine(lineTransition);
            lineTransition = StartCoroutine(AnimateLine());
            ResetInputGate();
        }

        public void Advance()
        {
            if (!IsShowing || IsClosing || Time.unscaledTime < inputAfter || lastAdvanceFrame == Time.frameCount) return;
            lastAdvanceFrame = Time.frameCount;
            ResetInputGate();
            if (Step == LessonStep.First) ShowLine(LessonStep.Second);
            else if (Step == LessonStep.Second)
            {
                Step = LessonStep.Video;
                progress.text = "VIDEO REVIEW"; inputHint.text = "PRESS ANY KEY TO SKIP VIDEO";
                playback = StartCoroutine(PlayVideo());
            }
            else if (Step == LessonStep.Video)
            {
                StopVideo();
                status.text = "Keep the lesson\nin mind.";
                screenKicker.text = "FIRE SAFETY  /  TAKE IT FORWARD"; screenKicker.gameObject.SetActive(true);
                ShowLine(LessonStep.Third);
            }
            else { IsClosing = true; Closing?.Invoke(); closing = StartCoroutine(AnimateExit()); }
        }

        void ResetInputGate() { inputArmed = false; inputAfter = Time.unscaledTime + InputGuardSeconds; }
        void Update()
        {
            if (!IsShowing || IsClosing) return;
            if (!inputArmed)
            {
                if (Time.unscaledTime >= inputAfter && !AnyInput(false)) inputArmed = true;
                return;
            }
            if (AnyInput(true)) Advance();
        }
        // Mouse movement/scroll is not a continue key. Each step requires release before another press.
        static bool AnyInput(bool pressedThisFrame)
        {
            bool Read(ButtonControl button) => button != null && (pressedThisFrame ? button.wasPressedThisFrame : button.isPressed);
            if (Read(Keyboard.current?.anyKey)) return true;
            var mouse = Mouse.current;
            if (mouse != null && (Read(mouse.leftButton) || Read(mouse.rightButton) || Read(mouse.middleButton)
                || Read(mouse.forwardButton) || Read(mouse.backButton))) return true;
            var gamepad = Gamepad.current;
            if (gamepad != null)
                foreach (var control in gamepad.allControls)
                    if (control is ButtonControl button && Read(button)) return true;
            return false;
        }
        IEnumerator AnimateEntrance()
        {
            float start = Time.unscaledTime;
            overlayGroup.alpha = 0f;
            while (Time.unscaledTime - start < .28f)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - start) / .28f);
                float ease = 1f - Mathf.Pow(1f - t, 3f);
                overlayGroup.alpha = ease; motionRoot.anchoredPosition = new Vector2(0f, -16f * (1f - ease));
                yield return null;
            }
            overlayGroup.alpha = 1f; motionRoot.anchoredPosition = Vector2.zero; entrance = null;
        }
        IEnumerator AnimateLine()
        {
            float start = Time.unscaledTime;
            dialogueGroup.alpha = 0f;
            while (Time.unscaledTime - start < .18f)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - start) / .18f);
                float ease = 1f - Mathf.Pow(1f - t, 3f);
                dialogueGroup.alpha = ease; dialogueRoot.anchoredPosition = new Vector2(0f, -6f * (1f - ease));
                yield return null;
            }
            dialogueGroup.alpha = 1f; dialogueRoot.anchoredPosition = Vector2.zero; lineTransition = null;
        }
        IEnumerator AnimateExit()
        {
            float start = Time.unscaledTime;
            while (Time.unscaledTime - start < .18f)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - start) / .18f);
                overlayGroup.alpha = 1f - t * t; motionRoot.anchoredPosition = new Vector2(0f, -8f * t * t);
                yield return null;
            }
            closing = null; Dismiss();
        }

        public static string ResolveVideoPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri)
                && (uri.Scheme == "https" || uri.Scheme == "http" || uri.Scheme == "file")) return path;
            return Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, path));
        }

        IEnumerator PlayVideo()
        {
            videoEnded = videoFailed = false; LastVideoError = null;
            status.text = "Preparing\nyour lesson...";
            screenKicker.text = "FIRE SAFETY  /  LESSON VIDEO";
            string path = null;
            try { path = ResolveVideoPath(config.videoPath); }
            catch (Exception e) { LastVideoError = e.Message; }
            if (path == null || (Path.IsPathRooted(path) && !File.Exists(path)))
            {
                VideoUnavailable(LastVideoError ?? "Video file has not been provided: " + config.videoPath);
                yield break;
            }
            CreateVideoPlayer();
            video.url = path;
            video.Prepare();
            float deadline = Time.realtimeSinceStartup + 15f;
            while (!video.isPrepared && !videoFailed && Time.realtimeSinceStartup < deadline) yield return null;
            if (!video.isPrepared || videoFailed)
            {
                VideoUnavailable(LastVideoError ?? "Video preparation timed out."); yield break;
            }
            float aspect = video.height > 0 ? (float)video.width / video.height : 16f / 9f;
            videoImage.GetComponent<AspectRatioFitter>().aspectRatio = aspect;
            status.text = ""; screenKicker.gameObject.SetActive(false); videoImage.gameObject.SetActive(true);
            // Commit the prepared output for a frame before starting the native decoder.
            yield return null;
            video.Play();
            double previous = -1;
            float stalledAt = Time.realtimeSinceStartup;
            float retryAt = stalledAt + 1f;
            while (!videoEnded && !videoFailed)
            {
                // Some native backends drop Play during the prepare-completed transition.
                if (video.isPrepared && !video.isPlaying && video.frame < 0 && Time.realtimeSinceStartup >= retryAt)
                { video.Play(); retryAt = Time.realtimeSinceStartup + 1f; }
                if (Math.Abs(video.time - previous) > .01) { previous = video.time; stalledAt = Time.realtimeSinceStartup; }
                if (Time.realtimeSinceStartup - stalledAt > 15f) { LastVideoError = "Video playback stalled (frame=" + video.frame + ", time=" + video.time + ", prepared=" + video.isPrepared + ", playing=" + video.isPlaying + ")."; videoFailed = true; }
                yield return null;
            }
            if (videoFailed) { VideoUnavailable(LastVideoError); yield break; }
            video.Stop(); videoImage.gameObject.SetActive(false);
            status.text = "LESSON COMPLETE";
            screenKicker.text = "FIRE SAFETY  /  TAKE IT FORWARD"; screenKicker.gameObject.SetActive(true);
            playback = null;
            ShowLine(LessonStep.Third);
        }

        void VideoUnavailable(string reason)
        {
            LastVideoError = reason; video.Stop(); videoImage.gameObject.SetActive(false);
            status.text = "Lesson video is unavailable.\n\nYou can continue the review.";
            screenKicker.gameObject.SetActive(true);
            inputHint.text = "PRESS ANY KEY TO CONTINUE";
            ResetInputGate();
            Debug.LogWarning("[InstructorLesson] " + reason, this);
            playback = null;
        }
        void OnVideoEnded(VideoPlayer source) => videoEnded = true;
        void OnVideoError(VideoPlayer source, string message) { videoFailed = true; LastVideoError = message; }
        void StopVideo()
        {
            if (playback != null) StopCoroutine(playback);
            playback = null;
            if (video != null) video.Stop();
            if (videoImage != null) videoImage.gameObject.SetActive(false);
        }
        public void Dismiss()
        {
            if (!IsShowing) return;
            if (entrance != null) StopCoroutine(entrance);
            if (lineTransition != null) StopCoroutine(lineTransition);
            if (closing != null) StopCoroutine(closing);
            entrance = lineTransition = closing = null;
            StopVideo(); overlay.SetActive(false);
            foreach (var item in controls) if (item.Key != null) item.Key.enabled = item.Value;
            foreach (var item in hud) if (item.Key != null) item.Key.SetActive(item.Value);
            controls.Clear(); hud.Clear();
            if (fallbackSystem != null) fallbackSystem.SetActive(false);
            Cursor.lockState = oldLock; Cursor.visible = oldCursor;
            EventSystem.current?.SetSelectedGameObject(oldSelection != null && oldSelection.activeInHierarchy ? oldSelection : null);
            IsShowing = false; IsClosing = false; inputArmed = false; Closed?.Invoke();
        }
        void OnDisable() => Dismiss();
        void OnDestroy()
        {
            if (video != null) { video.loopPointReached -= OnVideoEnded; video.errorReceived -= OnVideoError; video.targetTexture = null; }
            if (videoTexture != null) { videoTexture.Release(); Destroy(videoTexture); }
        }

        void BuildView()
        {
            overlay = new GameObject("Instructor Lesson", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            overlay.transform.SetParent(transform, false);
            overlayGroup = overlay.GetComponent<CanvasGroup>();
            var canvas = overlay.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 31100;
            var scaler = overlay.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            var shade = Rect("Backdrop", overlay.transform, 0, 0, 0, 0);
            shade.anchorMin = Vector2.zero; shade.anchorMax = Vector2.one; shade.offsetMin = shade.offsetMax = Vector2.zero;
            shade.gameObject.AddComponent<InstructorBackdropGraphic>();
            var stage = Rect("Layout", overlay.transform, 0, 0, 1920, 1080);
            stage.anchorMin = stage.anchorMax = stage.pivot = new Vector2(.5f, .5f);
            stage.anchoredPosition = Vector2.zero;
            var fitter = stage.gameObject.AddComponent<AspectRatioFitter>(); fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent; fitter.aspectRatio = 16f / 9f;
            // Children use normalized reference coordinates, keeping layout inside ultrawide / 4:3 screens.
            var reference = Rect("Reference", stage, 0, 0, 1920, 1080);
            reference.gameObject.AddComponent<InstructorLessonLayout>();
            motionRoot = Rect("Presentation", reference, 0, 0, 1920, 1080);
            var layout = motionRoot;
            Text("Eyebrow", layout, 80, 1029, 1100, 30, 20, "HM PROTECTION  /  TRAINING REVIEW").color = new Color(.62f, .66f, .70f);
            outcome = Text("Outcome", layout, 80, 980, 1100, 44, 32, ""); outcome.fontStyle = FontStyles.Bold;
            // Reserve space above the supplied nameplate so it never covers the video picture.
            var televisionRoot = Rect("Television Stage", layout, 57.6f, 93.6f, 1920, 1080);
            televisionRoot.localScale = Vector3.one * .91f;
            Artwork("Television", televisionRoot, "Television", 0, 300, 1280, 740);
            var screen = Rect("Screen", televisionRoot, 72, 457, 1140, 506);
            screen.gameObject.AddComponent<Image>().color = new Color(.035f, .045f, .06f);
            videoImage = Rect("Video", screen, 0, 0, 1140, 506).gameObject.AddComponent<RawImage>();
            videoImage.rectTransform.anchorMin = videoImage.rectTransform.anchorMax = videoImage.rectTransform.pivot = new Vector2(.5f, .5f);
            videoImage.rectTransform.anchoredPosition = Vector2.zero;
            videoImage.raycastTarget = false;
            var videoFit = videoImage.gameObject.AddComponent<AspectRatioFitter>(); videoFit.aspectMode = AspectRatioFitter.AspectMode.FitInParent; videoFit.aspectRatio = 16f / 9f;
            status = Text("Video Status", screen, 100, 115, 940, 250, 43, ""); status.alignment = TextAlignmentOptions.Center;
            status.color = new Color(.84f, .87f, .9f);
            screenKicker = Text("Screen Kicker", screen, 100, 375, 940, 34, 20, ""); screenKicker.alignment = TextAlignmentOptions.Center;
            screenKicker.color = new Color(.63f, .68f, .74f);
            Artwork("Instructor", layout, "Instructor", 1270, 100, 535, 958);
            // The supplied HAIMO artwork includes its own nameplate and transparent margins.
            // Keep its native aspect and full UVs so the border and lettering are not stretched/cropped.
            Artwork("Dialogue Frame", layout, "Dialogue", 72, -18, 1776, 1776f * 725f / 2170f);
            dialogueRoot = Rect("Dialogue Content", layout, 0, 0, 1920, 1080);
            dialogueGroup = dialogueRoot.gameObject.AddComponent<CanvasGroup>();
            dialogue = Text("Dialogue", dialogueRoot, 164, 170, 1580, 170, 34, "");
            dialogue.enableAutoSizing = true; dialogue.fontSizeMin = 27; dialogue.fontSizeMax = 34; dialogue.lineSpacing = 5f;
            progress = Text("Progress", layout, 166, 91, 270, 34, 21, "01 / 03"); progress.color = new Color(.62f, .67f, .73f);
            inputHint = Text("Input Hint", layout, 890, 91, 845, 34, 22, "PRESS ANY KEY TO CONTINUE");
            inputHint.alignment = TextAlignmentOptions.MidlineRight; inputHint.color = new Color(.83f, .85f, .88f);
            videoTexture = new RenderTexture(1920, 1080, 0) { name = "Instructor Video" }; videoTexture.Create();
            videoImage.texture = videoTexture;
            CreateVideoPlayer();
            overlay.SetActive(false);
        }
        void CreateVideoPlayer()
        {
            // Give every clip its own decoder lifetime, including after a failed/missing clip.
            if (video != null)
            {
                video.Stop(); video.loopPointReached -= OnVideoEnded; video.errorReceived -= OnVideoError;
                video.targetTexture = null; Destroy(video.gameObject);
            }
            var output = new GameObject("Instructor Video Output"); output.transform.SetParent(transform, false);
            video = output.AddComponent<VideoPlayer>(); video.playOnAwake = false; video.isLooping = false;
            video.source = VideoSource.Url; video.renderMode = VideoRenderMode.RenderTexture;
            // Match the existing CG audio route; avoid the platform's direct-audio clock in background review.
            var audio = output.AddComponent<AudioSource>(); audio.playOnAwake = false; audio.spatialBlend = 0f;
            video.audioOutputMode = VideoAudioOutputMode.AudioSource; video.controlledAudioTrackCount = 1;
            video.EnableAudioTrack(0, true); video.SetTargetAudioSource(0, audio);
            video.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
            // Imported clips can start at a nonzero timestamp; do not wait forever for timestamp zero.
            video.waitForFirstFrame = false;
            video.skipOnDrop = true;
            video.aspectRatio = VideoAspectRatio.Stretch;
            video.targetTexture = videoTexture;
            video.loopPointReached += OnVideoEnded; video.errorReceived += OnVideoError;
        }
        static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
        {
            var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); r.SetParent(parent, false);
            r.anchorMin = r.anchorMax = r.pivot = Vector2.zero; r.anchoredPosition = new Vector2(x, y); r.sizeDelta = new Vector2(w, h); return r;
        }
        static TMP_Text Text(string name, Transform parent, float x, float y, float w, float h, float size, string value)
        {
            var t = Rect(name, parent, x, y, w, h).gameObject.AddComponent<TextMeshProUGUI>();
            t.font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            t.fontSize = size; t.text = value; t.color = Color.white; t.raycastTarget = false;
            t.alignment = TextAlignmentOptions.MidlineLeft; return t;
        }
        static RawImage Artwork(string name, Transform parent, string resource, float x, float y, float w, float h)
        {
            var image = Rect(name, parent, x, y, w, h).gameObject.AddComponent<RawImage>();
            image.texture = Resources.Load<Texture2D>("InstructorLesson/" + resource); image.raycastTarget = false; return image;
        }
    }
}
