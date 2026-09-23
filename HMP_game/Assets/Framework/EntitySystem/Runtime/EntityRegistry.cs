using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HMProtection.Entities
{
    public sealed class EntityRegistry : IDisposable
    {
        private sealed class Entry { public GameEntity Entity; public EntityHandle Handle; public Dictionary<Type, object> Interfaces; public HashSet<string> Keys; public List<EntityCapability> Attached = new List<EntityCapability>(); }
        private readonly string scopeId;
        private readonly Dictionary<EntityId, Entry> entries = new Dictionary<EntityId, Entry>();
        private readonly Dictionary<string, SortedSet<EntityId>> tags = new Dictionary<string, SortedSet<EntityId>>(StringComparer.Ordinal);
        private sealed class HitEntry { public EntityHandle Handle; public EntityHitProxy Proxy; }
        private readonly Dictionary<Collider, HitEntry> hits = new Dictionary<Collider, HitEntry>();
        private int nextGeneration;
        private bool disposed, mutating, disposeRequested;
        internal EntityScope Scope { get; set; }
        public event Action<EntityHandle> Registered;
        public event Action<EntityHandle> Unregistered;
        internal EntityRegistry(string scopeId) { this.scopeId = scopeId; }
        public bool IsDisposed => disposed;
        public IReadOnlyList<EntityHandle> GetHandles()
        {
            PurgeDead();
            if (disposed || (Scope != null && !Scope.IsReady)) return Array.Empty<EntityHandle>();
            return entries.Values.OrderBy(entry => entry.Handle.Id.Value, StringComparer.Ordinal).Select(entry => entry.Handle).ToArray();
        }

        public bool TryRegister(GameEntity entity, out EntityHandle handle, out EntityError error)
        {
            if (!TryRegisterBatch(new[] { entity }, out var handles, out error)) { handle = default; return false; }
            handle = handles[0]; return true;
        }
        public bool TryRegisterBatch(IReadOnlyList<GameEntity> entities, out IReadOnlyList<EntityHandle> handles, out EntityError error)
            => TryRegisterBatchCore(entities, null, null, out handles, out error);
        internal bool TryRegisterInitialBatch(IReadOnlyList<GameEntity> entities, Func<EntityError> validateBeforeAttach, Action commitReady, out IReadOnlyList<EntityHandle> handles, out EntityError error)
            => TryRegisterBatchCore(entities, validateBeforeAttach, commitReady, out handles, out error);
        private bool TryRegisterBatchCore(IReadOnlyList<GameEntity> entities, Func<EntityError> validateBeforeAttach, Action commitReady, out IReadOnlyList<EntityHandle> handles, out EntityError error)
        {
            handles = Array.Empty<EntityHandle>();
            if (!Begin("Register", out error)) return false;
            var additions = new List<Entry>();
            try
            {
                var batchIds = new HashSet<EntityId>();
                foreach (var entity in entities ?? Array.Empty<GameEntity>())
                {
                    if (!ValidateEntity(entity, batchIds, out error)) return false;
                }
                foreach (var entity in entities ?? Array.Empty<GameEntity>())
                {
                    var handle = new EntityHandle(scopeId, entity.Id, ++nextGeneration);
                    if (!entity.TryClaim(this, handle, out error)) { Rollback(additions); return false; }
                    additions.Add(BuildEntry(entity, handle));
                }
                foreach (var entry in additions) AddIndexes(entry);
                if (validateBeforeAttach != null) { error = validateBeforeAttach(); if (!error.IsNone) { Rollback(additions); return false; } }
                foreach (var entry in additions) if (!Attach(entry, false, out error)) { Rollback(additions); return false; }
                foreach (var entry in additions) if (!Attach(entry, true, out error)) { Rollback(additions); return false; }
                handles = additions.Select(x => x.Handle).ToArray();
                commitReady?.Invoke();
                Notify(Registered, handles);
                if (disposeRequested) { error = EntityError.Create(EntityErrorCode.ScopeDisposed, "Register", "Scope was disposed during registration."); return false; }
                return true;
            }
            catch (Exception exception) { Rollback(additions); error = EntityError.Create(EntityErrorCode.OperationRejected, "Register", exception.Message); return false; }
            finally { End(); }
        }
        public bool TryResolve(EntityId id, out EntityHandle handle, out EntityError error)
        {
            PurgeDead();
            if (disposed) { handle = default; error = EntityError.Create(EntityErrorCode.ScopeDisposed, "Resolve", "Scope is disposed.", id); return false; }
            if (Scope != null && !Scope.IsReady) { handle = default; error = EntityError.Create(EntityErrorCode.ScopeNotReady, "Resolve", "Scope is not ready.", id); return false; }
            return TryResolveInternal(id, out handle, out error);
        }
        internal bool TryResolveInternal(EntityId id, out EntityHandle handle, out EntityError error) { if (entries.TryGetValue(id, out var entry)) { handle = entry.Handle; error = default; return true; } handle = default; error = EntityError.Create(EntityErrorCode.EntityNotFound, "Resolve", "Entity was not found.", id); return false; }
        public bool IsValid(EntityHandle handle) => !disposed && (Scope == null || Scope.IsReady) && handle.ScopeId == scopeId && entries.TryGetValue(handle.Id, out var entry) && entry.Handle.Equals(handle) && entry.Entity != null;
        public bool TryGetCapability<T>(EntityHandle handle, out T capability, out EntityError error) where T : class
        {
            PurgeDead(); capability = null;
            if (disposed) { error = EntityError.Create(EntityErrorCode.ScopeDisposed, "GetCapability", "Scope is disposed.", handle.Id); return false; }
            if (Scope != null && !Scope.IsReady) { error = EntityError.Create(EntityErrorCode.ScopeNotReady, "GetCapability", "Scope is not ready.", handle.Id); return false; }
            return TryGetCapabilityInternal(handle, out capability, out error);
        }
        internal bool TryGetCapabilityInternal<T>(EntityHandle handle, out T capability, out EntityError error) where T : class
        {
            capability = null; if (!TryEntry(handle, "GetCapability", out var entry, out error)) return false;
            if (!entry.Interfaces.TryGetValue(typeof(T), out var raw) || raw == null) { error = EntityError.Create(EntityErrorCode.MissingCapability, "GetCapability", "Capability is missing.", handle.Id, typeof(T).Name); return false; }
            if (raw is UnityEngine.Object unity && unity == null) { error = EntityError.Create(EntityErrorCode.CapabilityNotReady, "GetCapability", "Capability Unity object was destroyed.", handle.Id, typeof(T).Name); return false; }
            capability = (T)raw; error = default; return true;
        }
        public bool TryGetEntity(EntityHandle handle, out GameEntity entity, out EntityError error)
        {
            entity = null; PurgeDead();
            if (disposed) { error = EntityError.Create(EntityErrorCode.ScopeDisposed, "GetEntity", "Scope is disposed.", handle.Id); return false; }
            if (Scope != null && !Scope.IsReady) { error = EntityError.Create(EntityErrorCode.ScopeNotReady, "GetEntity", "Scope is not ready.", handle.Id); return false; }
            if (!TryEntry(handle, "GetEntity", out var entry, out error)) return false;
            entity = entry.Entity; error = default; return true;
        }
        public IReadOnlyList<EntityHandle> FindByTag(string tag, bool activeOnly = false)
        {
            PurgeDead(); if (disposed || (Scope != null && !Scope.IsReady) || string.IsNullOrWhiteSpace(tag) || !tags.TryGetValue(tag, out var ids)) return Array.Empty<EntityHandle>();
            return ids.Where(id => entries.TryGetValue(id, out var e) && (!activeOnly || (e.Entity.ActivationTarget != null && e.Entity.ActivationTarget.activeInHierarchy))).Select(id => entries[id].Handle).ToArray();
        }
        public bool TryResolveHit(Collider collider, out EntityHandle handle, out EntityError error)
        {
            PurgeDead();
            if (disposed) { handle = default; error = EntityError.Create(EntityErrorCode.ScopeDisposed, "ResolveHit", "Scope is disposed."); return false; }
            if (Scope != null && !Scope.IsReady) { handle = default; error = EntityError.Create(EntityErrorCode.ScopeNotReady, "ResolveHit", "Scope is not ready."); return false; }
            if (collider != null && hits.TryGetValue(collider, out var hit) && hit.Proxy != null && IsValid(hit.Handle)) { handle = hit.Handle; error = default; return true; }
            handle = default; error = EntityError.Create(EntityErrorCode.EntityNotFound, "ResolveHit", "Collider is not managed by this scope."); return false;
        }
        public bool IsManagedCollider(Collider collider) { PurgeDead(); return collider != null && hits.TryGetValue(collider, out var hit) && hit.Proxy != null; }
        public bool TryRegisterHit(EntityHitProxy proxy, out EntityError error) { if (!Begin("RegisterHit", out error)) return false; try { return TryRegisterHitInternal(proxy, out error); } finally { End(); } }
        internal bool TryRegisterHitInternal(EntityHitProxy proxy, out EntityError error)
        {
            if (proxy == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "RegisterHit", "Hit proxy is missing."); return false; }
            if (proxy.Target == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "RegisterHit", "Hit proxy target is missing."); return false; }
            if (!TryResolveInternal(proxy.Target.Id, out var handle, out error)) return false;
            if (!entries.TryGetValue(handle.Id, out var targetEntry) || targetEntry.Entity != proxy.Target) { error = EntityError.Create(EntityErrorCode.InvalidOwner, "RegisterHit", "Hit proxy target is not the registered entity instance.", proxy.Target.Id); return false; }
            var proxyColliders = new HashSet<Collider>();
            foreach (var collider in proxy.Colliders)
            {
                if (collider == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "RegisterHit", "Hit proxy collider is missing.", handle.Id); return false; }
                if (!proxyColliders.Add(collider)) { error = EntityError.Create(EntityErrorCode.InvalidOwner, "RegisterHit", "Collider is duplicated inside one hit proxy.", handle.Id); return false; }
                if (hits.ContainsKey(collider)) { error = EntityError.Create(EntityErrorCode.InvalidOwner, "RegisterHit", "Collider already belongs to a hit proxy.", handle.Id); return false; }
            }
            foreach (var collider in proxy.Colliders) hits[collider] = new HitEntry { Handle = handle, Proxy = proxy };
            proxy.AttachRegistry(this);
            error = default; return true;
        }
        public bool TryUnregister(EntityHandle handle, out EntityError error)
        {
            if (!Begin("Unregister", out error)) return false;
            try { if (!TryEntry(handle, "Unregister", out var entry, out error)) return false; Remove(entry, true); error = default; return true; }
            finally { End(); }
        }
        internal void NotifyDestroyed(GameEntity entity, EntityHandle handle) { if (disposed || mutating || entity == null || !IsValid(handle)) return; TryUnregister(handle, out _); }
        internal void NotifyProxyDestroyed(EntityHitProxy proxy) { if (disposed || proxy == null) return; foreach (var pair in hits.Where(x => x.Key == null || x.Value.Proxy == proxy).ToArray()) hits.Remove(pair.Key); }
        public void Dispose()
        {
            if (disposed) return; if (mutating) { disposeRequested = true; return; }
            mutating = true;
            try { disposed = true; foreach (var entry in entries.Values.OrderByDescending(x => x.Handle.Generation).ToArray()) Remove(entry, true); hits.Clear(); tags.Clear(); entries.Clear(); Registered = null; Unregistered = null; }
            finally { mutating = false; }
        }
        private bool Begin(string operation, out EntityError error) { if (disposed) { error = EntityError.Create(EntityErrorCode.ScopeDisposed, operation, "Scope is disposed."); return false; } if (mutating) { error = EntityError.Create(EntityErrorCode.Busy, operation, "Registry is dispatching a change."); return false; } mutating = true; error = default; return true; }
        private void End() { mutating = false; if (disposeRequested) { disposeRequested = false; Dispose(); } }
        private bool ValidateEntity(GameEntity entity, HashSet<EntityId> batchIds, out EntityError error)
        {
            if (entity == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "Register", "GameEntity is missing."); return false; }
            if (entity.Id.IsEmpty || !IsValidId(entity.Id.Value)) { error = EntityError.Create(EntityErrorCode.InvalidId, "Register", "Entity ID has invalid characters.", entity.Id); return false; }
            if (entries.ContainsKey(entity.Id) || !batchIds.Add(entity.Id)) { error = EntityError.Create(EntityErrorCode.DuplicateId, "Register", "Entity ID is duplicated.", entity.Id); return false; }
            if (entity.ActivationTarget == null || !(entity.ActivationTarget.transform == entity.transform || entity.ActivationTarget.transform.IsChildOf(entity.transform))) { error = EntityError.Create(EntityErrorCode.InvalidOwner, "Register", "Activation target must be the entity or its child.", entity.Id); return false; }
            var seenTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in entity.Tags) if (string.IsNullOrWhiteSpace(tag) || !seenTags.Add(tag)) { error = EntityError.Create(EntityErrorCode.DuplicateCapability, "Register", "Entity tag is empty or duplicated.", entity.Id, tag); return false; }
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var seenInterfaces = new HashSet<Type>();
            foreach (var cap in entity.Capabilities)
            {
                if (cap == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "Register", "Capability reference is missing.", entity.Id); return false; }
                if (string.IsNullOrWhiteSpace(cap.Key)) { error = EntityError.Create(EntityErrorCode.InvalidId, "Register", "Capability key is empty.", entity.Id); return false; }
                if (!seenKeys.Add(cap.Key)) { error = EntityError.Create(EntityErrorCode.DuplicateCapability, "Register", "Capability key is duplicated.", entity.Id, cap.Key); return false; }
                foreach (var iface in cap.GetType().GetInterfaces()) if (!seenInterfaces.Add(iface)) { error = EntityError.Create(EntityErrorCode.DuplicateCapability, "Register", "Capability interface is duplicated.", entity.Id, iface.Name); return false; }
                if (!cap.Validate(entity, out error)) return false;
            }
            error = default; return true;
        }
        private Entry BuildEntry(GameEntity entity, EntityHandle handle)
        {
            var entry = new Entry { Entity = entity, Handle = handle, Interfaces = new Dictionary<Type, object>(), Keys = new HashSet<string>(StringComparer.Ordinal) };
            foreach (var cap in entity.Capabilities)
            {
                entry.Keys.Add(cap.Key);
                foreach (var iface in cap.GetType().GetInterfaces()) entry.Interfaces.Add(iface, cap);
            }
            return entry;
        }
        private void AddIndexes(Entry entry)
        {
            entries.Add(entry.Handle.Id, entry);
            foreach (var tag in entry.Entity.Tags.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal)) { if (!tags.TryGetValue(tag, out var ids)) tags[tag] = ids = new SortedSet<EntityId>(); ids.Add(entry.Handle.Id); }
        }
        private bool Attach(Entry entry, bool prepare, out EntityError error)
        {
            var context = new EntityCapabilityContext(Scope, entry.Entity, entry.Handle);
            if (!prepare) foreach (var cap in entry.Entity.Capabilities) { entry.Attached.Add(cap); if (!cap.Attach(context, out error)) return false; }
            if (prepare) foreach (var cap in entry.Attached) if (!cap.Prepare(out error)) return false;
            error = default; return true;
        }
        private void Rollback(List<Entry> additions) { foreach (var entry in additions.AsEnumerable().Reverse()) Remove(entry, false); }
        private void Remove(Entry entry, bool notify)
        {
            entries.Remove(entry.Handle.Id); foreach (var set in tags.Values) set.Remove(entry.Handle.Id); foreach (var pair in hits.Where(x => x.Value.Handle.Equals(entry.Handle)).ToArray()) { pair.Value.Proxy?.DetachRegistry(this); hits.Remove(pair.Key); }
            foreach (var cap in entry.Attached.AsEnumerable().Reverse()) if (cap != null) { try { cap.Detach(); } catch (Exception exception) { Debug.LogException(exception); } } entry.Entity?.Release(this); if (notify) Notify(Unregistered, new[] { entry.Handle });
        }
        private void Notify(Action<EntityHandle> callback, IEnumerable<EntityHandle> handles) { if (callback == null) return; foreach (var handle in handles) foreach (Action<EntityHandle> listener in callback.GetInvocationList()) try { listener(handle); } catch (Exception exception) { Debug.LogException(exception); } }
        private bool TryEntry(EntityHandle handle, string operation, out Entry entry, out EntityError error)
        { entry = null; if (!handle.IsValid || handle.ScopeId != scopeId || !entries.TryGetValue(handle.Id, out entry) || !entry.Handle.Equals(handle) || entry.Entity == null) { error = EntityError.Create(EntityErrorCode.InvalidHandle, operation, "Entity handle is invalid.", handle.Id); return false; } error = default; return true; }
        private void PurgeDead()
        {
            if (disposed || mutating) return;
            foreach (var pair in hits.Where(x => x.Key == null || x.Value.Proxy == null || !IsValidInternal(x.Value.Handle)).ToArray()) hits.Remove(pair.Key);
            foreach (var entry in entries.Values.Where(x => x.Entity == null).ToArray()) { mutating = true; try { Remove(entry, true); } finally { mutating = false; } }
        }
        private bool IsValidInternal(EntityHandle handle) => !disposed && handle.ScopeId == scopeId && entries.TryGetValue(handle.Id, out var entry) && entry.Handle.Equals(handle) && entry.Entity != null;
        internal bool HasCapability(EntityHandle handle, string key) => TryEntry(handle, "HasCapability", out var entry, out _) && entry.Keys.Contains(key);
        private static bool IsValidId(string value) { if (string.IsNullOrWhiteSpace(value)) return false; foreach (var c in value) if (!(char.IsLower(c) || char.IsDigit(c) || c == '.' || c == '_' || c == '-')) return false; return true; }
    }
}
