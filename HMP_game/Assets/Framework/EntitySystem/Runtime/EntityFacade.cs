using UnityEngine;

namespace HMProtection.Entities
{
    public sealed class EntityFacade
    {
        private readonly EntityScope scope;
        public EntityFacade(EntityScope scope) { this.scope = scope; }
        public bool TryGet(string entityId, out EntityHandle handle, out EntityError error)
        {
            handle = default;
            if (!CheckReady(out error)) return false;
            return scope.Registry.TryResolve(new EntityId(entityId), out handle, out error);
        }
        public bool HasTag(EntityHandle handle, string tag, out bool result, out EntityError error)
        {
            result = false; if (!CheckReady(out error)) return false;
            if (!scope.Registry.TryGetEntity(handle, out var entity, out error)) return false; foreach (var value in entity.Tags) if (value == tag) { result = true; break; } error = default; return true;
        }
        public bool TrySetActive(EntityHandle handle, bool active, out EntityError error)
        {
            if (!CheckReady(out error) || !scope.Registry.TryGetEntity(handle, out var entity, out error)) return false;
            var target = entity.ActivationTarget; if (target == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "SetActive", "Activation target is missing.", handle.Id); return false; }
            target.SetActive(active); error = default; return true;
        }
        public bool TryGetActive(EntityHandle handle, out bool activeSelf, out bool activeInHierarchy, out EntityError error)
        {
            activeSelf = false; activeInHierarchy = false;
            if (!CheckReady(out error) || !scope.Registry.TryGetEntity(handle, out var entity, out error)) return false;
            var target = entity.ActivationTarget;
            if (target == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "GetActive", "Activation target is missing.", handle.Id); return false; }
            activeSelf = target.activeSelf; activeInHierarchy = target.activeInHierarchy; error = default; return true;
        }
        public bool TryGetAnchorPose(EntityHandle handle, string slot, out EntityPose pose, out EntityError error)
        { pose = default; return CheckReady(out error) && scope.Registry.TryGetCapability<IAnchorCapability>(handle, out var anchor, out error) && anchor.TryGetPose(slot, out pose, out error); }
        private bool CheckReady(out EntityError error) { if (scope == null || scope.IsDisposed) { error = EntityError.Create(EntityErrorCode.ScopeDisposed, "Facade", "Scope is disposed."); return false; } if (!scope.IsReady) { error = EntityError.Create(EntityErrorCode.ScopeNotReady, "Facade", "Scope is not ready."); return false; } error = default; return true; }
    }
}
