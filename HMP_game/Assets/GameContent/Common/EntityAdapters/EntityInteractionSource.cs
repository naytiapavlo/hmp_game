using HMProtection.Entities;
using UnityEngine;

namespace HMProtection.EntityAdapters
{
    /// <summary>Level-neutral boundary used by the player raycaster and interaction capabilities.</summary>
    public abstract class EntityInteractionSource : MonoBehaviour
    {
        public abstract bool ManagesCollider(Collider collider);
        public abstract bool TryGetInteraction(Collider collider, out EntityHandle target, out string prompt);
        public abstract bool TryInteract(EntityHandle target, out string error);
        public abstract bool TryGetActor(EntityHandle actor, out Interactor value, out string error);
    }
}
