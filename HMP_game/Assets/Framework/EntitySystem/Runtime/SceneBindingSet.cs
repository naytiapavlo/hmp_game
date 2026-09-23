using System;
using System.Collections.Generic;
using UnityEngine;

namespace HMProtection.Entities
{
    [Serializable]
    public sealed class SceneEntityBinding
    {
        [SerializeField] private string role;
        [SerializeField] private string entityId;
        [SerializeField] private bool required = true;
        [SerializeField] private string capabilityKey;
        [SerializeField] private string anchorSlot;
        public string Role => role;
        public EntityId EntityId => new EntityId(entityId);
        public bool Required => required;
        public string CapabilityKey => capabilityKey;
        public string AnchorSlot => anchorSlot;
        public SceneEntityBinding() { }
        public SceneEntityBinding(string role, string entityId, bool required = true, string capabilityKey = null, string anchorSlot = null) { Configure(role, entityId, required, capabilityKey, anchorSlot); }
        public void Configure(string newRole, string newEntityId, bool isRequired = true, string newCapabilityKey = null, string newAnchorSlot = null) { role = newRole; entityId = newEntityId; required = isRequired; capabilityKey = newCapabilityKey; anchorSlot = newAnchorSlot; }
    }

    [DisallowMultipleComponent]
    public sealed class SceneBindingSet : MonoBehaviour
    {
        [SerializeField] private List<SceneEntityBinding> bindings = new List<SceneEntityBinding>();
        public IReadOnlyList<SceneEntityBinding> Bindings => bindings;
        public void Configure(IEnumerable<SceneEntityBinding> items) => bindings = items == null ? new List<SceneEntityBinding>() : new List<SceneEntityBinding>(items);
        public bool TryResolveRole(string role, EntityScope scope, out EntityHandle handle, out EntityError error)
        {
            handle = default;
            if (scope == null || !scope.IsReady) { error = EntityError.Create(EntityErrorCode.ScopeNotReady, "ResolveRole", "Scope is not ready."); return false; }
            var binding = bindings.Find(x => x != null && string.Equals(x.Role, role, StringComparison.Ordinal));
            if (binding == null) { error = EntityError.Create(EntityErrorCode.MissingBinding, "ResolveRole", "Role is not bound."); return false; }
            return scope.Registry.TryResolve(binding.EntityId, out handle, out error);
        }
        public bool Validate(EntityScope scope, out EntityError error)
        {
            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                if (binding == null || string.IsNullOrWhiteSpace(binding.Role)) { error = EntityError.Create(EntityErrorCode.MissingBinding, "ValidateBindings", "Binding role is missing."); return false; }
                if (!roles.Add(binding.Role)) { error = EntityError.Create(EntityErrorCode.MissingBinding, "ValidateBindings", "Binding role is duplicated."); return false; }
                if (!scope.Registry.TryResolveInternal(binding.EntityId, out var handle, out var resolveError)) { if (binding.Required) { error = resolveError; return false; } continue; }
                if (!string.IsNullOrWhiteSpace(binding.CapabilityKey) && !scope.Registry.HasCapability(handle, binding.CapabilityKey)) { error = EntityError.Create(EntityErrorCode.MissingCapability, "ValidateBindings", "Required capability is missing.", binding.EntityId, binding.CapabilityKey); return false; }
                if (!string.IsNullOrWhiteSpace(binding.AnchorSlot))
                {
                    if (!scope.Registry.TryGetCapabilityInternal<IAnchorCapability>(handle, out var anchor, out error) || !anchor.HasSlot(binding.AnchorSlot)) { error = EntityError.Create(EntityErrorCode.MissingCapability, "ValidateBindings", "Required anchor slot is missing.", binding.EntityId, "anchor"); return false; }
                }
            }
            error = default; return true;
        }
    }
}
