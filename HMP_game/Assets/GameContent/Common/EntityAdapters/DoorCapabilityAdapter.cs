using HMProtection.Entities;
using HMProtection.Modules.Interaction;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    [DisallowMultipleComponent]
    public sealed class DoorCapabilityAdapter : LegacyInteractionCapability, IDoorCapability
    {
        [SerializeField] private DoorController door;
        public bool IsOpen => door != null && door.IsOpen;
        public bool IsAnimating => door != null && door.IsAnimating;
        public void Configure(DoorController value, EntityInteractionSource source) { door = value; base.Configure(value, source); }
        public override bool Validate(GameEntity owner, out EntityError error)
        {
            if (door == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "door.validate", "DoorController is required.", owner.Id, Key); return false; }
            return base.Validate(owner, out error);
        }
        public bool TrySetOpen(EntityHandle actorHandle, bool open, out string error)
        {
            if (!TryActor(actorHandle, out var actor, out error)) return false;
            if (!door.TrySetOpen(actor, open)) { error = "Door is currently unavailable."; return false; }
            error = null;
            return true;
        }
    }
}
