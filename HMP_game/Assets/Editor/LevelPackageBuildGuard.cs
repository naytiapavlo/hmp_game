using HMProtection.LevelPackages;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

/// <summary>Reject broken manifests or stale generated Lua source before creating a player.</summary>
public sealed class LevelPackageBuildGuard : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;
    public void OnPreprocessBuild(BuildReport report)
    {
        try { LevelPackageTools.ValidateAll(); }
        catch (System.Exception error) { throw new BuildFailedException("Level package validation failed: " + error.Message); }
    }
}
