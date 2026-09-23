using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HMProtection.Quiz
{
    public sealed class SceneChoiceBubble : MonoBehaviour
    {
        public Button button;
        public RectTransform bodyRect;
        public SceneChoiceArtwork artwork;
        public TextMeshProUGUI badge, label;
        public SceneChoiceOption Option { get; private set; }
        public Transform Anchor { get; private set; }
        public bool TargetVisible { get; private set; }
        public Vector3 WorldPosition => TryGetWorldPosition(out var position) ? position : Vector3.zero;
        public Rect LayoutRect => new Rect(bodyRect.anchoredPosition - bodyRect.sizeDelta / 2, bodyRect.sizeDelta);
        bool locked;
        Func<Vector3?> entityPosition;
        public bool TryGetWorldPosition(out Vector3 position)
        {
            position = Vector3.zero;
            if (Option == null) return false;
            if (Option.target.mode != "anchor") position = Option.target.Position;
            else if (entityPosition != null)
            {
                var current = entityPosition();
                if (!current.HasValue) return false;
                position = current.Value;
            }
            else if (Anchor != null) position = Anchor.position;
            else return false;
            position += Option.target.Offset;
            return true;
        }
        public void BindEntity(SceneChoiceOption option, Func<Vector3?> position, int index, Action<SceneChoiceBubble> selected, bool numbered = false)
        {
            Bind(option, null, index, selected, numbered);
            entityPosition = position;
        }
        public void Bind(SceneChoiceOption option, Transform anchor, int index, Action<SceneChoiceBubble> selected, bool numbered = false)
        {
            Option = option; Anchor = anchor; entityPosition = null; locked = false; TargetVisible = false;
            gameObject.SetActive(true); button.onClick.RemoveAllListeners(); button.interactable = true;
            button.onClick.AddListener(() => { if (!locked && TargetVisible) selected(this); });
            string letters = ""; for (int n = index + 1; n > 0; n = (n - 1) / 26) letters = (char)('A' + (n - 1) % 26) + letters;
            badge.text = numbered ? (index + 1).ToString() : letters; label.text = option.label;
            float height = Mathf.Max(90, label.GetPreferredValues(option.label, option.Width - 110, Mathf.Infinity).y + 42);
            bodyRect.sizeDelta = new Vector2(option.Width, height);
            artwork.color = Color.white;
        }
        public void Place(Camera camera, RectTransform canvas, float topReserve)
        {
            bool hasPosition = TryGetWorldPosition(out var worldPosition);
            Vector3 viewport = camera.WorldToViewportPoint(worldPosition);
            TargetVisible = hasPosition && viewport.z > 0 && viewport.x >= 0 && viewport.x <= 1 && viewport.y >= 0 && viewport.y <= 1;
            gameObject.SetActive(TargetVisible);
            if (!TargetVisible) return;
            Vector2 target = new Vector2(Mathf.Lerp(canvas.rect.xMin, canvas.rect.xMax, viewport.x), Mathf.Lerp(canvas.rect.yMin, canvas.rect.yMax, viewport.y));
            Vector2 desired = target + Option.Offset, half = bodyRect.sizeDelta / 2;
            desired.x = Mathf.Clamp(desired.x, canvas.rect.xMin + half.x + 24, canvas.rect.xMax - half.x - 24);
            desired.y = Mathf.Clamp(desired.y, canvas.rect.yMin + half.y + 24, canvas.rect.yMax - half.y - 24 - topReserve);
            bodyRect.anchoredPosition = desired;
            artwork.PointAt(target - desired);
        }
        public void Lock(bool selected) { locked = true; button.interactable = false; artwork.color = selected ? Color.white : new Color(.65f, .65f, .65f, .65f); }
        public void Release() { button.onClick.RemoveAllListeners(); gameObject.SetActive(false); Anchor = null; entityPosition = null; Option = null; }
    }
}
