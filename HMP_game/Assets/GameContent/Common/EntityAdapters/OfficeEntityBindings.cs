using System;
using System.Collections.Generic;
using HMProtection.Entities;
using HMProtection.Modules.Fire;
using HMProtection.Modules.Interaction;
using HMProtection.Quiz;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    /// <summary>Opt-in migration boundary. No hierarchy or global searches are used at runtime.</summary>
    [DisallowMultipleComponent]
    public sealed class OfficeEntityBindings : LevelSceneBindings
    {
        [Serializable] public sealed class TargetBinding
        { public string targetId, entityId, slot = "approach", label; }
        [Serializable] public sealed class AnchorBinding
        { public string anchorId, entityId, slot = "origin"; }
        public EntityScopeHost host;
        public LevelSessionHost sessionHost;
        public EntityGuidanceService routeService;
        public HMProtection.Presentation.SessionPresentationGate presentationGate;
        public body player;
        public Interactor actor;
        public OfficeFireChoiceFlow flow;
        public GuidanceSystem guidance;
        public HMProtection.Core.LevelFlowRunner runner;
        public TargetBinding[] targets = Array.Empty<TargetBinding>();
        public AnchorBinding[] anchors = Array.Empty<AnchorBinding>();
        public Collider[] managedColliders = Array.Empty<Collider>();
        public override bool IsReady => host != null && host.Scope != null && host.Scope.IsReady;
        public override EntityScope Scope => host != null ? host.Scope : null;

        public override body Player => player;
        public override void ApplyDefinition(LevelDefinition definition)
        {
            if (flow != null && flow.presenter != null) flow.presenter.catalogAsset = definition != null ? definition.questionCatalog : null;
        }

        public void Configure(EntityScopeHost scopeHost, body playerBody, Interactor interactor,
            OfficeFireChoiceFlow quiz, GuidanceSystem route, TargetBinding[] targetBindings, AnchorBinding[] anchorBindings)
        {
            host = scopeHost; player = playerBody; actor = interactor; flow = quiz; guidance = route;
            targets = targetBindings ?? Array.Empty<TargetBinding>(); anchors = anchorBindings ?? Array.Empty<AnchorBinding>();
        }

        public override bool TryInitialize(string levelId, out string error)
        {
            if (!ValidateReferences(out error)) return false;
            if (!host.TryInitialize(levelId, out var failure)) { error = failure.ToString(); return false; }
            if (!TryRole("player", out var playerHandle, out error)
                || player.GetComponent<GameEntity>() == null || !player.GetComponent<GameEntity>().Handle.Equals(playerHandle))
            { error = error ?? "The player role must bind the explicitly configured player entity."; DisposeScope(); return false; }
            if (!TryRolePose("spawn", "origin", out _, out error) || !TryGetFire(out _, out error)
                || !TryRolePose("primaryFire", "origin", out _, out error))
            { DisposeScope(); return false; }
            foreach (var binding in targets)
            {
                if (!TryGetTargetPosition(binding.targetId, out _, out _, out error)
                    || !Scope.Registry.TryResolve(binding.entityId, out var handle, out failure)
                    || !Scope.Registry.TryGetCapability<IInteractionCapability>(handle, out _, out failure))
                { error = error ?? failure.ToString(); DisposeScope(); return false; }
            }
            foreach (var binding in anchors)
                if (!TryGetAnchorPosition(binding.anchorId, out _, out error)) { DisposeScope(); return false; }
            foreach (var collider in managedColliders)
                if (!Scope.Registry.TryResolveHit(collider, out _, out failure))
                { error = failure.ToString(); DisposeScope(); return false; }
            if (sessionHost == null) sessionHost = GetComponent<LevelSessionHost>() ?? gameObject.AddComponent<LevelSessionHost>();
            if (!sessionHost.Bind(Scope, out error)) { DisposeScope(); return false; }
            player.sessionHost = sessionHost;
            foreach (var root in host.ContentRoots)
                if (root != null)
                    foreach (var fire in root.GetComponentsInChildren<FireEffectController>(true)) fire.sessionHost = sessionHost;
            presentationGate = GetComponent<HMProtection.Presentation.SessionPresentationGate>() ?? gameObject.AddComponent<HMProtection.Presentation.SessionPresentationGate>();
            presentationGate.Configure(sessionHost, player, actor.Hud);
            var interaction = flow.GetComponent<OfficeChoiceInteraction>();
            if (interaction != null)
            {
                if (interaction.feedback != null) interaction.feedback.gate = presentationGate;
                if (interaction.instructor != null) interaction.instructor.gate = presentationGate;
            }
            routeService = GetComponent<EntityGuidanceService>() ?? gameObject.AddComponent<EntityGuidanceService>();
            routeService.Configure(Scope, sessionHost, guidance, player.transform);
            return true;
        }

        public bool ValidateReferences(out string error)
        {
            error = null;
            if (host == null || player == null || actor == null || flow == null || guidance == null
                || runner == null || actor.gameObject != player.gameObject || flow.presenter == null)
            { error = "Office entity migration needs explicit host, player/actor, flow/presenter and guidance references."; return false; }
            if (flow.entityBindings != this || flow.presenter.entityBindings != this || actor.entityBindings != this)
            { error = "Office entity migration must be enabled on the flow, presenter and actor together."; return false; }
            if (targets == null || anchors == null || managedColliders == null || targets.Length == 0 || managedColliders.Length == 0)
            { error = "Office entity target/anchor/collider bindings are missing."; return false; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in targets)
                if (b == null || string.IsNullOrWhiteSpace(b.targetId) || !ids.Add(b.targetId)
                    || string.IsNullOrWhiteSpace(b.entityId) || string.IsNullOrWhiteSpace(b.slot))
                { error = "Invalid or duplicate office target binding."; return false; }
            ids.Clear();
            foreach (var b in anchors)
                if (b == null || string.IsNullOrWhiteSpace(b.anchorId) || !ids.Add(b.anchorId)
                    || string.IsNullOrWhiteSpace(b.entityId) || string.IsNullOrWhiteSpace(b.slot))
                { error = "Invalid or duplicate office anchor binding."; return false; }
            var colliders = new HashSet<Collider>();
            foreach (var c in managedColliders)
                if (c == null || !colliders.Add(c)) { error = "Missing or duplicate managed collider."; return false; }
            return true;
        }

        public override void DisposeScope() { if (sessionHost != null) sessionHost.Release(); if (host != null) host.DisposeScope(); }
        public bool TryRole(string role, out EntityHandle handle, out string error)
        {
            handle = default; error = null;
            if (!IsReady) { error = "Entity scope is not ready or has been disposed."; return false; }
            if (Scope.Bindings.TryResolveRole(role, Scope, out handle, out var failure)) return true;
            error = failure.ToString(); return false;
        }
        public override bool TryRolePose(string role, string slot, out Vector3 position, out string error)
        {
            position = default;
            return TryRole(role, out var handle, out error) && TryPose(handle, slot, out position, out error);
        }
        bool TryPose(EntityHandle handle, string slot, out Vector3 position, out string error)
        {
            position = default; error = null;
            if (!IsReady) { error = "Entity scope is not ready."; return false; }
            if (!Scope.Registry.TryGetCapability<IAnchorCapability>(handle, out var anchor, out var failure)
                || !anchor.TryGetPose(slot, out var pose, out failure))
            { error = failure.ToString(); return false; }
            position = pose.Position; return true;
        }
        bool TryPosition(string entityId, string slot, out Vector3 position, out string error)
        {
            position = default; error = null;
            if (!IsReady) { error = "Entity scope is not ready."; return false; }
            if (!Scope.Registry.TryResolve(entityId, out var handle, out var failure)) { error = failure.ToString(); return false; }
            return TryPose(handle, slot, out position, out error);
        }
        public bool TryGetTargetPosition(string targetId, out Vector3 position, out string label, out string error)
        {
            position = default; label = null; error = null;
            var b = Array.Find(targets ?? Array.Empty<TargetBinding>(), x => x != null && string.Equals(x.targetId, targetId, StringComparison.Ordinal));
            if (b == null) { error = "Missing entity binding for target " + targetId; return false; }
            label = b.label;
            return TryPosition(b.entityId, b.slot, out position, out error);
        }
        public bool TryShowTargetRoute(string targetId, out string error)
        {
            error = null;
            var b = Array.Find(targets ?? Array.Empty<TargetBinding>(), x => x != null && x.targetId == targetId);
            if (!IsReady || routeService == null || b == null) { error = "Route bindings are unavailable."; return false; }
            if (!Scope.Registry.TryResolve(b.entityId, out var handle, out var failure)) { error = failure.ToString(); return false; }
            return routeService.TryNavigate(handle, b.slot, b.label, out _, out error);
        }
        public override bool TryGetAnchorPosition(string anchorId, out Vector3 position, out string error)
        {
            position = default; error = null;
            var b = Array.Find(anchors ?? Array.Empty<AnchorBinding>(), x => x != null && string.Equals(x.anchorId, anchorId, StringComparison.Ordinal));
            if (b == null) { error = "Missing entity binding for anchor " + anchorId; return false; }
            return TryPosition(b.entityId, b.slot, out position, out error);
        }
        public bool TryGetFire(out IFireCapability fire, out string error)
        {
            fire = null;
            if (!TryRole("primaryFire", out var handle, out error)) return false;
            if (Scope.Registry.TryGetCapability<IFireCapability>(handle, out fire, out var failure)) return true;
            error = failure.ToString(); return false;
        }
        public override bool TryGetActor(EntityHandle handle, out Interactor value, out string error)
        {
            value = null;
            if (!TryRole("player", out var expected, out error)) return false;
            if (!expected.Equals(handle) || actor == null || !actor.isActiveAndEnabled
                || sessionHost == null || sessionHost.IsBlocked(HMProtection.Sessions.ControlMask.Interaction))
            { error = "Invalid or inactive actor for this entity scope."; return false; }
            value = actor; return true;
        }
        public override bool ManagesCollider(Collider collider) => collider != null && managedColliders != null && Array.IndexOf(managedColliders, collider) >= 0;
        public override bool TryGetInteraction(Collider collider, out EntityHandle target, out string prompt)
        {
            target = default; prompt = null;
            if (!IsReady || !TryRole("player", out var actorHandle, out _)
                || !Scope.Registry.TryResolveHit(collider, out target, out _)
                || !Scope.Registry.TryGetCapability<IInteractionCapability>(target, out var interaction, out _)) return false;
            return interaction.TryGetPrompt(actorHandle, out prompt, out _);
        }
        public override bool TryInteract(EntityHandle target, out string error)
        {
            if (!TryRole("player", out var actorHandle, out error)) return false;
            if (!Scope.Registry.TryGetCapability<IInteractionCapability>(target, out var interaction, out var failure))
            { error = failure.ToString(); return false; }
            return interaction.TryInteract(actorHandle, out error);
        }
    }
}
