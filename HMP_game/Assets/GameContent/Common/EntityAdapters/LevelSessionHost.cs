using System;
using HMProtection.Entities;
using HMProtection.Sessions;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    /// <summary>Unity tick/lifetime boundary. References are explicitly supplied by the scene installer.</summary>
    [DefaultExecutionOrder(-500)]
    public sealed class LevelSessionHost : MonoBehaviour
    {
        public LevelSession Session { get; private set; }
        public HMProtection.Scripting.ComponentScriptService Scripts { get; private set; }
        public HMProtection.Scripting.LevelLuaRunner Lua { get; private set; }
        public bool IsRunning => Session != null && Session.State == LevelSessionState.Running;
        public bool IsBlocked(ControlMask mask) => !IsRunning || Session.Controls.IsBlocked(mask);
        public event Action Stopping;
        public bool Bind(EntityScope scope, out string error)
        {
            if (Session != null && Session.Entities == scope && IsRunning) { error = null; return true; }
            Release();
            Session = new LevelSession(scope);
            if (Session.Start(out error))
            {
                Scripts = new HMProtection.Scripting.ComponentScriptService(Session, GetComponent<LevelSceneBindings>());
                return true;
            }
            Session.Dispose(); Session = null; return false;
        }
        public bool TryStartLua(LevelDefinition definition, out string error)
        {
            Lua?.Dispose(); Lua = null; error = null;
            if (definition == null || !definition.scriptEnabled) return true;
            if (!HMProtection.Scripting.LevelLuaRunner.TryCreate(this, definition, out var runner, out error)) return false;
            Lua = runner; return true;
        }
        public IDisposable Acquire(object owner, ControlMask mask) => IsRunning ? Session.Controls.Acquire(owner, mask) : null;
        void Update()
        {
            Scripts?.Tick();
            if (IsRunning && !Session.Tick(Time.deltaTime, Time.unscaledDeltaTime, out var error))
                Debug.LogError("[Session] " + error, this);
        }
        void LateUpdate()
        {
            if (Lua != null && Lua.IsRunning && !Lua.Tick(IsBlocked(ControlMask.Simulation) ? 0f : Time.deltaTime, Time.unscaledDeltaTime, out var error))
                Debug.LogError("[Level Lua] " + error, this);
        }
        public void Release()
        {
            if (Session == null) return;
            Lua?.Dispose(); Lua = null;
            var previous = Session;
            Session = null; // Reentrant commands are rejected before cancellation callbacks.
            Scripts?.Dispose(); Scripts = null;
            if (Stopping != null)
                foreach (Action callback in Stopping.GetInvocationList())
                    try { callback(); } catch (Exception exception) { Debug.LogException(exception, this); }
            Stopping = null;
            previous.Dispose();
        }
        void OnDestroy() => Release();
    }
}
