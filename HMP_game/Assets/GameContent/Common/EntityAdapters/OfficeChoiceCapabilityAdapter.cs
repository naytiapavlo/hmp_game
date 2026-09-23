using HMProtection.Entities;
using HMProtection.Modules.Interaction;
using HMProtection.Quiz;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    /// <summary>Transitional adapter: the existing office owner still evaluates the answer.</summary>
    [DisallowMultipleComponent]
    public sealed class OfficeChoiceCapabilityAdapter : EntityCapability, IInteractionCapability
    {
        [SerializeField] OfficeChoiceTarget target;
        [SerializeField] OfficeEntityBindings bindings;
        EntityCapabilityContext context;
        public override string Key => "interaction";
        public void Configure(OfficeChoiceTarget value, OfficeEntityBindings bridge) { target = value; bindings = bridge; }
        public override bool Validate(GameEntity owner, out EntityError error)
        {
            error = default;
            if (target != null && target.owner != null && bindings != null && target.gameObject.scene == owner.gameObject.scene) return true;
            error = EntityError.Create(EntityErrorCode.MissingReference, "interaction.validate", "Office target, owner and explicit bindings are required.", owner.Id, Key);
            return false;
        }
        public override bool Attach(EntityCapabilityContext value, out EntityError error)
        { context = value; error = default; return true; }
        public override void Detach() => context = null;
        bool TryActor(EntityHandle actorHandle, out Interactor actor, out string error)
        {
            actor = null; error = null;
            if (context == null || !context.Scope.IsReady || !context.Scope.Registry.IsValid(context.Handle)
                || !isActiveAndEnabled || target == null || bindings == null || !target.isActiveAndEnabled)
            { error = "Interaction capability is not ready."; return false; }
            return bindings.TryGetActor(actorHandle, out actor, out error);
        }
        public bool TryGetPrompt(EntityHandle actorHandle, out string prompt, out string error)
        {
            prompt = null;
            if (!TryActor(actorHandle, out var actor, out error)) return false;
            prompt = target.GetInteractPrompt(actor);
            return true;
        }
        public bool TryInteract(EntityHandle actorHandle, out string error)
        {
            if (!TryActor(actorHandle, out var actor, out error)) return false;
            if (string.IsNullOrEmpty(target.GetInteractPrompt(actor)))
            { error = "Interaction is currently unavailable."; return false; }
            target.Interact(actor);
            return true;
        }
    }
}
