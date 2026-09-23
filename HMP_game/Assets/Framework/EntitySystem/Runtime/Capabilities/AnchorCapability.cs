using System;
using System.Collections.Generic;
using UnityEngine;

namespace HMProtection.Entities
{
    [Serializable]
    public sealed class AnchorSlot
    {
        [SerializeField] private string slot = "origin";
        [SerializeField] private Transform anchor;
        public string Slot => slot;
        public Transform Anchor => anchor;
        public void Configure(string newSlot, Transform newAnchor) { slot = newSlot; anchor = newAnchor; }
    }

    public sealed class AnchorCapability : EntityCapability, IAnchorCapability
    {
        [SerializeField] private List<AnchorSlot> slots = new List<AnchorSlot>();
        private EntityCapabilityContext context;
        public override string Key => "anchor";
        public void Configure(IEnumerable<AnchorSlot> newSlots) => slots = newSlots == null ? new List<AnchorSlot>() : new List<AnchorSlot>(newSlots);
        public override bool Validate(GameEntity owner, out EntityError error)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in slots)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Slot) || item.Anchor == null || item.Anchor.gameObject.scene != owner.gameObject.scene) { error = EntityError.Create(EntityErrorCode.MissingReference, "ValidateAnchor", "Anchor slot is missing or belongs to another scene.", owner.Id, Key); return false; }
                if (!found.Add(item.Slot)) { error = EntityError.Create(EntityErrorCode.DuplicateCapability, "ValidateAnchor", "Anchor slot is duplicated.", owner.Id, Key); return false; }
            }
            error = default; return true;
        }
        public override bool Attach(EntityCapabilityContext value, out EntityError error) { context = value; error = default; return true; }
        public override void Detach() { context = null; }
        public bool HasSlot(string slot) => Find(slot) != null;
        public bool TryGetPose(string slot, out EntityPose pose, out EntityError error)
        {
            if (context == null || context.Scope == null || context.Scope.IsDisposed) { pose = default; error = EntityError.Create(EntityErrorCode.ScopeDisposed, "GetAnchorPose", "Anchor capability is detached or its scope is disposed.", default, Key); return false; }
            if (!context.Scope.IsReady) { pose = default; error = EntityError.Create(EntityErrorCode.ScopeNotReady, "GetAnchorPose", "Scope is not ready.", context.Handle.Id, Key); return false; }
            if (!context.Scope.Registry.IsValid(context.Handle)) { pose = default; error = EntityError.Create(EntityErrorCode.InvalidHandle, "GetAnchorPose", "Anchor entity handle is invalid.", context.Handle.Id, Key); return false; }
            var item = Find(slot);
            if (item == null || item.Anchor == null) { pose = default; error = EntityError.Create(EntityErrorCode.MissingReference, "GetAnchorPose", "Anchor slot is missing.", default, Key); return false; }
            pose = new EntityPose(item.Anchor.position, item.Anchor.rotation); error = default; return true;
        }
        private AnchorSlot Find(string slot) => slots.Find(x => x != null && string.Equals(x.Slot, slot, StringComparison.Ordinal));
    }
}
