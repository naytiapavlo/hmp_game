using HMProtection.Entities;
using HMProtection.Modules.Interaction;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    /// <summary>Bridges an existing IInteractable into the entity command path without changing its behaviour.</summary>
    [DisallowMultipleComponent]
    public class LegacyInteractionCapability : EntityCapability, IInteractionCapability, IInteractionCancellation
    {
        [SerializeField] private MonoBehaviour interactableComponent;
        [SerializeField] private EntityInteractionSource interactionSource;
        private EntityCapabilityContext context;
        private IInteractable target;
        private readonly InteractionRequestGate requestGate = new InteractionRequestGate();

        public override string Key => "interaction";

        public void Configure(MonoBehaviour legacyTarget, EntityInteractionSource source)
        {
            interactableComponent = legacyTarget;
            interactionSource = source;
            target = legacyTarget as IInteractable;
        }

        public override bool Validate(GameEntity owner, out EntityError error)
        {
            target = interactableComponent as IInteractable;
            if (target != null && interactionSource != null && interactableComponent.gameObject.scene == owner.gameObject.scene)
            {
                error = default;
                return true;
            }
            error = EntityError.Create(EntityErrorCode.MissingReference, "interaction.validate",
                "An explicit IInteractable target and EntityInteractionSource are required.", owner.Id, Key);
            return false;
        }

        public override bool Attach(EntityCapabilityContext value, out EntityError error)
        {
            context = value;
            error = default;
            return true;
        }

        public override void Detach()
        {
            CancelInteraction();
            context = null;
        }

        protected bool TryActor(EntityHandle actorHandle, out Interactor actor, out string error)
        {
            actor = null;
            if (context == null || !context.Scope.IsReady || !context.Scope.Registry.IsValid(context.Handle)
                || !isActiveAndEnabled || target == null || interactionSource == null)
            {
                error = "Interaction capability is not ready.";
                return false;
            }
            return interactionSource.TryGetActor(actorHandle, out actor, out error);
        }

        public virtual bool TryGetPrompt(EntityHandle actorHandle, out string prompt, out string error)
        {
            prompt = null;
            if (!TryActor(actorHandle, out var actor, out error)) return false;
            prompt = target.GetInteractPrompt(actor);
            return !string.IsNullOrEmpty(prompt);
        }

        public virtual bool TryInteract(EntityHandle actorHandle, out string error)
        {
            if (!TryActor(actorHandle, out var actor, out error)) return false;
            if (!requestGate.TryEnter(Time.frameCount))
            {
                error = "This interaction request is already being handled.";
                return false;
            }
            try
            {
                if (string.IsNullOrEmpty(target.GetInteractPrompt(actor)))
                {
                    error = "Interaction is currently unavailable.";
                    return false;
                }
                target.Interact(actor);
                error = null;
                return true;
            }
            finally { requestGate.Exit(); }
        }

        public virtual void CancelInteraction()
        {
            requestGate.Cancel();
            if (target is IInteractionCancellation cancellation) cancellation.CancelInteraction();
        }
    }
}
