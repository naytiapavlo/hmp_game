using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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
        public Button ContinueButton => next;
        public TMP_Text DialogueText => dialogue;
        public VideoPlayer Player => video;
        public event Action Closed;

        GameObject overlay, fallbackSystem;
        TMP_Text dialogue, speaker, status, progress, nextLabel, outcome;
        Button next;
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
            IsShowing = true;
            oldLock = Cursor.lockState; oldCursor = Cursor.visible;
            oldSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            foreach (var root in gameObject.scene.GetRootGameObjects())
            {
                foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
                    if (b is body || b is Interactor) { controls[b] = b.enabled; b.enabled = false; }
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
            speaker.text = lesson.speaker;
            outcome.text = correct ? "CORRECT  /  LET'S REVIEW" : "INCORRECT  /  LET'S LEARN";
            outcome.color = correct ? new Color(.48f, .95f, .72f) : new Color(1f, .48f, .42f);
            status.text = "FIRE SAFETY\n\nA moment to learn";
            videoImage.gameObject.SetActive(false);
            ShowLine(LessonStep.First);
            return true;
        }

        void ShowLine(LessonStep step)
        {
            Step = step;
            dialogue.text = step == LessonStep.First ? lines.first : step == LessonStep.Second ? lines.second : lines.third;
            progress.text = step == LessonStep.First ? "01 / 03" : step == LessonStep.Second ? "02 / 03" : "03 / 03";
            nextLabel.text = step == LessonStep.Second ? "Watch video  >" : step == LessonStep.Third ? "Continue training  >" : "Next  >";
            next.interactable = true;
            inputAfter = Time.unscaledTime + .2f;
            EventSystem.current?.SetSelectedGameObject(next.gameObject);
        }

        public void Advance()
        {
            if (!IsShowing || Time.unscaledTime < inputAfter) return;
            inputAfter = Time.unscaledTime + .2f;
            if (Step == LessonStep.First) ShowLine(LessonStep.Second);
            else if (Step == LessonStep.Second)
            {
                Step = LessonStep.Video;
                progress.text = "VIDEO"; nextLabel.text = "Skip video  >";
                playback = StartCoroutine(PlayVideo());
            }
            else if (Step == LessonStep.Video)
            {
                StopVideo();
                ShowLine(LessonStep.Third);
            }
            else Dismiss();
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
            status.text = "Loading lesson...";
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
            status.text = ""; videoImage.gameObject.SetActive(true);
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
            playback = null;
            ShowLine(LessonStep.Third);
        }

        void VideoUnavailable(string reason)
        {
            LastVideoError = reason; video.Stop(); videoImage.gameObject.SetActive(false);
            status.text = "Lesson video is unavailable.\n\nYou can continue the review.";
            nextLabel.text = "Continue review  >";
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
            StopVideo(); overlay.SetActive(false);
            foreach (var item in controls) if (item.Key != null) item.Key.enabled = item.Value;
            foreach (var item in hud) if (item.Key != null) item.Key.SetActive(item.Value);
            controls.Clear(); hud.Clear();
            if (fallbackSystem != null) fallbackSystem.SetActive(false);
            Cursor.lockState = oldLock; Cursor.visible = oldCursor;
            EventSystem.current?.SetSelectedGameObject(oldSelection);
            IsShowing = false; Closed?.Invoke();
        }
        void OnDisable() => Dismiss();
        void OnDestroy()
        {
            if (video != null) { video.loopPointReached -= OnVideoEnded; video.errorReceived -= OnVideoError; video.targetTexture = null; }
            if (videoTexture != null) { videoTexture.Release(); Destroy(videoTexture); }
        }

        void BuildView()
        {
            overlay = new GameObject("Instructor Lesson", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            overlay.transform.SetParent(transform, false);
            var canvas = overlay.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 31100;
            var scaler = overlay.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            var shade = Rect("Backdrop", overlay.transform, 0, 0, 0, 0);
            shade.anchorMin = Vector2.zero; shade.anchorMax = Vector2.one; shade.offsetMin = shade.offsetMax = Vector2.zero;
            shade.gameObject.AddComponent<Image>().color = new Color(.035f, .028f, .03f, .98f);
            var stage = Rect("Layout", overlay.transform, 0, 0, 1920, 1080);
            stage.anchorMin = stage.anchorMax = stage.pivot = new Vector2(.5f, .5f);
            stage.anchoredPosition = Vector2.zero;
            var fitter = stage.gameObject.AddComponent<AspectRatioFitter>(); fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent; fitter.aspectRatio = 16f / 9f;
            // Children use normalized reference coordinates, keeping layout inside ultrawide / 4:3 screens.
            var layout = Rect("Reference", stage, 0, 0, 1920, 1080);
            layout.anchorMin = layout.anchorMax = Vector2.zero;
            layout.gameObject.AddComponent<InstructorLessonLayout>();
            Text("Eyebrow", layout, 72, 975, 1000, 50, 24, "HM PROTECTION  /  TRAINING REVIEW").color = new Color(.7f, .65f, .62f);
            outcome = Text("Outcome", layout, 72, 919, 1100, 55, 36, ""); outcome.fontStyle = FontStyles.Bold;
            Artwork("Television", layout, "Television", 0, 250, 1280, 740);
            var screen = Rect("Screen", layout, 72, 407, 1140, 506);
            screen.gameObject.AddComponent<Image>().color = new Color(.035f, .045f, .055f);
            videoImage = Rect("Video", screen, 0, 0, 1140, 506).gameObject.AddComponent<RawImage>();
            videoImage.rectTransform.anchorMin = videoImage.rectTransform.anchorMax = videoImage.rectTransform.pivot = new Vector2(.5f, .5f);
            videoImage.rectTransform.anchoredPosition = Vector2.zero;
            videoImage.raycastTarget = false;
            var videoFit = videoImage.gameObject.AddComponent<AspectRatioFitter>(); videoFit.aspectMode = AspectRatioFitter.AspectMode.FitInParent; videoFit.aspectRatio = 16f / 9f;
            status = Text("Video Status", screen, 100, 120, 940, 266, 32, ""); status.alignment = TextAlignmentOptions.Center;
            status.color = new Color(.7f, .75f, .78f);
            Artwork("Instructor", layout, "Instructor", 1270, 70, 550, 985);
            var panel = Artwork("Dialogue Frame", layout, "Dialogue", 360, 32, 1490, 370);
            panel.uvRect = new Rect(0, .265f, 1, .565f);
            var namePlate = Rect("Name Plate", layout, 487, 298, 325, 78);
            namePlate.gameObject.AddComponent<Image>().color = new Color(.40f, .055f, .07f);
            speaker = Text("Speaker", namePlate, 0, 0, 325, 78, 31, "INSTRUCTOR"); speaker.alignment = TextAlignmentOptions.Center; speaker.fontStyle = FontStyles.Bold;
            dialogue = Text("Dialogue", layout, 435, 122, 1270, 150, 34, ""); dialogue.enableAutoSizing = true; dialogue.fontSizeMin = 25; dialogue.fontSizeMax = 34;
            progress = Text("Progress", layout, 442, 78, 180, 42, 21, "01 / 03");
            progress.color = new Color(.7f, .73f, .77f);
            var buttonRect = Rect("Continue", layout, 1400, 70, 350, 60);
            var buttonImage = buttonRect.gameObject.AddComponent<Image>(); buttonImage.color = new Color(.6f, .055f, .07f);
            next = buttonRect.gameObject.AddComponent<Button>(); next.targetGraphic = buttonImage; next.onClick.AddListener(Advance);
            nextLabel = Text("Label", buttonRect, 0, 0, 350, 60, 25, "Next  >"); nextLabel.alignment = TextAlignmentOptions.Center;
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
