using System;
using System.Collections.Generic;
using UnityEngine;

namespace HMProtection.Sessions
{
    public enum ClockDomain { Simulation, Presentation }

    public sealed class LevelClock
    {
        public float SimulationTime { get; private set; }
        public float PresentationTime { get; private set; }
        public static bool IsValidDelta(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
        public void Tick(float simulationDelta, float presentationDelta)
        {
            if (!IsValidDelta(simulationDelta)) throw new ArgumentOutOfRangeException(nameof(simulationDelta));
            if (!IsValidDelta(presentationDelta)) throw new ArgumentOutOfRangeException(nameof(presentationDelta));
            SimulationTime += simulationDelta;
            PresentationTime += presentationDelta;
        }
    }

    public readonly struct TimerHandle : IEquatable<TimerHandle>
    {
        public int Id { get; }
        public int Generation { get; }
        public bool IsValid => Id > 0 && Generation > 0;
        internal TimerHandle(int id, int generation) { Id = id; Generation = generation; }
        public bool Equals(TimerHandle other) => Id == other.Id && Generation == other.Generation;
        public override bool Equals(object obj) => obj is TimerHandle other && Equals(other);
        public override int GetHashCode() => (Id * 397) ^ Generation;
    }

    public sealed class SessionTimerService : IDisposable
    {
        private sealed class Timer
        {
            public int Generation;
            public float Remaining;
            public ClockDomain Domain;
            public Action<TimerHandle> Callback;
            public bool Active;
            public bool Paused;
            public bool PendingCallback;
        }

        private readonly Dictionary<int, Timer> timers = new Dictionary<int, Timer>();
        private int nextId;
        private bool disposed;
        public bool IsDisposed => disposed;

        public bool Start(float duration, ClockDomain domain, Action<TimerHandle> onElapsed, out TimerHandle handle, out string error)
        {
            if (disposed) { handle = default; error = "Timer service is disposed."; return false; }
            if (!LevelClock.IsValidDelta(duration)) { handle = default; error = "Duration must be finite and non-negative."; return false; }
            if (onElapsed == null) { handle = default; error = "Timer callback is required."; return false; }
            var id = ++nextId;
            var timer = new Timer { Generation = 1, Remaining = duration, Domain = domain, Callback = onElapsed, Active = true };
            timers[id] = timer;
            handle = new TimerHandle(id, timer.Generation);
            error = string.Empty;
            return true;
        }

        public bool Cancel(TimerHandle handle)
        {
            if (disposed || !TryGet(handle, out var timer) || (!timer.Active && !timer.PendingCallback)) return false;
            timer.Active = false;
            timer.PendingCallback = false;
            timers.Remove(handle.Id);
            return true;
        }

        public bool Restart(TimerHandle handle, float duration, out TimerHandle restarted, out string error)
        {
            if (disposed) { restarted = default; error = "Timer service is disposed."; return false; }
            if (!LevelClock.IsValidDelta(duration)) { restarted = default; error = "Duration must be finite and non-negative."; return false; }
            if (!TryGetActive(handle, out var timer)) { restarted = default; error = "Timer handle is stale, elapsed, or cancelled."; return false; }
            timer.Generation++;
            timer.Remaining = duration;
            timer.Active = true;
            timer.Paused = false;
            timer.PendingCallback = false;
            restarted = new TimerHandle(handle.Id, timer.Generation);
            error = string.Empty;
            return true;
        }

        public bool Pause(TimerHandle handle) { if (!TryGetActive(handle, out var timer)) return false; timer.Paused = true; return true; }
        public bool Resume(TimerHandle handle) { if (!TryGetActive(handle, out var timer)) return false; timer.Paused = false; return true; }
        public bool IsActive(TimerHandle handle) => TryGetActive(handle, out _);
        public bool TryGetRemaining(TimerHandle handle, out float remaining) { if (!TryGetActive(handle, out var timer)) { remaining = 0f; return false; } remaining = timer.Remaining; return true; }

        /// <summary>Advances this service once. A LevelSession calls this for session-owned timers;
        /// standalone legacy adapters may call it with their own clock deltas.</summary>
        public void Tick(float simulationDelta, float presentationDelta)
        {
            if (disposed) return;
            if (!LevelClock.IsValidDelta(simulationDelta)) throw new ArgumentOutOfRangeException(nameof(simulationDelta));
            if (!LevelClock.IsValidDelta(presentationDelta)) throw new ArgumentOutOfRangeException(nameof(presentationDelta));
            var elapsed = new List<TimerHandle>();
            foreach (var pair in timers)
            {
                var timer = pair.Value;
                if (!timer.Active || timer.Paused) continue;
                timer.Remaining -= timer.Domain == ClockDomain.Simulation ? simulationDelta : presentationDelta;
                if (timer.Remaining <= 0f)
                {
                    timer.Active = false;
                    timer.PendingCallback = true;
                    elapsed.Add(new TimerHandle(pair.Key, timer.Generation));
                }
            }
            foreach (var handle in elapsed)
            {
                if (!TryGet(handle, out var timer) || !timer.PendingCallback) continue;
                var callback = timer.Callback;
                timer.PendingCallback = false;
                timers.Remove(handle.Id);
                try { callback?.Invoke(handle); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        internal void CancelAll()
        {
            timers.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CancelAll();
        }

        private bool TryGet(TimerHandle handle, out Timer timer)
        {
            timer = null;
            return handle.IsValid && timers.TryGetValue(handle.Id, out timer) && timer.Generation == handle.Generation;
        }
        private bool TryGetActive(TimerHandle handle, out Timer timer)
        {
            if (!TryGet(handle, out timer)) return false;
            return timer.Active;
        }
    }
}
