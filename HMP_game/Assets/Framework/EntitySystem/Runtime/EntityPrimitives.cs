using System;
using UnityEngine;

namespace HMProtection.Entities
{
    [Serializable]
    public readonly struct EntityId : IEquatable<EntityId>, IComparable<EntityId>
    {
        [SerializeField] private readonly string value;
        public string Value => value ?? string.Empty;
        public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
        public EntityId(string value) { this.value = value; }
        public static EntityId Parse(string value) => new EntityId(value);
        public bool Equals(EntityId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is EntityId other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public int CompareTo(EntityId other) => StringComparer.Ordinal.Compare(Value, other.Value);
        public override string ToString() => Value;
        public static implicit operator EntityId(string value) => new EntityId(value);
    }

    [Serializable]
    public readonly struct EntityHandle : IEquatable<EntityHandle>
    {
        [SerializeField] private readonly string scopeId;
        [SerializeField] private readonly EntityId id;
        [SerializeField] private readonly int generation;
        public string ScopeId => scopeId ?? string.Empty;
        public EntityId Id => id;
        public int Generation => generation;
        public bool IsValid => !string.IsNullOrEmpty(ScopeId) && !Id.IsEmpty && Generation > 0;
        public EntityHandle(string scopeId, EntityId id, int generation) { this.scopeId = scopeId; this.id = id; this.generation = generation; }
        public bool Equals(EntityHandle other) => Generation == other.Generation && Id.Equals(other.Id) && string.Equals(ScopeId, other.ScopeId, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is EntityHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ScopeId, Id, Generation);
        public override string ToString() => $"{ScopeId}:{Id}@{Generation}";
    }

    public enum EntityErrorCode
    {
        None, InvalidId, DuplicateId, EntityNotFound, InvalidHandle, ScopeNotReady, ScopeDisposed,
        AlreadyOwned, Busy, MissingCapability, DuplicateCapability, CapabilityNotReady, MissingBinding,
        MissingReference, InvalidOwner, OperationRejected
    }

    public readonly struct EntityError
    {
        public EntityErrorCode Code { get; }
        public string Operation { get; }
        public string Message { get; }
        public EntityId EntityId { get; }
        public string CapabilityKey { get; }
        public bool IsNone => Code == EntityErrorCode.None;
        private EntityError(EntityErrorCode code, string operation, string message, EntityId id, string capabilityKey)
        { Code = code; Operation = operation ?? string.Empty; Message = message ?? string.Empty; EntityId = id; CapabilityKey = capabilityKey ?? string.Empty; }
        public static EntityError Create(EntityErrorCode code, string operation, string message, EntityId id = default, string capabilityKey = null)
            => new EntityError(code, operation, message, id, capabilityKey);
        public override string ToString() => $"{Code} [{Operation}] {Message} (entity={EntityId}, capability={CapabilityKey})";
    }

    public readonly struct EntityPose
    {
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public EntityPose(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
    }
}
