using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace HMProtection.Quiz
{
    public sealed class QuizViewController : MonoBehaviour
    {
        public Camera overviewCamera;
        public Transform officeGeometry;
        public CeilingVisibility ceiling;
        public VisibilityToggle character;
        public GameObject fallbackEventSystem;
        public bool IsOverview { get; private set; }
        public event Action OverviewExited;
        readonly Dictionary<Behaviour, bool> behaviours = new Dictionary<Behaviour, bool>();
        readonly Dictionary<Renderer, bool> renderers = new Dictionary<Renderer, bool>();
        readonly Dictionary<GameObject, bool> hud = new Dictionary<GameObject, bool>();
        bool ceilingVisible, characterVisible, mouseVisible, fallbackActive;
        CursorLockMode mouseLock;
        GameObject oldSelection;
        float aspect;
        public bool EnterOverview()
        {
            if (IsOverview) return true;
            if (overviewCamera == null || officeGeometry == null || !isActiveAndEnabled) return false;
            behaviours.Clear(); renderers.Clear(); hud.Clear();
            mouseLock = Cursor.lockState; mouseVisible = Cursor.visible;
            oldSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            foreach (var root in gameObject.scene.GetRootGameObjects())
            {
                foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
                    if (b is Camera || b is AudioListener || b is body || b is Interactor)
                    { behaviours[b] = b.enabled; b.enabled = false; }
                foreach (var h in root.GetComponentsInChildren<InteractionHUD>(true))
                { hud[h.gameObject] = h.gameObject.activeSelf; h.gameObject.SetActive(false); }
            }
            if (ceiling != null)
            {
                ceilingVisible = ceiling.ceilingsVisible;
                foreach (var r in ceiling.GetComponentsInChildren<MeshRenderer>(true)) if (r.name.StartsWith("SM_Ceiling", StringComparison.Ordinal)) renderers[r] = r.enabled;
                ceiling.SetCeilingsVisible(false);
            }
            if (character != null)
            {
                characterVisible = character.visible;
                foreach (var r in character.GetComponentsInChildren<Renderer>(true)) renderers[r] = r.enabled;
                character.SetVisible(false);
            }
            if (fallbackEventSystem != null)
            {
                fallbackActive = fallbackEventSystem.activeSelf;
                if (EventSystem.current == null) fallbackEventSystem.SetActive(true);
            }
            IsOverview = true; FitCamera(); overviewCamera.enabled = true;
            var listener = overviewCamera.GetComponent<AudioListener>(); if (listener != null) listener.enabled = true;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            return true;
        }
        public void FitCamera()
        {
            if (overviewCamera == null || officeGeometry == null) return;
            bool found = false; Bounds bounds = default;
            foreach (var r in officeGeometry.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.name == "floor" || r.name.StartsWith("SM_Ceiling", StringComparison.Ordinal) || r.bounds.size.x > 60 || r.bounds.size.z > 60) continue;
                if (!found) { bounds = r.bounds; found = true; } else bounds.Encapsulate(r.bounds);
            }
            if (!found) return;
            var tr = overviewCamera.transform;
            tr.rotation = Quaternion.Euler(65, -35, 0);
            tr.position = bounds.center - tr.forward * 50;
            float x = 0, y = 0;
            for (int i = 0; i < 8; i++)
            {
                var p = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var local = tr.InverseTransformPoint(p); x = Mathf.Max(x, Mathf.Abs(local.x)); y = Mathf.Max(y, Mathf.Abs(local.y));
            }
            aspect = overviewCamera.aspect;
            overviewCamera.orthographic = true;
            overviewCamera.orthographicSize = Mathf.Max(y, x / Mathf.Max(.1f, aspect)) * 1.35f;
            overviewCamera.nearClipPlane = .1f; overviewCamera.farClipPlane = 150;
        }
        public void ExitOverview()
        {
            if (!IsOverview) return;
            IsOverview = false;
            if (ceiling != null) ceiling.SetCeilingsVisible(ceilingVisible);
            if (character != null) character.SetVisible(characterVisible);
            foreach (var pair in renderers) if (pair.Key != null) pair.Key.enabled = pair.Value;
            foreach (var pair in behaviours) if (pair.Key != null) pair.Key.enabled = pair.Value;
            foreach (var pair in hud) if (pair.Key != null) pair.Key.SetActive(pair.Value);
            if (fallbackEventSystem != null) fallbackEventSystem.SetActive(fallbackActive);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(oldSelection);
            Cursor.lockState = mouseLock; Cursor.visible = mouseVisible;
            OverviewExited?.Invoke();
            behaviours.Clear(); renderers.Clear(); hud.Clear();
        }
        void LateUpdate() { if (IsOverview && !Mathf.Approximately(aspect, overviewCamera.aspect)) FitCamera(); }
        void OnDisable() => ExitOverview();
        void OnDestroy() => ExitOverview();
    }
}
