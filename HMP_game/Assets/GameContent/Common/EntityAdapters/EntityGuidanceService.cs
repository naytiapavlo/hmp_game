using System;
using HMProtection.Entities;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    public enum RouteOutcome { Arrived, Cancelled, TargetLost, Unreachable }
    public sealed class EntityGuidanceService : MonoBehaviour
    {
        public event Action<Guid, RouteOutcome> Finished;
        EntityScope scope;
        LevelSessionHost session;
        GuidanceSystem backend;
        EntityHandle target;
        string slot, label;
        Guid request;
        public Guid ActiveOperation => request;
        Vector3 lastPosition;
        public void Configure(EntityScope value, LevelSessionHost owner, GuidanceSystem guidance, Transform player)
        {
            Cancel();
            if (backend != null) backend.OnDestinationReached -= Arrived;
            if (session != null) session.Stopping -= Cancel;
            scope = value; session = owner; backend = guidance;
            if (backend != null) { backend.BindPlayer(player); backend.OnDestinationReached += Arrived; }
            if (session != null) session.Stopping += Cancel;
        }
        public bool TryNavigate(EntityHandle entity, string anchorSlot, string text, out Guid operation, out string error)
        {
            Cancel(); operation = Guid.Empty; error = null;
            if (scope == null || !scope.IsReady || backend == null || !TryPose(entity, anchorSlot, out var position))
            { error = "Route requires a ready scope, backend and valid target anchor."; return false; }
            target = entity; slot = anchorSlot; label = text; request = operation = Guid.NewGuid(); lastPosition = position;
            backend.ShowRoute(position, label);
            if (!backend.HasPlannedPath) { End(RouteOutcome.Unreachable); error = "Target is unreachable."; return false; }
            return true;
        }
        bool TryPose(EntityHandle entity, string name, out Vector3 position)
        {
            position = default;
            if (scope == null || !scope.Registry.TryGetCapability<IAnchorCapability>(entity, out var anchor, out _)
                || !anchor.TryGetPose(name, out var pose, out _)) return false;
            position = pose.Position; return true;
        }
        void Update()
        {
            if (request == Guid.Empty) return;
            if (!TryPose(target, slot, out var position)) { End(RouteOutcome.TargetLost); return; }
            if ((position - lastPosition).sqrMagnitude > .09f)
            {
                lastPosition = position; backend.ShowRoute(position, label);
                if (!backend.HasPlannedPath) End(RouteOutcome.Unreachable);
            }
        }
        void Arrived(Vector3 _, string __) { if (request != Guid.Empty) End(RouteOutcome.Arrived); }
        void End(RouteOutcome outcome)
        {
            if (request == Guid.Empty) return;
            var completed = request; request = Guid.Empty;
            if (backend != null) backend.HideRoute();
            Finished?.Invoke(completed, outcome);
        }
        public void Cancel() => End(RouteOutcome.Cancelled);
        void OnDestroy()
        {
            Cancel();
            if (backend != null) backend.OnDestinationReached -= Arrived;
            if (session != null) session.Stopping -= Cancel;
        }
    }
}
