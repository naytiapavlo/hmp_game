using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HMProtection.Navigation
{
    public enum NavigationState { Loading, ReadyToActivate, Activating, Completed, Failed, Cancelled }

    /// <summary>One observable scene-navigation request. IDs make stale UI callbacks harmless.</summary>
    public sealed class NavigationOperation
    {
        internal NavigationOperation(long id, string scenePath) { Id = id; ScenePath = scenePath; }
        public long Id { get; }
        public string ScenePath { get; }
        internal HMProtection.EntityAdapters.LevelDefinition Definition { get; set; }
        internal bool DefinitionConsumed { get; set; }
        public float Progress { get; internal set; }
        public NavigationState State { get; internal set; } = NavigationState.Loading;
        public string Error { get; internal set; }
        public bool IsTerminal => State == NavigationState.Completed || State == NavigationState.Failed || State == NavigationState.Cancelled;
        public event Action<NavigationOperation> Changed;
        internal bool ActivationRequested { get; private set; }
        internal bool CancellationRequested { get; private set; }
        internal bool NativeLoadStarted { get; set; }
        /// <summary>Unity can cancel safely only before it has created its AsyncOperation.</summary>
        public bool IsCancellable => State == NavigationState.Loading && !NativeLoadStarted;
        /// <summary>May be called before readiness; the request activates as soon as it is loaded.</summary>
        public void AllowActivation() { if (!IsTerminal) ActivationRequested = true; }
        public bool TryCancel()
        {
            if (!IsCancellable) return false;
            CancellationRequested = true;
            return true;
        }
        [Obsolete("Use TryCancel and inspect its return value.")]
        public void Cancel() => TryCancel();
        internal void Notify()
        {
            if (Changed == null) return;
            foreach (Action<NavigationOperation> callback in Changed.GetInvocationList())
                try { callback(this); }
                catch (Exception exception) { Debug.LogException(exception); }
        }
    }

    /// <summary>
    /// The sole runtime gateway for single-scene navigation.  It serializes requests,
    /// exposes progress and keeps activation under the caller's control for CGs.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AppNavigationService : MonoBehaviour
    {
        static AppNavigationService instance;
        long nextId;
        public NavigationOperation Active { get; private set; }
        public static AppNavigationService Instance
        {
            get
            {
                if (instance != null) return instance;
                var root = new GameObject("App Navigation Service");
                DontDestroyOnLoad(root);
                return instance = root.AddComponent<AppNavigationService>();
            }
        }

        public event Action<NavigationOperation> OperationFinished;
        public bool TryLoadDefinition(HMProtection.EntityAdapters.LevelDefinition definition,
            out NavigationOperation operation, out string error)
        {
            operation = null;
            if (definition == null) { error = "A level definition is required."; return false; }
            if (!definition.Validate(out error) || !TryLoadSingle(definition.scenePath, out operation, out error)) return false;
            operation.Definition = definition;
            return true;
        }
        /// <summary>Only the destination bootstrapper may consume this one navigation's content selection.</summary>
        public static bool TryTakeDefinition(string scenePath, out HMProtection.EntityAdapters.LevelDefinition definition)
        {
            definition = null;
            var operation = instance != null ? instance.Active : null;
            if (operation == null || operation.DefinitionConsumed || operation.Definition == null
                || !string.Equals(operation.ScenePath, scenePath, StringComparison.Ordinal)
                || operation.State != NavigationState.Activating) return false;
            operation.DefinitionConsumed = true;
            definition = operation.Definition;
            return true;
        }

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>Starts one load. A concurrent request is rejected rather than replacing a live transition.</summary>
        public bool TryLoadSingle(string scenePath, out NavigationOperation operation, out string error)
        {
            operation = null;
            if (Active != null && !Active.IsTerminal) { error = "Another navigation operation is already active."; return false; }
            if (string.IsNullOrWhiteSpace(scenePath)) { error = "A scene path is required."; return false; }
#if UNITY_EDITOR
            if (UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.SceneAsset>(scenePath) == null)
            { error = "Scene asset does not exist: " + scenePath; return false; }
#else
            if (!Application.CanStreamedLevelBeLoaded(scenePath))
            { error = "Scene is not included in this build: " + scenePath; return false; }
#endif
            error = null;
            operation = new NavigationOperation(++nextId, scenePath);
            Active = operation;
            StartCoroutine(LoadRoutine(operation));
            return true;
        }

        public void Cancel(NavigationOperation operation)
        {
            TryCancel(operation);
        }

        public bool TryCancel(NavigationOperation operation)
        {
            if (operation == null || operation != Active || operation.IsTerminal) return false;
            return operation.TryCancel();
        }

        IEnumerator LoadRoutine(NavigationOperation request)
        {
            // Let a caller paint its transition frame before deserializing the scene.
            yield return null;
            if (request.CancellationRequested) { Finish(request, NavigationState.Cancelled, null); yield break; }
            request.NativeLoadStarted = true;
            AsyncOperation load = null;
            try
            {
#if UNITY_EDITOR
                load = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                    request.ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
                load = SceneManager.LoadSceneAsync(request.ScenePath, LoadSceneMode.Single);
#endif
            }
            catch (Exception exception) { Finish(request, NavigationState.Failed, exception.Message); yield break; }
            if (load == null) { Finish(request, NavigationState.Failed, "Unity did not create a scene load operation."); yield break; }
            load.allowSceneActivation = false;
            while (load.progress < .9f)
            {
                request.Progress = Mathf.Clamp01(load.progress / .9f);
                request.Notify();
                yield return null;
            }
            request.Progress = 1f;
            request.State = NavigationState.ReadyToActivate;
            request.Notify();
            while (!request.ActivationRequested) yield return null;
            request.State = NavigationState.Activating;
            request.Notify();
            load.allowSceneActivation = true;
            while (!load.isDone) yield return null;
            Finish(request, NavigationState.Completed, null);
        }

        void Finish(NavigationOperation request, NavigationState state, string error)
        {
            request.Error = error;
            request.State = state;
            request.Notify();
            if (OperationFinished != null)
                foreach (Action<NavigationOperation> callback in OperationFinished.GetInvocationList())
                    try { callback(request); }
                    catch (Exception exception) { Debug.LogException(exception); }
            if (Active == request) Active = null;
        }
    }
}
