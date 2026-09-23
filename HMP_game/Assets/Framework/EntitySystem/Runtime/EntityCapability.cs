using UnityEngine;

namespace HMProtection.Entities
{
    public sealed class EntityCapabilityContext
    {
        public EntityScope Scope { get; }
        public GameEntity Entity { get; }
        public EntityHandle Handle { get; }
        internal EntityCapabilityContext(EntityScope scope, GameEntity entity, EntityHandle handle) { Scope = scope; Entity = entity; Handle = handle; }
        public bool TryResolve(EntityId id, out EntityHandle resolved, out EntityError error)
        {
            resolved = default;
            if (Scope == null) { error = EntityError.Create(EntityErrorCode.ScopeDisposed, "Resolve", "Scope is unavailable.", id); return false; }
            return Scope.Registry.TryResolveInternal(id, out resolved, out error);
        }
        public bool TryGetCapability<T>(EntityHandle target, out T capability, out EntityError error) where T : class
        {
            capability = null;
            if (Scope == null) { error = EntityError.Create(EntityErrorCode.ScopeDisposed, "GetCapability", "Scope is unavailable.", target.Id); return false; }
            return Scope.Registry.TryGetCapabilityInternal(target, out capability, out error);
        }
    }

    public abstract class EntityCapability : MonoBehaviour
    {
        internal GameEntity Owner { get; set; }
        public abstract string Key { get; }
        public virtual bool Validate(GameEntity owner, out EntityError error) { error = default; return true; }
        public virtual bool Attach(EntityCapabilityContext context, out EntityError error) { error = default; return true; }
        public virtual bool Prepare(out EntityError error) { error = default; return true; }
        public virtual void Detach() { }
    }

    public interface IAnchorCapability
    {
        bool HasSlot(string slot);
        bool TryGetPose(string slot, out EntityPose pose, out EntityError error);
    }
}
