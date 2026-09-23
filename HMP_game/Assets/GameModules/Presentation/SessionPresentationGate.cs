using System;
using HMProtection.EntityAdapters;
using HMProtection.Sessions;
using UnityEngine;

namespace HMProtection.Presentation
{
    /// <summary>Explicit modal ownership. Views use this when supplied and retain their legacy behaviour otherwise.</summary>
    [DisallowMultipleComponent]
    public sealed class SessionPresentationGate : MonoBehaviour
    {
        [SerializeField] private LevelSessionHost host;
        [SerializeField] private body player;
        [SerializeField] private InteractionHUD hud;
        LevelSessionHost subscribedHost;
        int count;
        int epoch;
        bool savedHud, savedCursor;
        CursorLockMode savedLock;
        void Awake() => Subscribe(host);
        void OnDestroy() { Subscribe(null); ForceRelease(); }
        public void Configure(LevelSessionHost value, body playerBody, InteractionHUD interactionHud) { Subscribe(value); player = playerBody; hud = interactionHud; }
        void Subscribe(LevelSessionHost value)
        {
            if (subscribedHost != null) subscribedHost.Stopping -= ForceRelease;
            host = value;
            subscribedHost = host;
            if (subscribedHost != null) subscribedHost.Stopping += ForceRelease;
        }
        public bool TryAcquireModal(object owner, out IDisposable lease)
        {
            lease = null;
            if (host == null || !host.IsRunning) return false;
            if (count++ == 0)
            {
                savedLock = Cursor.lockState; savedCursor = Cursor.visible;
                if (hud != null) { savedHud = hud.gameObject.activeSelf; hud.gameObject.SetActive(false); }
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            }
            var controls = host.Acquire(owner, ControlMask.Simulation | ControlMask.Movement | ControlMask.Look | ControlMask.Interaction | ControlMask.ToolUse);
            lease = new ModalLease(this, controls, epoch);
            return true;
        }
        void Release(IDisposable controls, int leaseEpoch)
        {
            controls?.Dispose();
            if (leaseEpoch != epoch) return;
            if (count <= 0 || --count != 0) return;
            if (hud != null) hud.gameObject.SetActive(savedHud);
            Cursor.lockState = savedLock; Cursor.visible = savedCursor;
        }
        void ForceRelease()
        {
            if (count <= 0) return;
            count = 0; epoch++;
            if (hud != null) hud.gameObject.SetActive(savedHud);
            Cursor.lockState = savedLock; Cursor.visible = savedCursor;
        }
        sealed class ModalLease : IDisposable
        {
            SessionPresentationGate gate; IDisposable controls; readonly int epoch;
            public ModalLease(SessionPresentationGate owner, IDisposable value, int leaseEpoch) { gate = owner; controls = value; epoch = leaseEpoch; }
            public void Dispose() { var current = gate; gate = null; current?.Release(controls, epoch); controls = null; }
        }
    }
}
