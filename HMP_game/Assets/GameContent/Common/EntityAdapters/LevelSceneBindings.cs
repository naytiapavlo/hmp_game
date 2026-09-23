using HMProtection.Entities;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    /// <summary>Scene-facing contracts used by preparation and choice views; no level-specific flow.</summary>
    public abstract class LevelSceneBindings : EntityInteractionSource
    {
        public HMProtection.Core.LevelBootstrapper Bootstrapper { get; internal set; }
        public abstract EntityScope Scope { get; }
        public abstract bool IsReady { get; }
        public abstract body Player { get; }
        public virtual void ApplyDefinition(LevelDefinition definition) { }
        public abstract bool TryInitialize(string levelId, out string error);
        public abstract void DisposeScope();
        public abstract bool TryRolePose(string role, string slot, out Vector3 position, out string error);
        public abstract bool TryGetAnchorPosition(string anchorId, out Vector3 position, out string error);
    }
}
