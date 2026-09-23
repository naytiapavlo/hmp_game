using UnityEngine;
using UnityEngine.Events;

namespace HMProtection.UI
{
    /// <summary>Presentation-only menu. Connect feature owners in the Inspector.</summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuView : MonoBehaviour
    {
        [Header("Navigation hooks — connect when each feature is implemented")]
        public UnityEvent startTrainingRequested = new UnityEvent();
        public UnityEvent trainingRecordsRequested = new UnityEvent();
        public UnityEvent deviceSettingsRequested = new UnityEvent();
        public UnityEvent howToPlayRequested = new UnityEvent();

        private void Start()
        {
            // Cursor state survives scene loads. Claim it after outgoing gameplay/modal
            // teardown, which may restore the hidden first-person cursor.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void StartTraining() => startTrainingRequested.Invoke();
        public void TrainingRecords() => trainingRecordsRequested.Invoke();
        public void DeviceSettings() => deviceSettingsRequested.Invoke();
        public void HowToPlay() => howToPlayRequested.Invoke();
        public void Exit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
