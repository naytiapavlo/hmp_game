using System;
using System.Collections.Generic;
using UnityEngine;

namespace HMProtection.Entities
{
    public static class SceneEntityInstaller
    {
        public static bool Collect(IReadOnlyList<Transform> roots, out List<GameEntity> entities, out EntityError error)
        {
            entities = new List<GameEntity>(); var unique = new HashSet<GameEntity>();
            if (roots == null || roots.Count == 0) { error = EntityError.Create(EntityErrorCode.MissingReference, "Collect", "At least one content root is required."); return false; }
            foreach (var root in roots)
            {
                if (root == null) { error = EntityError.Create(EntityErrorCode.MissingReference, "Collect", "A content root is missing."); return false; }
                foreach (var entity in root.GetComponentsInChildren<GameEntity>(true)) if (entity != null && unique.Add(entity)) entities.Add(entity);
            }
            error = default; return true;
        }
        public static bool Validate(EntityScopeHost host, out List<EntityError> errors)
        {
            errors = new List<EntityError>();
            if (host == null) { errors.Add(EntityError.Create(EntityErrorCode.MissingReference, "Validate", "Scope host is missing.")); return false; }
            if (!Collect(host.ContentRoots, out var entities, out var collectError)) { errors.Add(collectError); return false; }
            var ids = new HashSet<EntityId>();
            foreach (var entity in entities)
            {
                if (entity.Id.IsEmpty) errors.Add(EntityError.Create(EntityErrorCode.InvalidId, "Validate", "Entity ID is empty.", entity.Id));
                else if (!ids.Add(entity.Id)) errors.Add(EntityError.Create(EntityErrorCode.DuplicateId, "Validate", "Entity ID is duplicated.", entity.Id));
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var capability in entity.Capabilities)
                {
                    if (capability == null) { errors.Add(EntityError.Create(EntityErrorCode.MissingReference, "Validate", "Capability is missing.", entity.Id)); continue; }
                    if (!keys.Add(capability.Key)) errors.Add(EntityError.Create(EntityErrorCode.DuplicateCapability, "Validate", "Capability key is duplicated.", entity.Id, capability.Key));
                    else if (!capability.Validate(entity, out var capabilityError)) errors.Add(capabilityError);
                }
            }
            return errors.Count == 0;
        }
    }
}
