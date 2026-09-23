using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using HMProtection.Navigation;

namespace HMProtection.UI
{
    /// <summary>One available training environment; locked cards never receive input.</summary>
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
        [SerializeField] private string officeScenePath = "Assets/Scenes/办公室场景.unity";
        [Tooltip("选完场景后的起火 CG 转场（闪黑→CG→黑场渐出），留空则直接加载")]
        [SerializeField] private CgTransitionPlayer cgTransition;

        public HMProtection.EntityAdapters.LevelDefinition officeDefinition;

        private bool officeSelected;
        private bool loading;

        public void Open()
        {
            if (loading) return;
            officeSelected = false;
            mainMenuContent.SetActive(false);
            windowContent.SetActive(true);
            loadingOverlay.SetActive(false);
            officeCard.interactable = backButton.interactable = closeButton.interactable = true;
            RefreshSelection();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            EventSystem.current?.SetSelectedGameObject(officeCard.gameObject);
        }

        public void SelectOffice()
        {
            if (loading) return;
            officeSelected = true;
            RefreshSelection();
        }

        private void RefreshSelection()
        {
            startButton.interactable = officeSelected;
            selectedBadge.SetActive(officeSelected);
            officeCard.GetComponent<MenuButtonGraphic>().Style = officeSelected
                ? MenuButtonGraphic.ButtonStyle.Secondary : MenuButtonGraphic.ButtonStyle.Quiet;
            selectionStatus.text = officeSelected ? "Selected: Office" : "Select a scene to continue.";
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
            if (!officeSelected || loading) return;
            StartCoroutine(LoadOffice());
        }

        private IEnumerator LoadOffice()
        {
            HMProtection.Quiz.OfficeFireChoiceFlow.RequestOnNextOfficeLoad();
            loading = true;
            startButton.interactable = officeCard.interactable = backButton.interactable = closeButton.interactable = false;
            // 起火 CG 转场：立刻闪黑盖住界面，CG 与场景加载并行
            if (cgTransition != null && cgTransition.HasClip) cgTransition.BeginCgTransition();
            else QuizLoadingOverlay.Show();
            loadingOverlay.SetActive(true);
            loadingProgress.fillAmount = 0;
            loadingLabel.text = "Loading Office... 0%";
            EventSystem.current?.SetSelectedGameObject(null);
            NavigationOperation operation;
            string error;
            bool accepted = officeDefinition != null
                ? AppNavigationService.Instance.TryLoadDefinition(officeDefinition, out operation, out error)
                : AppNavigationService.Instance.TryLoadSingle(officeScenePath, out operation, out error);
            if (!accepted)
            {
                HMProtection.Quiz.OfficeFireChoiceFlow.CancelPendingEntry();
                RestoreAfterLoadFailure(error);
                yield break;
            }
            if (cgTransition != null && cgTransition.HasClip) cgTransition.BeginCgTransition(operation);
            while (operation.State == NavigationState.Loading)
            {
                float progress = operation.Progress;
                loadingProgress.fillAmount = progress;
                loadingLabel.text = "Loading Office... " + Mathf.RoundToInt(progress * 100) + "%";
                QuizLoadingOverlay.SetProgress(.05f + .1f * progress, "Loading the training scene...");
                yield return null;
            }
            if (operation.State == NavigationState.Failed || operation.State == NavigationState.Cancelled)
            {
                HMProtection.Quiz.OfficeFireChoiceFlow.CancelPendingEntry();
                RestoreAfterLoadFailure(operation.Error);
                yield break;
            }
            loadingProgress.fillAmount = 1;
            loadingLabel.text = "Loading Office... 100%";
            // 等 CG 播完（或被跳过）再进第一人称；场景在黑场后早已加载就绪，激活瞬间完成
            if (cgTransition != null && cgTransition.HasClip)
                while (!cgTransition.VideoDone) yield return null;
            operation.AllowActivation();
        }

        void RestoreAfterLoadFailure(string error)
        {
            loading = false;
            if (cgTransition != null && cgTransition.HasClip) cgTransition.CancelTransition();
            QuizLoadingOverlay.Hide();
            loadingOverlay.SetActive(false);
            officeCard.interactable = backButton.interactable = closeButton.interactable = true;
            startButton.interactable = officeSelected;
            selectionStatus.text = "Unable to load Office. Please try again.";
            Debug.LogError("Office scene load failed: " + error, this);
            EventSystem.current?.SetSelectedGameObject(startButton.gameObject);
        }
    }
}
