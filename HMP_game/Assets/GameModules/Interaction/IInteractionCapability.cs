using HMProtection.Entities;

namespace HMProtection.Modules.Interaction
{
    public interface IInteractionCapability
    {
        bool TryGetPrompt(EntityHandle actor, out string prompt, out string error);
        // Success means the request was forwarded, not that a mission was completed.
        bool TryInteract(EntityHandle actor, out string error);
    }
}
