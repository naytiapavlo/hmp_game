using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

namespace HMProtection.UI
{
    /// <summary>
    /// CG 转场播放器：选完场景后闪黑 → 全屏播放起火 CG → 进第一人称前黑场渐出。
    /// 挂在场景根物体「CG转场」上（必须是根物体，DontDestroyOnLoad 才能跨场景存活；
    /// 场景里默认保持未激活，由本脚本在转场开始时激活）。
    /// 由 SceneSelectUI 在开始加载时调用 BeginCgTransition()，全程与场景加载并行。
    /// 播放期间按任意键/鼠标可跳过（留有防误触缓冲）。
    /// </summary>
    public sealed class CgTransitionPlayer : MonoBehaviour
    {
        [Tooltip("视频播放器（clip、RenderTexture 由挂载脚本配置）")]
        [SerializeField] private VideoPlayer videoPlayer;
        [Tooltip("全屏视频画面")]
        [SerializeField] private RawImage videoImage;
        [Tooltip("全屏黑场图，负责闪黑/渐出")]
        [SerializeField] private Image blackout;
        [Tooltip("整个转场画布（平时禁用，转场时激活）")]
        [SerializeField] private GameObject canvasRoot;

        [Tooltip("点开训练后黑场渐入时长（秒），「闪黑」要快")]
        [SerializeField] private float fadeOutDuration = 0.25f;
        [Tooltip("进入第一人称后黑场渐出时长（秒）")]
        [SerializeField] private float fadeInDuration = 0.6f;
        [Tooltip("跳过保护期（秒）：开始播放后这么短时间内不接受跳过输入")]
        [SerializeField] private float skipGracePeriod = 0.4f;

        /// <summary>CG 是否已播完（SceneSelectUI 等它为 true 才激活目标场景）。</summary>
        public bool VideoDone { get; private set; }

        private bool playing;
        private bool sceneActivated;
        private bool canceled;
        private bool playbackEnded;
        private bool playbackFailed;

        private void Awake()
        {
            if (canvasRoot == null) canvasRoot = gameObject;
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (videoPlayer != null)
            {
                videoPlayer.loopPointReached += OnVideoEnded;
                videoPlayer.errorReceived += OnVideoError;
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (videoPlayer != null)
            {
                videoPlayer.loopPointReached -= OnVideoEnded;
                videoPlayer.errorReceived -= OnVideoError;
            }
        }

        private void OnVideoEnded(VideoPlayer source) => playbackEnded = true;

        private void OnVideoError(VideoPlayer source, string message)
        {
            playbackFailed = true;
            Debug.LogWarning("CG playback failed; continuing to Office: " + message, this);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 任何新场景激活（即办公室场景）都视为可以开始渐出
            if (playing && !canceled) sceneActivated = true;
        }

        /// <summary>是否具备播放条件（视频片段已配置）。</summary>
        public bool HasClip => videoPlayer != null && videoPlayer.clip != null;

        /// <summary>开始转场：闪黑 → 播 CG → 停在黑场，等场景激活后渐出。</summary>
        public void BeginCgTransition()
        {
            if (playing || !HasClip) return;
            playing = true;
            // 先激活物体再开协程：未激活物体上的协程不会执行
            canvasRoot.SetActive(true);
            DontDestroyOnLoad(gameObject); // 根物体才能跨场景存活
            StartCoroutine(TransitionRoutine());
        }

        /// <summary>加载失败时取消转场：恢复画面并清理。</summary>
        public void CancelTransition()
        {
            if (!playing) return;
            canceled = true;
            StopAllCoroutines();
            StartCoroutine(CancelRoutine());
        }

        private IEnumerator TransitionRoutine()
        {
            videoImage.enabled = false;

            // 1) 闪黑：菜单快速沉入黑场
            yield return Fade(blackout, 0f, 1f, fadeOutDuration);

            // 2) 全屏播放 CG（办公室场景在黑场后面并行预载）
            VideoDone = false;
            playbackEnded = playbackFailed = false;
            videoPlayer.Prepare();
            float prepareGuard = 15f;
            while (!videoPlayer.isPrepared && !playbackFailed && prepareGuard > 0f)
            {
                prepareGuard -= Time.unscaledDeltaTime;
                yield return null;
            }
            if (videoPlayer.isPrepared && !playbackFailed)
            {
                videoImage.enabled = true;
                videoPlayer.Play();
            }
            else playbackFailed = true;

            double limit = videoPlayer.clip.length + 5.0; // 兜底：解码异常时不卡死
            float elapsed = 0f;
            while (!playbackFailed && !playbackEnded && elapsed < limit)
            {
                elapsed += Time.unscaledDeltaTime;
                // 播放保护期过后，任意按键/鼠标跳过
                if (elapsed > skipGracePeriod && SkipPressed()) break;
                yield return null;
            }
            videoPlayer.Stop();
            videoImage.enabled = false;
            VideoDone = true; // SceneSelectUI 看到 true 才 allowSceneActivation

            // 3) 停在黑场，等目标场景激活（sceneLoaded 回调置位；30 秒兜底）
            float guard = 30f;
            while (!sceneActivated && guard > 0f)
            {
                guard -= Time.unscaledDeltaTime;
                yield return null;
            }

            // 4) 黑场渐出，露出第一人称画面
            yield return Fade(blackout, 1f, 0f, fadeInDuration);
            canvasRoot.SetActive(false);
            Destroy(gameObject);
        }

        private IEnumerator CancelRoutine()
        {
            if (videoPlayer != null) videoPlayer.Stop();
            yield return Fade(blackout, blackout.color.a, 0f, 0.2f);
            canvasRoot.SetActive(false);
            Destroy(gameObject);
        }

        private static bool SkipPressed()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame) return true;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return false;
            return mouse.leftButton.wasPressedThisFrame
                   || mouse.rightButton.wasPressedThisFrame
                   || mouse.middleButton.wasPressedThisFrame;
        }

        // 用 unscaled 时间做透明度渐变，避免 timeScale 影响转场节奏
        private IEnumerator Fade(Graphic target, float from, float to, float duration)
        {
            duration = Mathf.Max(0.01f, duration);
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                Color c = target.color;
                c.a = Mathf.Lerp(from, to, t / duration);
                target.color = c;
                yield return null;
            }
            Color finalColor = target.color;
            finalColor.a = to;
            target.color = finalColor;
        }
    }
}
