namespace HMProtection.Modules.Visibility
{
    public interface IVisibilityCapability
    {
        bool IsVisible { get; }
        bool TrySetVisible(bool visible, out string error);
    }
}
