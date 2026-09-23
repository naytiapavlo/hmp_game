using HMProtection.Entities;
using HMProtection.Modules.Interaction;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    [DisallowMultipleComponent]
    public sealed class SeatCapabilityAdapter : LegacyInteractionCapability, ISeatCapability
    {
        [SerializeField] private SeatController seat;
        public bool IsOccupied => seat != null && seat.IsOccupied;
        public bool IsTransitioning => seat != null && seat.IsTransitioning;
        public void Configure(SeatController value, EntityInteractionSource source) { seat = value; base.Configure(value, source); }
        public override bool Validate(GameEntity owner, out EntityError error)
        {
            if (seat == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "seat.validate", "SeatController is required.", owner.Id, Key); return false; }
            return base.Validate(owner, out error);
        }
        public bool TryLeave(EntityHandle actorHandle, out string error)
        {
            if (!TryActor(actorHandle, out var actor, out error)) return false;
            if (!seat.TryStand(actor)) { error = "Seat is currently unavailable."; return false; }
            error = null;
            return true;
        }
        public override void CancelInteraction() { base.CancelInteraction(); seat?.CancelSeat(); }
    }
}
