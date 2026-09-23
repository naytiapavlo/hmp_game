using System;
using System.Collections.Generic;
using System.Linq;
using HMProtection.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HMProtection.Entities.Editor
{
    /// <summary>Read-only authoring checks. This never installs, registers, attaches or prepares a capability.</summary>
    public static class EntitySceneValidator
    {
        public sealed class Finding
        {
            public readonly MessageType Severity;
            public readonly string Message;
            public readonly UnityEngine.Object Context;
            public Finding(MessageType severity, string message, UnityEngine.Object context)
            { Severity = severity; Message = message; Context = context; }
        }

        [MenuItem("Tools/Entity System/Validate Active Scene")]
        public static void ValidateActiveSceneMenu()
        {
            var findings = Validate(SceneManager.GetActiveScene());
            foreach (var finding in findings)
            {
                if (finding.Severity == MessageType.Error) Debug.LogError("[Entity Validation] " + finding.Message, finding.Context);
                else if (finding.Severity == MessageType.Warning) Debug.LogWarning("[Entity Validation] " + finding.Message, finding.Context);
                else Debug.Log("[Entity Validation] " + finding.Message, finding.Context);
            }
            Debug.Log("[Entity Validation] " + SceneManager.GetActiveScene().name + ": " + findings.Count + " finding(s).");
        }

        public static bool HasErrors(IEnumerable<Finding> findings) => findings != null && findings.Any(finding => finding.Severity == MessageType.Error);

        public static List<Finding> Validate(Scene scene)
        {
            var findings = new List<Finding>();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                findings.Add(new Finding(MessageType.Error, "Scene is not loaded.", null));
                return findings;
            }

            var sceneEntities = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<GameEntity>(true)).ToArray();
            var coverage = new Dictionary<GameEntity, EntityScopeHost>();
            var hosts = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<EntityScopeHost>(true)).ToArray();
            foreach (var host in hosts)
            {
                var scopeEntities = CollectHostEntities(host, findings);
                ValidateScope(host, scopeEntities, findings);
                foreach (var entity in scopeEntities)
                {
                    if (coverage.TryGetValue(entity, out var firstHost) && firstHost != host)
                        findings.Add(new Finding(MessageType.Error, "Entity '" + DisplayId(entity) + "' is included by both scope hosts '" + firstHost.name + "' and '" + host.name + "'.", entity));
                    else coverage[entity] = host;
                }
            }

            foreach (var entity in sceneEntities)
                if (!coverage.ContainsKey(entity))
                    findings.Add(new Finding(MessageType.Warning, "GameEntity '" + DisplayId(entity) + "' is not covered by any EntityScopeHost content root in this scene.", entity));
            if (sceneEntities.Length == 0) findings.Add(new Finding(MessageType.Warning, "No GameEntity components exist in this scene.", null));
            return findings;
        }

        private static List<GameEntity> CollectHostEntities(EntityScopeHost host, List<Finding> findings)
        {
            var entities = new List<GameEntity>();
            var unique = new HashSet<GameEntity>();
            if (host.ContentRoots == null || host.ContentRoots.Count == 0)
            {
                findings.Add(new Finding(MessageType.Error, "Scope host has no content roots.", host));
                return entities;
            }
            foreach (var root in host.ContentRoots)
            {
                if (root == null) { findings.Add(new Finding(MessageType.Error, "Scope host has a missing content root.", host)); continue; }
                foreach (var entity in root.GetComponentsInChildren<GameEntity>(true)) if (entity != null && unique.Add(entity)) entities.Add(entity);
            }
            return entities;
        }

        private static void ValidateScope(EntityScopeHost host, List<GameEntity> entities, List<Finding> findings)
        {
            var byId = new Dictionary<string, GameEntity>(StringComparer.Ordinal);
            foreach (var entity in entities)
            {
                string id = entity.Id.Value;
                if (!IsValidId(id)) findings.Add(new Finding(MessageType.Error, "Entity ID '" + DisplayId(entity) + "' is empty or contains invalid characters.", entity));
                else if (byId.TryGetValue(id, out var first)) findings.Add(new Finding(MessageType.Error, "Duplicate entity ID '" + id + "' within scope host; first owner is '" + first.name + "'.", entity));
                else byId.Add(id, entity);
                ValidateEntity(entity, findings);
            }
            ValidateBindings(host, byId, findings);
            ValidateHitProxies(host, entities, findings);
        }

        private static void ValidateEntity(GameEntity entity, List<Finding> findings)
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in entity.Tags)
                if (string.IsNullOrWhiteSpace(tag) || !tags.Add(tag)) findings.Add(new Finding(MessageType.Error, "Entity '" + DisplayId(entity) + "' has an empty or duplicate tag.", entity));

            GameObject activationTarget = entity.ActivationTarget;
            if (activationTarget == null || !(activationTarget.transform == entity.transform || activationTarget.transform.IsChildOf(entity.transform)))
                findings.Add(new Finding(MessageType.Error, "Entity '" + DisplayId(entity) + "' has an activation target outside its own hierarchy.", entity));

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var interfaces = new HashSet<Type>();
            foreach (var capability in entity.Capabilities)
            {
                if (capability == null) { findings.Add(new Finding(MessageType.Error, "Entity '" + DisplayId(entity) + "' has a missing capability reference.", entity)); continue; }
                if (capability.gameObject != entity.gameObject) findings.Add(new Finding(MessageType.Error, "Capability '" + capability.GetType().Name + "' must be on the same GameObject as entity '" + DisplayId(entity) + "'.", capability));
                if (string.IsNullOrWhiteSpace(capability.Key) || !keys.Add(capability.Key)) findings.Add(new Finding(MessageType.Error, "Entity '" + DisplayId(entity) + "' has an empty or duplicate capability key '" + capability.Key + "'.", capability));
                foreach (var implemented in capability.GetType().GetInterfaces()) if (!interfaces.Add(implemented)) findings.Add(new Finding(MessageType.Error, "Entity '" + DisplayId(entity) + "' has duplicate capability interface '" + implemented.Name + "'.", capability));
                if (!capability.Validate(entity, out var error)) findings.Add(new Finding(MessageType.Error, error.ToString(), capability));
            }
        }

        private static void ValidateBindings(EntityScopeHost host, Dictionary<string, GameEntity> byId, List<Finding> findings)
        {
            if (host.BindingSet == null) return;
            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in host.BindingSet.Bindings)
            {
                if (binding == null || string.IsNullOrWhiteSpace(binding.Role)) { findings.Add(new Finding(MessageType.Error, "Scope binding has a missing role.", host.BindingSet)); continue; }
                if (!roles.Add(binding.Role)) findings.Add(new Finding(MessageType.Error, "Scope binding role '" + binding.Role + "' is duplicated.", host.BindingSet));
                if (!byId.TryGetValue(binding.EntityId.Value, out var entity))
                {
                    if (binding.Required) findings.Add(new Finding(MessageType.Error, "Required role '" + binding.Role + "' targets missing entity '" + binding.EntityId.Value + "'.", host.BindingSet));
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(binding.CapabilityKey) && !entity.Capabilities.Any(capability => capability != null && string.Equals(capability.Key, binding.CapabilityKey, StringComparison.Ordinal)))
                    findings.Add(new Finding(MessageType.Error, "Role '" + binding.Role + "' requires capability '" + binding.CapabilityKey + "' on entity '" + entity.Id.Value + "'.", host.BindingSet));
                if (!string.IsNullOrWhiteSpace(binding.AnchorSlot))
                {
                    var anchor = entity.Capabilities.OfType<IAnchorCapability>().FirstOrDefault();
                    if (anchor == null || !anchor.HasSlot(binding.AnchorSlot)) findings.Add(new Finding(MessageType.Error, "Role '" + binding.Role + "' requires anchor slot '" + binding.AnchorSlot + "' on entity '" + entity.Id.Value + "'.", host.BindingSet));
                }
            }
        }

        private static void ValidateHitProxies(EntityScopeHost host, List<GameEntity> entities, List<Finding> findings)
        {
            var entitySet = new HashSet<GameEntity>(entities);
            var colliders = new HashSet<Collider>();
            var proxies = new HashSet<EntityHitProxy>();
            foreach (var proxy in host.HitProxies ?? Array.Empty<EntityHitProxy>())
            {
                if (proxy == null) { findings.Add(new Finding(MessageType.Error, "Scope host has a missing hit proxy reference.", host)); continue; }
                if (!proxies.Add(proxy)) findings.Add(new Finding(MessageType.Error, "Scope host lists the same hit proxy more than once.", proxy));
                if (proxy.Target == null || !entitySet.Contains(proxy.Target)) findings.Add(new Finding(MessageType.Error, "Hit proxy target is missing or is not included in this scope's content roots.", proxy));
                foreach (var collider in proxy.Colliders)
                {
                    if (collider == null) { findings.Add(new Finding(MessageType.Error, "Hit proxy has a missing Collider reference.", proxy)); continue; }
                    if (!colliders.Add(collider)) findings.Add(new Finding(MessageType.Error, "Collider '" + collider.name + "' is listed by more than one hit-proxy entry.", proxy));
                }
            }
        }

        private static bool IsValidId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (char character in value) if (!(char.IsLower(character) || char.IsDigit(character) || character == '.' || character == '_' || character == '-')) return false;
            return true;
        }

        private static string DisplayId(GameEntity entity) => string.IsNullOrWhiteSpace(entity.Id.Value) ? "<empty>" : entity.Id.Value;
    }
}
