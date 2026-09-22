using System;
using System.Collections;
using System.Collections.Generic;
using HMProtection.Core;
using HMProtection.Quiz;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace HMProtection.UI
{
    /// <summary>选关窗口——卡片由关卡注册表（Resources/Configs/levels.json）生成，
    /// 见《关卡架构设计（骨架版）》§五：「以现有 officeCard 为模板克隆生成卡片，按 status 渲染
    /// 可用/锁定/即将开放三态；两张静态 COMING SOON 占位卡停用，由数据驱动替代」。
    ///
    /// 加一个关卡 = 做场景 + 写一份 level_xxx.json + 注册表加一行；**本文件与预制体都不用改**。
    ///
    /// 视觉部分（SceneSelectionUI.prefab、MenuButtonGraphic 样式、尺寸/间距、章节排布）全部沿用
    /// 协作者交付件：运行时只写卡片文字、选中态与按钮可用性，预制体与场景资产都没被改写。
    /// </summary>
    public sealed class SceneSelectUI : MonoBehaviour
    {
        public GameObject windowContent;
        public GameObject mainMenuContent;
        public Button returnFocus;
        public Button officeCard;
        public Button startButton;
        public Button backButton;
        public Button closeButton;
        public GameObject selectedBadge;
        public TextMeshProUGUI selectionStatus;
        public GameObject loadingOverlay;
        public Image loadingProgress;
        public TextMeshProUGUI loadingLabel;
        [Tooltip("卡片横向间距（像素）：与 SceneSelectionUISetup 里 58 / 558 / 1058 的排布一致")]
        [SerializeField] private float cardSpacing = 500f;
        [Tooltip("选完场景后的起火 CG 转场（闪黑→CG→黑场渐出）：只对配了 transitionVideo 的关卡生效，留空则直接加载")]
        [SerializeField] private CgTransitionPlayer cgTransition;

        /// <summary>一张关卡卡片：数据 + 运行时控件引用。</summary>
        private sealed class Card
        {
            public LevelEntry entry;
            public Button button;
            public GameObject badge;
        }

        private readonly List<Card> cards = new List<Card>();
        private int selectedIndex = -1;
        private bool loading;

        public void Open()
        {
            if (loading) return;
            mainMenuContent.SetActive(false);
            windowContent.SetActive(true);
            loadingOverlay.SetActive(false);
            BuildCards();
            selectedIndex = -1;
            RefreshSelection();
            RefreshInteractable();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            EventSystem.current?.SetSelectedGameObject(FirstSelectable());
        }

        /// <summary>预制体里 officeCard 的持久监听指向本方法；运行时已按索引重接（见 Bind），
        /// 这里保留以免预制体出现「丢失方法」告警，兼作数据异常时的手动兜底入口。</summary>
        public void SelectOffice() => Select(0);

        /// <summary>选中第 index 张卡（卡片按钮在 Bind 里按索引接过来）。</summary>
        public void Select(int index)
        {
            if (loading) return;
            if (index < 0 || index >= cards.Count) return;
            if (!cards[index].button.interactable) return;
            selectedIndex = index;
            RefreshSelection();
            RefreshInteractable();
        }

        public void Close()
        {
            if (loading) return;
            windowContent.SetActive(false);
            mainMenuContent.SetActive(true);
            EventSystem.current?.SetSelectedGameObject(returnFocus.gameObject);
        }

        private void Update()
        {
            if (windowContent.activeSelf && !loading && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                Close();
        }

        public void StartSelectedScene()
        {
            if (loading || selectedIndex < 0 || selectedIndex >= cards.Count) return;
            LevelEntry entry = cards[selectedIndex].entry;
            string scenePath = ResolveScenePath(entry);
            if (string.IsNullOrEmpty(scenePath))
            {
                selectionStatus.text = CardTitle(entry) + " is unavailable. Please try again later.";
                Debug.LogError("[SceneSelect] 关卡场景不在构建列表里：id=" + entry.id
                               + "，sceneName=\"" + entry.sceneName + "\"（把它加进 EditorBuildSettings 的 Scenes In Build）", this);
                return;
            }
            StartCoroutine(LoadLevel(entry, scenePath));
        }

        // ==== 卡片（数据驱动，见类注释） ====

        /// <summary>按注册表生成卡片。只在还没建过时建；注册表读不出来时保留原占位卡，下次 Open 再试。</summary>
        private void BuildCards()
        {
            if (cards.Count > 0) return;
            if (!LevelCatalog.TryLoad(out LevelCatalog catalog, out string error))
            {
                selectionStatus.text = "Level list unavailable. Please try again later.";
                Debug.LogError("[SceneSelect] " + error, this);
                return;
            }

            IReadOnlyList<LevelEntry> entries = catalog.Levels;
            Transform panel = officeCard.transform.parent;
            Vector2 origin = ((RectTransform)officeCard.transform).anchoredPosition;
            int insertAt = officeCard.transform.GetSiblingIndex();
            HidePlaceholders(panel);

            for (int i = 0; i < entries.Count; i++)
            {
                // 第一张卡直接用协作者摆好的 officeCard（视觉零改动），其余克隆它
                GameObject go = i == 0 ? officeCard.gameObject : Instantiate(officeCard.gameObject, panel);
                go.name = "Card - " + entries[i].id;
                if (i > 0)
                {
                    ((RectTransform)go.transform).anchoredPosition = origin + new Vector2(cardSpacing * i, 0f);
                    go.transform.SetSiblingIndex(insertAt + i);
                }
                cards.Add(Bind(go, entries[i], i));
            }
            Log("选关卡片 " + cards.Count + " 张：" + string.Join(" / ", CardTitles()));
        }

        private Card Bind(GameObject go, LevelEntry entry, int index)
        {
            bool available = IsAvailable(entry);
            var button = go.GetComponent<Button>();
            // 模板上那条指向 SelectOffice() 的持久监听会随克隆一起复制；这里按索引重接，
            // 否则每张卡都只会去选第一关。（改的是运行时场景实例，预制体资产不受影响）
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => Select(index));
            button.interactable = available;
            if (!available) button.navigation = new Navigation { mode = Navigation.Mode.None };

            SetText(go.transform, "Available", StatusLabel(entry));
            SetText(go.transform, "SceneName", CardTitle(entry));
            SetText(go.transform, "Description", CardDescription(entry));

            Image preview = FindChildComponent<Image>(go.transform, "OfficePreview");
            if (preview != null)
            {
                Sprite art = string.IsNullOrEmpty(entry.cardImage) ? null : Resources.Load<Sprite>(entry.cardImage);
                if (art != null) { preview.sprite = art; preview.enabled = true; }
                // 克隆卡不继承模板的办公室切图；等美术给某关的卡图，把 Resources 路径填进注册表即可
                else if (index > 0) preview.enabled = false;
            }
            return new Card { entry = entry, button = button, badge = ResolveBadge(go.transform) };
        }

        /// <summary>停用协作者摆的两张静态 COMING SOON 占位卡——它们的位置由注册表数据接管。</summary>
        private static void HidePlaceholders(Transform panel)
        {
            for (int i = 1; i <= 8; i++)
            {
                Transform placeholder = panel.Find("ComingSoon" + i);
                if (placeholder == null) continue;
                placeholder.gameObject.SetActive(false);
            }
        }

        private void RefreshSelection()
        {
            for (int i = 0; i < cards.Count; i++)
            {
                bool selected = i == selectedIndex;
                var graphic = cards[i].button.GetComponent<MenuButtonGraphic>();
                if (graphic != null)
                    graphic.Style = selected ? MenuButtonGraphic.ButtonStyle.Secondary : MenuButtonGraphic.ButtonStyle.Quiet;
                if (cards[i].badge != null) cards[i].badge.SetActive(selected);
            }
            selectionStatus.text = selectedIndex >= 0
                ? "Selected: " + CardTitle(cards[selectedIndex].entry)
                : "Select a scene to continue.";
        }

        /// <summary>按钮可用性：卡片按 status，开始按钮按「已选且不在加载中」，其余按是否正在加载。</summary>
        private void RefreshInteractable()
        {
            for (int i = 0; i < cards.Count; i++) cards[i].button.interactable = IsAvailable(cards[i].entry);
            if (startButton != null) startButton.interactable = !loading && selectedIndex >= 0;
            if (backButton != null) backButton.interactable = !loading;
            if (closeButton != null) closeButton.interactable = !loading;
        }

        private GameObject FirstSelectable()
        {
            for (int i = 0; i < cards.Count; i++)
                if (cards[i].button.interactable) return cards[i].button.gameObject;
            return officeCard.gameObject;
        }

        private GameObject ResolveBadge(Transform card)
        {
            if (selectedBadge != null && selectedBadge.transform.IsChildOf(card)) return selectedBadge;
            return FindChildComponent<Transform>(card, "SelectedBadge")?.gameObject;
        }

        // ==== 进关 ====

        /// <summary>关卡只按场景名登记；真实路径从构建列表反查——顺带把「场景没加进构建列表」
        /// 变成一条明确报错，否则 LoadSceneAsync 只会静默失败、黑屏卡住。</summary>
        private static string ResolveScenePath(LevelEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.sceneName)) return null;
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == entry.sceneName) return path;
            }
            return null;
        }

        /// <summary>办公室那套问答流程要靠「从主菜单进入」的入场请求决定是否自启出题
        /// （直开场景时保持静默）。判据取关卡配置里有没有答题段，不写死关卡 id。</summary>
        private static bool WantsQuizEntry(LevelEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.configPath)) return false;
            if (!ConfigLoader.TryLoadLevel(entry.configPath, out LevelConfigDto config, out string error))
            {
                Debug.LogWarning("[SceneSelect] 关卡配置读取失败（不影响进关）：" + entry.id + "：" + error);
                return false;
            }
            if (config.stages == null) return false;
            for (int i = 0; i < config.stages.Length; i++)
            {
                StageDto stage = config.stages[i];
                if (stage != null && string.Equals(stage.kind, "quiz", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private IEnumerator LoadLevel(LevelEntry entry, string scenePath)
        {
            if (WantsQuizEntry(entry)) OfficeFireChoiceFlow.RequestOnNextOfficeLoad();
            loading = true;
            RefreshInteractable();
            string title = CardTitle(entry);
            // CG 转场只属于配了 transitionVideo 的关卡（当前只有第一关）：其余关卡走加载页直接进
            bool withCg = cgTransition != null && cgTransition.HasClip && !string.IsNullOrEmpty(entry.transitionVideo);
            if (withCg) cgTransition.BeginCgTransition();
            else QuizLoadingOverlay.Show();
            loadingOverlay.SetActive(true);
            // 加载页的大标题在预制体里写死成 "OFFICE"，按所选关卡改写（第二关不该显示 OFFICE）；
            // 第一关的卡片标题就是 OFFICE，视觉不变。
            TextMeshProUGUI loadingTitle = FindChildComponent<TextMeshProUGUI>(loadingOverlay.transform, "Title");
            if (loadingTitle != null) loadingTitle.text = title;
            loadingProgress.fillAmount = 0;
            loadingLabel.text = "Loading " + title + "... 0%";
            EventSystem.current?.SetSelectedGameObject(null);
            // Paint loading feedback before beginning scene deserialization.
            yield return null;
            AsyncOperation operation = null;
            string error = null;
            try { operation = SceneManager.LoadSceneAsync(scenePath, LoadSceneMode.Single); }
            catch (Exception exception) { error = exception.Message; }
            if (operation == null)
            {
                yield return FailLoad(withCg, "Unable to load " + title + ". Please try again.", error);
                yield break;
            }
            operation.allowSceneActivation = !withCg;
            while (operation.progress < .9f)
            {
                float progress = Mathf.Clamp01(operation.progress / .9f);
                loadingProgress.fillAmount = progress;
                loadingLabel.text = "Loading " + title + "... " + Mathf.RoundToInt(progress * 100) + "%";
                QuizLoadingOverlay.SetProgress(.05f + .1f * progress, "Loading the training scene...");
                yield return null;
            }
            loadingProgress.fillAmount = 1;
            loadingLabel.text = "Loading " + title + "... 100%";
            yield return null;
            if (withCg)
            {
                // 等 CG 播完（或被跳过）再进第一人称；场景在黑场后早已加载就绪，激活瞬间完成
                while (!cgTransition.VideoDone) yield return null;
                operation.allowSceneActivation = true;
            }
            else
            {
                while (!operation.isDone) yield return null;
            }
        }

        private IEnumerator FailLoad(bool withCg, string message, string error)
        {
            OfficeFireChoiceFlow.CancelPendingEntry();
            loading = false;
            // 黑场退回，别把用户困在黑屏里
            if (withCg && cgTransition != null) cgTransition.CancelTransition();
            QuizLoadingOverlay.Hide();
            loadingOverlay.SetActive(false);
            RefreshInteractable();
            selectionStatus.text = message;
            Debug.LogError("[SceneSelect] " + message + (string.IsNullOrEmpty(error) ? "" : "  " + error), this);
            EventSystem.current?.SetSelectedGameObject(startButton.gameObject);
            yield break;
        }

        // ==== 卡片文案与工具 ====

        private static bool IsAvailable(LevelEntry entry) =>
            entry != null && string.Equals(entry.status, "available", StringComparison.OrdinalIgnoreCase);

        private static string StatusLabel(LevelEntry entry)
        {
            if (IsAvailable(entry)) return "AVAILABLE";
            return string.Equals(entry.status, "locked", StringComparison.OrdinalIgnoreCase) ? "LOCKED" : "COMING SOON";
        }

        private static string CardTitle(LevelEntry entry) =>
            entry == null ? "?" : (string.IsNullOrEmpty(entry.cardTitle) ? entry.displayName : entry.cardTitle);

        private static string CardDescription(LevelEntry entry)
        {
            if (entry == null) return "";
            if (!string.IsNullOrEmpty(entry.cardDescription)) return entry.cardDescription;
            return string.IsNullOrEmpty(entry.displayName) ? "" : entry.displayName;
        }

        private List<string> CardTitles()
        {
            var titles = new List<string>();
            for (int i = 0; i < cards.Count; i++) titles.Add(CardTitle(cards[i].entry));
            return titles;
        }

        private static void SetText(Transform card, string childName, string value)
        {
            TextMeshProUGUI text = FindChildComponent<TextMeshProUGUI>(card, childName);
            if (text != null) text.text = value;
        }

        private static T FindChildComponent<T>(Transform root, string childName) where T : Component
        {
            T[] all = root.GetComponentsInChildren<T>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == childName) return all[i];
            return null;
        }

        private void Log(string message) => Debug.Log("[SceneSelect] " + message, this);
    }
}
