using System.Collections.Generic;
using UnityEngine;

namespace HMProtection.Entities
{
    [DisallowMultipleComponent]
    public sealed class EntityHitProxy : MonoBehaviour
    {
        [SerializeField] private GameEntity target;
        [SerializeField] private List<Collider> colliders = new List<Collider>();
        private EntityRegistry registry;
        public GameEntity Target => target;
        public IReadOnlyList<Collider> Colliders => colliders;
        public void Configure(GameEntity newTarget, IEnumerable<Collider> newColliders) { target = newTarget; colliders = newColliders == null ? new List<Collider>() : new List<Collider>(newColliders); }
        internal void AttachRegistry(EntityRegistry value) { registry = value; }
        internal void DetachRegistry(EntityRegistry value) { if (registry == value) registry = null; }
        internal bool Contains(Collider collider) => colliders.Contains(collider);
        public bool ContainsCollider(Collider collider) => collider != null && colliders.Contains(collider);
        private void OnDestroy() { registry?.NotifyProxyDestroyed(this); registry = null; }
    }
}
