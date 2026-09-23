using System;
using System.Collections.Generic;
using UnityEngine;

namespace HMProtection.Entities
{
    public sealed class EntityScope : IDisposable
    {
        public string ScopeId { get; }
        public string LevelId { get; }
        public EntityRegistry Registry { get; }
        public SceneBindingSet Bindings { get; private set; }
        public bool IsReady { get; private set; }
        internal bool IsInstalling { get; private set; }
        public bool IsDisposed => Registry.IsDisposed;
        public EntityScope(string levelId)
        {
            LevelId = levelId ?? string.Empty; ScopeId = Guid.NewGuid().ToString("N"); Registry = new EntityRegistry(ScopeId); Registry.Scope = this;
        }
        public bool TryInitialize(SceneBindingSet bindingSet, IReadOnlyList<Transform> roots, out EntityError error) => TryInitialize(bindingSet, roots, null, out error);
        public bool TryInitialize(SceneBindingSet bindingSet, IReadOnlyList<Transform> roots, IReadOnlyList<EntityHitProxy> hitProxies, out EntityError error)
        {
            if (IsDisposed) { error = EntityError.Create(EntityErrorCode.ScopeDisposed, "Initialize", "Scope is disposed."); return false; }
            if (IsInstalling) { error = EntityError.Create(EntityErrorCode.Busy, "Initialize", "Scope installation is already in progress."); return false; }
            if (IsReady) { error = EntityError.Create(EntityErrorCode.OperationRejected, "Initialize", "Scope is already ready."); return false; }
            IsInstalling = true;
            try
            {
                Bindings = bindingSet;
                if (!SceneEntityInstaller.Collect(roots, out var entities, out error) || !Registry.TryRegisterInitialBatch(entities, () => ValidateBeforeAttach(hitProxies), () => IsReady = true, out _, out error)) { Dispose(); return false; }
                error = default; return true;
            }
            finally { IsInstalling = false; }
        }
        private EntityError ValidateBeforeAttach(IReadOnlyList<EntityHitProxy> hitProxies)
        {
            if (Bindings != null && !Bindings.Validate(this, out var error)) return error;
            foreach (var proxy in hitProxies ?? new EntityHitProxy[0]) if (!Registry.TryRegisterHitInternal(proxy, out error)) return error;
            return default;
        }
        public void Dispose() { IsReady = false; Bindings = null; if (IsDisposed) return; Registry.Dispose(); }
    }
}
