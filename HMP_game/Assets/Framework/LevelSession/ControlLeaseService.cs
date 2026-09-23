using System;
using System.Collections.Generic;

namespace HMProtection.Sessions
{
    [Flags]
    public enum ControlMask
    {
        None = 0,
        Simulation = 1 << 0,
        Movement = 1 << 1,
        Look = 1 << 2,
        Interaction = 1 << 3,
        ToolUse = 1 << 4
    }

    /// <summary>Combines independent restrictions; releasing one owner cannot unlock another owner's restriction.</summary>
    public sealed class ControlLeaseService : IDisposable
    {
        private readonly Dictionary<int, ControlMask> leases = new Dictionary<int, ControlMask>();
        private int nextId;
        private bool disposed;

        public IDisposable Acquire(object owner, ControlMask mask)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (disposed || mask == ControlMask.None) return EmptyLease.Instance;
            var id = ++nextId;
            leases.Add(id, mask);
            return new Lease(this, id);
        }

        public bool IsBlocked(ControlMask mask)
        {
            if (mask == ControlMask.None) return false;
            foreach (var held in leases.Values) if ((held & mask) != 0) return true;
            return false;
        }

        public void Dispose() { if (disposed) return; disposed = true; leases.Clear(); }
        private void Release(int id) { leases.Remove(id); }

        private sealed class Lease : IDisposable
        {
            private ControlLeaseService service;
            private readonly int id;
            public Lease(ControlLeaseService service, int id) { this.service = service; this.id = id; }
            public void Dispose() { var current = service; service = null; current?.Release(id); }
        }
        private sealed class EmptyLease : IDisposable { public static readonly EmptyLease Instance = new EmptyLease(); public void Dispose() { } }
    }
}
