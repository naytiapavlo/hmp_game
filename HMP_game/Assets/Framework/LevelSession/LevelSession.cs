using System;
using System.Threading;
using HMProtection.Entities;
using UnityEngine;

namespace HMProtection.Sessions
{
    public enum LevelSessionState
    {
        Created,
        Running,
        Stopped,
        Disposed
    }

    /// <summary>Owns the services whose lifetime is one run of a level.</summary>
    public sealed class LevelSession : IDisposable
    {
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();

        public LevelSession(EntityScope entities)
        {
            Entities = entities ?? throw new ArgumentNullException(nameof(entities));
            Events = new ScopeEventBus();
            ScriptEvents = new NamedEventBus();
            Clock = new LevelClock();
            Timers = new SessionTimerService();
            Controls = new ControlLeaseService();
            State = LevelSessionState.Created;
        }

        public EntityScope Entities { get; }
        public LevelSessionState State { get; private set; }
        public ScopeEventBus Events { get; }
        public NamedEventBus ScriptEvents { get; }
        public LevelClock Clock { get; }
        public SessionTimerService Timers { get; }
        public ControlLeaseService Controls { get; }
        public CancellationToken Lifetime => lifetime.Token;

        public bool Start(out string error)
        {
            if (State == LevelSessionState.Disposed) { error = "Session is disposed."; return false; }
            if (State != LevelSessionState.Created) { error = "Session can only start once."; return false; }
            if (Entities.IsDisposed || !Entities.IsReady) { error = "Entity scope must be ready before the session starts."; return false; }
            State = LevelSessionState.Running;
            error = string.Empty;
            return true;
        }

        public bool Tick(float simulationDelta, float presentationDelta, out string error)
        {
            if (State != LevelSessionState.Running) { error = "Session is not running."; return false; }
            if (!LevelClock.IsValidDelta(simulationDelta) || !LevelClock.IsValidDelta(presentationDelta))
            {
                error = "Tick deltas must be finite and non-negative.";
                return false;
            }

            if (Controls.IsBlocked(ControlMask.Simulation)) simulationDelta = 0f;
            Clock.Tick(simulationDelta, presentationDelta);
            Timers.Tick(simulationDelta, presentationDelta);
            Events.Dispatch();
            ScriptEvents.Dispatch();
            error = string.Empty;
            return true;
        }

        public void Stop()
        {
            if (State == LevelSessionState.Stopped || State == LevelSessionState.Disposed) return;
            State = LevelSessionState.Stopped;
            RunCleanup(() => lifetime.Cancel());
            RunCleanup(Timers.Dispose);
            RunCleanup(Events.Dispose);
            RunCleanup(ScriptEvents.Dispose);
            RunCleanup(Controls.Dispose);
            RunCleanup(Entities.Dispose);
        }

        public void Dispose()
        {
            if (State == LevelSessionState.Disposed) return;
            Stop();
            State = LevelSessionState.Disposed;
            lifetime.Dispose();
        }

        private static void RunCleanup(Action action)
        {
            try { action(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
