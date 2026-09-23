using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HMProtection.Entities
{
    [DisallowMultipleComponent]
    public sealed class EntityScopeHost : MonoBehaviour
    {
        [SerializeField] private string levelId;
        [SerializeField] private bool autoInstall;
        [SerializeField] private Transform[] contentRoots;
        [SerializeField] private SceneBindingSet bindingSet;
        [SerializeField] private EntityHitProxy[] hitProxies;
        private readonly HashSet<Scene> ownedScenes = new HashSet<Scene>();
        public EntityScope Scope { get; private set; }
        public IReadOnlyList<Transform> ContentRoots => contentRoots;
        public SceneBindingSet BindingSet => bindingSet;
        public IReadOnlyList<EntityHitProxy> HitProxies => hitProxies;
        public void Configure(string newLevelId, IEnumerable<Transform> roots, SceneBindingSet bindings = null, IEnumerable<EntityHitProxy> proxies = null, bool installOnEnable = false)
        { levelId = newLevelId; contentRoots = roots == null ? new Transform[0] : new List<Transform>(roots).ToArray(); bindingSet = bindings; hitProxies = proxies == null ? new EntityHitProxy[0] : new List<EntityHitProxy>(proxies).ToArray(); autoInstall = installOnEnable; }
        private void OnEnable() { if (autoInstall && Scope == null && !TryInitialize(levelId, out var error)) Debug.LogError(error.ToString(), this); }
        private void OnDestroy() { DisposeScope(); }
        private void OnSceneUnloaded(Scene scene) { if (Scope != null && ownedScenes.Contains(scene)) DisposeScope(); }
        public bool TryInitialize(string requestedLevelId, out EntityError error)
        {
            if (Scope != null && !Scope.IsDisposed) { error = EntityError.Create(EntityErrorCode.OperationRejected, "Initialize", "Host already owns a scope."); return false; }
            var scope = new EntityScope(string.IsNullOrWhiteSpace(requestedLevelId) ? levelId : requestedLevelId);
            ownedScenes.Clear(); foreach (var root in contentRoots ?? new Transform[0]) if (root != null) ownedScenes.Add(root.gameObject.scene);
            if (!scope.TryInitialize(bindingSet, contentRoots, hitProxies, out error)) { scope.Dispose(); ownedScenes.Clear(); return false; }
            Scope = scope; SceneManager.sceneUnloaded += OnSceneUnloaded; error = default; return true;
        }
        public void DisposeScope() { SceneManager.sceneUnloaded -= OnSceneUnloaded; Scope?.Dispose(); Scope = null; ownedScenes.Clear(); }
        public bool Validate(out List<EntityError> errors) => SceneEntityInstaller.Validate(this, out errors);
    }
}
