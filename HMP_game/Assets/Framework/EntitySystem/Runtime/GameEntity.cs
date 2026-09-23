using System;
using System.Collections.Generic;
using UnityEngine;

namespace HMProtection.Entities
{
    [DisallowMultipleComponent]
    public sealed class GameEntity : MonoBehaviour
    {
        [SerializeField] private string entityId;
        [SerializeField] private List<string> tags = new List<string>();
        [SerializeField] private GameObject activationTarget;
        [SerializeField] private List<EntityCapability> capabilities = new List<EntityCapability>();
        private EntityRegistry registry;
        private EntityHandle handle;

        public EntityId Id => new EntityId(entityId);
        public IReadOnlyList<string> Tags => tags;
        public GameObject ActivationTarget => activationTarget == null ? gameObject : activationTarget;
        public IReadOnlyList<EntityCapability> Capabilities => capabilities;
        public EntityHandle Handle => handle;
        public void Configure(string id, IEnumerable<string> newTags = null, GameObject target = null, IEnumerable<EntityCapability> newCapabilities = null)
        {
            if (handle.IsValid) return;
            entityId = id;
            tags = newTags == null ? new List<string>() : new List<string>(newTags);
            activationTarget = target;
            capabilities = newCapabilities == null ? new List<EntityCapability>() : new List<EntityCapability>(newCapabilities);
        }
        internal bool TryClaim(EntityRegistry newRegistry, EntityHandle newHandle, out EntityError error)
        {
            if (registry != null && registry != newRegistry) { error = EntityError.Create(EntityErrorCode.AlreadyOwned, "Claim", "Entity is owned by another scope.", Id); return false; }
            foreach (var capability in capabilities) if (capability != null && capability.Owner != null && capability.Owner != this) { error = EntityError.Create(EntityErrorCode.AlreadyOwned, "Claim", "Capability belongs to another entity.", Id, capability.Key); return false; }
            registry = newRegistry; handle = newHandle; foreach (var capability in capabilities) if (capability != null) capability.Owner = this; error = default; return true;
        }
        internal void Release(EntityRegistry owner) { if (registry == owner) { foreach (var capability in capabilities) if (capability != null && capability.Owner == this) capability.Owner = null; registry = null; handle = default; } }
        private void OnDestroy() { if (registry != null) registry.NotifyDestroyed(this, handle); }
    }
}
