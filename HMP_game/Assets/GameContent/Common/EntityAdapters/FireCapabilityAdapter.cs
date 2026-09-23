using HMProtection.Entities;
using HMProtection.Modules.Fire;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    [DisallowMultipleComponent]
    public sealed class FireCapabilityAdapter : EntityCapability, IFireCapability
    {
        [SerializeField] FireEffectController controller;
        EntityCapabilityContext context;
        public override string Key => "fire";
        public FireState CurrentState => controller != null ? (FireState)controller.CurrentLevel : FireState.None;
        public bool HasEffect => controller != null && controller.HasFire;
        public bool DebugKeyEnabled => controller != null && controller.DebugKeyEnabled;

        public void Configure(FireEffectController value) => controller = value;
        public override bool Validate(GameEntity owner, out EntityError error)
        {
            error = default;
            if (controller != null && controller.gameObject.scene == owner.gameObject.scene) return true;
            error = EntityError.Create(EntityErrorCode.MissingReference, "fire.validate", "Explicit fire controller in the same scene is required.", owner.Id, Key);
            return false;
        }
        public override bool Attach(EntityCapabilityContext value, out EntityError error)
        { context = value; error = default; return true; }
        public override void Detach() => context = null;
        bool Ready(out string error)
        {
            error = null;
            if (context != null && context.Scope.IsReady && context.Scope.Registry.IsValid(context.Handle)
                && controller != null && controller.isActiveAndEnabled && isActiveAndEnabled) return true;
            error = "Fire capability is detached, inactive or no longer valid.";
            return false;
        }
        public bool TrySetState(FireState state, out string error)
        {
            if (!Ready(out error)) return false;
            if (state < FireState.None || state > FireState.Large) { error = "Unknown fire state."; return false; }
            controller.SetLevel((FireLevel)state);
            return true;
        }
        public bool TrySetFrozen(bool frozen, out string error)
        { if (!Ready(out error)) return false; controller.SetFrozen(frozen); return true; }
        public bool TrySetVisualParameters(float intensity, float scale, float smokeAmount, out string error)
        {
            if (!Ready(out error)) return false;
            if (float.IsNaN(intensity) || float.IsInfinity(intensity) || float.IsNaN(scale) || float.IsInfinity(scale)
                || float.IsNaN(smokeAmount) || float.IsInfinity(smokeAmount) || scale < 0)
            { error = "Fire parameters must be finite; scale must be nonnegative."; return false; }
            var vfx = controller.CurrentVfx;
            if (vfx != null) { vfx.SetIntensity(intensity); vfx.SetScale(scale); vfx.SetSmokeAmount(smokeAmount); }
            return true;
        }
    }
}
