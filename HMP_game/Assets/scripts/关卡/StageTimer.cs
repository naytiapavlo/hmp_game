using System;
using HMProtection.Sessions;
using HMProtection.EntityAdapters;
using UnityEngine;

namespace HMProtection.Core
{
    /// <summary>Compatibility view over one timer owner; hosts alone tick session timers.</summary>
    [DisallowMultipleComponent]
    public sealed class StageTimer : MonoBehaviour
    {
        public LevelSessionHost sessionHost;
        public float Duration { get; private set; }
        public float Remaining { get { Refresh(); return remaining; } }
        public float Elapsed => Mathf.Max(0f, Duration - Remaining);
        public bool IsRunning { get; private set; }
        public bool HasExpired { get; private set; }
        public float Normalized01 => Duration <= 0f ? 0f : Mathf.Clamp01(Remaining / Duration);
        public event Action OnExpired;
        SessionTimerService timers;
        TimerHandle handle;
        bool locallyDriven;
        float remaining;
        public void Begin(float duration)
        {
            Stop();
            if (float.IsNaN(duration) || float.IsInfinity(duration) || duration < 0f)
                throw new ArgumentOutOfRangeException(nameof(duration));
            Duration = remaining = duration;
            locallyDriven = sessionHost == null;
            if (!locallyDriven && !sessionHost.IsRunning) return;
            timers = locallyDriven ? new SessionTimerService() : sessionHost.Session.Timers;
            IsRunning = timers.Start(duration, ClockDomain.Simulation, _ =>
            {
                remaining = 0f; IsRunning = false; HasExpired = true; OnExpired?.Invoke();
            }, out handle, out var error);
            if (!IsRunning) Debug.LogError("[Timer] " + error, this);
        }
        void Update()
        {
            if (locallyDriven && timers != null) timers.Tick(Time.deltaTime, Time.unscaledDeltaTime);
            Refresh();
        }
        void Refresh() { if (timers != null && timers.TryGetRemaining(handle, out var value)) remaining = value; }
        public void Pause() { if (timers != null) timers.Pause(handle); IsRunning = false; }
        public void Resume() { if (HasExpired || timers == null) return; IsRunning = timers.Resume(handle); }
        public void Stop()
        {
            timers?.Cancel(handle); timers = null; handle = default;
            Duration = remaining = 0f; HasExpired = IsRunning = false;
        }
        void OnDestroy() => Stop();
    }
}
