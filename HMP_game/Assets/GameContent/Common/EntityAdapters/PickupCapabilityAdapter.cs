using HMProtection.Entities;
using HMProtection.Modules.Interaction;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    [DisallowMultipleComponent]
    public sealed class PickupCapabilityAdapter : LegacyInteractionCapability, IPickupCapability
    {
        [SerializeField] private PickupItem pickup;
        private Interactor lastActor;
        public bool IsHeld => pickup != null && pickup.IsHeld;
        public void Configure(PickupItem value, EntityInteractionSource source) { pickup = value; base.Configure(value, source); }
        public override bool Validate(GameEntity owner, out EntityError error)
        {
            if (pickup == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "pickup.validate", "PickupItem is required.", owner.Id, Key); return false; }
            return base.Validate(owner, out error);
        }
        public bool TryDrop(EntityHandle actorHandle, bool toss, out string error)
        {
            if (!TryActor(actorHandle, out var actor, out error)) return false;
            if (actor.CarriedItem != pickup) { error = "This actor is not carrying the requested item."; return false; }
            if (toss) actor.DropCarried(); else actor.PlaceCarried();
            lastActor = null;
            error = null;
            return true;
        }
        public override bool TryInteract(EntityHandle actorHandle, out string error)
        {
            if (!TryActor(actorHandle, out var actor, out error)) return false;
            bool accepted = base.TryInteract(actorHandle, out error);
            if (accepted && pickup != null && pickup.Carrier == actor) lastActor = actor;
            return accepted;
        }
        public override void CancelInteraction()
        {
            if (pickup != null && lastActor != null && lastActor.CarriedItem == pickup) lastActor.PlaceCarried();
            lastActor = null;
            base.CancelInteraction();
        }
        public override void Detach()
        {
            CancelInteraction();
            base.Detach();
        }
    }
}
