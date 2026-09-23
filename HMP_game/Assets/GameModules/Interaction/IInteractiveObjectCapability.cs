using HMProtection.Entities;

namespace HMProtection.Modules.Interaction
{
    /// <summary>Stateful interaction contracts. A successful request only means the controller accepted it.</summary>
    public interface IInteractionCancellation
    {
        void CancelInteraction();
    }

    public interface IPickupCapability : IInteractionCapability
    {
        bool IsHeld { get; }
        bool TryDrop(EntityHandle actor, bool toss, out string error);
    }

    public interface IDoorCapability : IInteractionCapability
    {
        bool IsOpen { get; }
        bool IsAnimating { get; }
        bool TrySetOpen(EntityHandle actor, bool open, out string error);
    }

    public interface ISeatCapability : IInteractionCapability
    {
        bool IsOccupied { get; }
        bool IsTransitioning { get; }
        bool TryLeave(EntityHandle actor, out string error);
    }
}
