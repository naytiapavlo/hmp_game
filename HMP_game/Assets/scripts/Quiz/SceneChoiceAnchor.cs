using UnityEngine;
namespace HMProtection.Quiz
{
    [DisallowMultipleComponent]
    public sealed class SceneChoiceAnchor : MonoBehaviour
    {
        public string anchorId;
        void OnDrawGizmosSelected() { Gizmos.color = Color.red; Gizmos.DrawWireSphere(transform.position, .15f); }
    }
}
