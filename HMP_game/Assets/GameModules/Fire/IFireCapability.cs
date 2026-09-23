namespace HMProtection.Modules.Fire
{
    public enum FireState { None, SmokeOnly, Small, Medium, Large }

    /// <summary>Fire-domain contract; entity infrastructure has no dependency on this interface.</summary>
    public interface IFireCapability
    {
        FireState CurrentState { get; }
        bool HasEffect { get; }
        bool DebugKeyEnabled { get; }
        bool TrySetState(FireState state, out string error);
        bool TrySetFrozen(bool frozen, out string error);
        bool TrySetVisualParameters(float intensity, float scale, float smokeAmount, out string error);
    }
}
