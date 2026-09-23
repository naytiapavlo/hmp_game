using HMProtection.Entities;
using HMProtection.Modules.Interaction;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    /// <summary>
    /// Reusable scene bridge for entity-backed interaction levels. It has no office or quiz dependency;
    /// collision routing comes solely from EntityHitProxy registrations in the configured scope host.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EntityInteractionBridge : LevelSceneBindings
    {
        [SerializeField] private EntityScopeHost scopeHost;
        [SerializeField] private GameEntity playerEntity;
        [SerializeField] private Interactor actor;
        [SerializeField] private LevelSessionHost sessionHost;
        private readonly InteractionRequestGate requests = new InteractionRequestGate();

        public override EntityScope Scope => scopeHost != null ? scopeHost.Scope : null;
        public override bool IsReady => Scope != null && Scope.IsReady;
        public override body Player => actor != null ? actor.GetComponent<body>() : null;

        public void Configure(EntityScopeHost host, GameEntity player, Interactor playerActor, LevelSessionHost sessions = null)
        {
            scopeHost = host;
            playerEntity = player;
            actor = playerActor;
            sessionHost = sessions;
        }

        public override bool TryInitialize(string levelId, out string error)
        {
            error = null;
            if (scopeHost == null || playerEntity == null || actor == null || actor.gameObject != playerEntity.gameObject)
            {
                error = "Entity interaction bridge needs explicit scope host, player entity and matching Interactor.";
                return false;
            }
            if (!scopeHost.TryInitialize(levelId, out var entityError))
            {
                error = entityError.ToString();
                return false;
            }
            if (!Scope.Registry.TryResolve(playerEntity.Id, out var handle, out entityError)
                || !playerEntity.Handle.Equals(handle))
            {
                error = "The configured player entity is not registered in this scope.";
                DisposeScope();
                return false;
            }
            if (sessionHost == null) sessionHost = GetComponent<LevelSessionHost>();
            if (sessionHost == null) sessionHost = gameObject.AddComponent<LevelSessionHost>();
            if (!sessionHost.Bind(Scope, out error))
            {
                DisposeScope();
                return false;
            }
            var playerBody = Player;
            if (playerBody != null) playerBody.sessionHost = sessionHost;
            return true;
        }

        public override void DisposeScope()
        {
            // Keep the stopped host on body: IsBlocked then fail-closes movement, tools and interaction
            // until the next successful Bind replaces it. Clearing it would revive legacy input mid-stop.
            sessionHost?.Release();
            requests.Cancel();
            scopeHost?.DisposeScope();
        }

        // This remains true after a scope failure/disposal so Interactor never falls back to a legacy
        // IInteractable on a collider explicitly reserved for entity routing.
        public override bool ManagesCollider(Collider collider)
        {
            if (collider == null || scopeHost == null || scopeHost.HitProxies == null) return false;
            foreach (var proxy in scopeHost.HitProxies)
                if (proxy != null && proxy.ContainsCollider(collider)) return true;
            return false;
        }

        public override bool TryGetInteraction(Collider collider, out EntityHandle target, out string prompt)
        {
            target = default;
            prompt = null;
            if (!TryGetPlayerHandle(out var actorHandle, out _)
                || !Scope.Registry.TryResolveHit(collider, out target, out _)
                || !Scope.Registry.TryGetCapability<IInteractionCapability>(target, out var capability, out _)) return false;
            return capability.TryGetPrompt(actorHandle, out prompt, out _);
        }

        public override bool TryInteract(EntityHandle target, out string error)
            => TryInteractRequest(target, target.ScopeId + ":" + target.Id.Value + ":frame-" + Time.frameCount, out error);

        /// <summary>Idempotent command entry for networked/UI callers that already own a request ID.</summary>
        public bool TryInteractRequest(EntityHandle target, string requestId, out string error)
        {
            if (!requests.TryEnter(requestId)) { error = "This interaction request was already handled or cancelled."; return false; }
            if (sessionHost == null || sessionHost.IsBlocked(HMProtection.Sessions.ControlMask.Interaction))
            { requests.Cancel(requestId); error = "Interaction is blocked by the current level session."; return false; }
            if (!TryGetPlayerHandle(out var actorHandle, out error)) { requests.Cancel(requestId); return false; }
            if (!Scope.Registry.TryGetCapability<IInteractionCapability>(target, out var capability, out var entityError))
            {
                error = entityError.ToString();
                requests.Cancel(requestId); return false;
            }
            try
            {
                bool accepted = capability.TryInteract(actorHandle, out error);
                if (accepted) requests.Complete(requestId); else requests.Cancel(requestId);
                return accepted;
            }
            catch (System.Exception exception)
            {
                requests.Cancel(requestId);
                error = exception.Message;
                Debug.LogException(exception, this);
                return false;
            }
        }

        public void CancelInteractionRequest(string requestId) => requests.Cancel(requestId);

        public override bool TryGetActor(EntityHandle requested, out Interactor value, out string error)
        {
            value = null;
            if (!TryGetPlayerHandle(out var expected, out error)) return false;
            if (!expected.Equals(requested) || actor == null || !actor.isActiveAndEnabled
                || sessionHost == null || sessionHost.IsBlocked(HMProtection.Sessions.ControlMask.Interaction))
            {
                error = "The requested actor is not the active player for this scope.";
                return false;
            }
            value = actor;
            return true;
        }

        public override bool TryRolePose(string role, string slot, out Vector3 position, out string error)
        {
            position = default;
            if (!TryRole(role, out var handle, out error)) return false;
            return TryPose(handle, slot, out position, out error);
        }

        public override bool TryGetAnchorPosition(string anchorId, out Vector3 position, out string error)
        {
            position = default;
            if (!IsReady)
            {
                error = "Entity interaction scope is not ready.";
                return false;
            }
            if (!Scope.Registry.TryResolve(anchorId, out var handle, out var entityError))
            { error = entityError.ToString(); return false; }
            return TryPose(handle, "origin", out position, out error);
        }

        private bool TryGetPlayerHandle(out EntityHandle handle, out string error)
        {
            handle = default;
            if (!IsReady || playerEntity == null)
            {
                error = "Entity interaction scope is not ready.";
                return false;
            }
            if (!Scope.Registry.TryResolve(playerEntity.Id, out handle, out var entityError))
            { error = entityError.ToString(); return false; }
            error = null;
            return true;
        }

        private bool TryRole(string role, out EntityHandle handle, out string error)
        {
            handle = default;
            if (!IsReady) { error = "Entity interaction scope is not ready."; return false; }
            if (Scope.Bindings != null && Scope.Bindings.TryResolveRole(role, Scope, out handle, out var entityError)) { error = null; return true; }
            error = "Missing role binding: " + role;
            return false;
        }

        private bool TryPose(EntityHandle handle, string slot, out Vector3 position, out string error)
        {
            position = default;
            if (!Scope.Registry.TryGetCapability<IAnchorCapability>(handle, out var anchor, out var entityError)
                || !anchor.TryGetPose(slot, out var pose, out entityError))
            {
                error = entityError.ToString();
                return false;
            }
            position = pose.Position;
            error = null;
            return true;
        }
    }
}
