using HMProtection.Entities;
using HMProtection.Modules.Visibility;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    [DisallowMultipleComponent]
    public sealed class VisibilityCapabilityAdapter : EntityCapability, IVisibilityCapability
    {
        [SerializeField] private GameObject[] targets = new GameObject[0];
        [SerializeField] private bool visibleOnAttach = true;
        private EntityCapabilityContext context;
        public override string Key => "visibility";
        public bool IsVisible { get; private set; }
        public void Configure(GameObject[] values, bool visible) { targets = values ?? new GameObject[0]; visibleOnAttach = visible; }
        public override bool Validate(GameEntity owner, out EntityError error)
        {
            if (targets == null || targets.Length == 0) { error = EntityError.Create(EntityErrorCode.MissingReference, "visibility.validate", "At least one visibility target is required.", owner.Id, Key); return false; }
            foreach (var target in targets) if (target == null || target.scene != owner.gameObject.scene) { error = EntityError.Create(EntityErrorCode.InvalidOwner, "visibility.validate", "Visibility targets must be in the owner scene.", owner.Id, Key); return false; }
            error = default; return true;
        }
        public override bool Attach(EntityCapabilityContext value, out EntityError error) { context = value; IsVisible = targets.Length > 0 && targets[0] != null && targets[0].activeSelf; error = default; return true; }
        public override bool Prepare(out EntityError error) { error = default; Apply(visibleOnAttach); return true; }
        public override void Detach() => context = null;
        public bool TrySetVisible(bool visible, out string error)
        {
            if (context == null || !context.Scope.IsReady || !context.Scope.Registry.IsValid(context.Handle) || targets == null)
            { error = "Visibility capability is detached or its scope is unavailable."; return false; }
            Apply(visible); error = null; return true;
        }
        private void Apply(bool visible)
        {
            foreach (var target in targets) if (target != null) target.SetActive(visible);
            IsVisible = visible;
        }
    }
}
