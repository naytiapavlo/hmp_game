using UnityEngine;

namespace HMProtection.EntityAdapters
{
    [CreateAssetMenu(menuName = "HM Protection/Level Definition")]
    public sealed class LevelDefinition : ScriptableObject
    {
        [System.Serializable]
        public sealed class FireInitialState
        {
            public string entityId;
            public HMProtection.Modules.Fire.FireState state;
        }
        [Tooltip("Optional level.json. When assigned, JSON is the source of truth for content settings.")]
        public TextAsset packageConfig;
        public string packageRoot;
        [TextArea] public string luaSource;
        [TextArea] public string luaApiSource;
        [Tooltip("Optional package-local JSON configuration declared by script.config.")]
        public TextAsset scriptConfig;
        public bool scriptEnabled;
        public string scriptEntry;
        public string levelId;
        public string displayName;
        public string scenePath;
        public string menuScenePath = "Assets/Scenes/初始界面.unity";
        [Tooltip("Compatibility driver only. Disable for interaction/suppression scenes without questions.")]
        public bool runLegacyStages;
        public string legacyConfigPath;
        public TextAsset questionCatalog;
        public FireInitialState[] initialFires = System.Array.Empty<FireInitialState>();
        public bool Validate(out string error)
        {
            error = null;
            if (packageConfig != null && !ReadPackage(out error)) return false;
            if (string.IsNullOrWhiteSpace(levelId) || string.IsNullOrWhiteSpace(scenePath))
                error = "Level ID and scene path are required.";
            else if (runLegacyStages && string.IsNullOrWhiteSpace(legacyConfigPath))
                error = "The legacy stage driver requires an explicit config path.";
            var ids = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (var fire in initialFires ?? System.Array.Empty<FireInitialState>())
                if (fire == null || string.IsNullOrWhiteSpace(fire.entityId) || !ids.Add(fire.entityId)
                    || fire.state < HMProtection.Modules.Fire.FireState.None || fire.state > HMProtection.Modules.Fire.FireState.Large)
                { error = "Initial fire entries require unique entity IDs and valid states."; break; }
            return error == null;
        }
        bool ReadPackage(out string error)
        {
            if (!HMProtection.LevelPackages.LevelPackageConfig.TryParse(packageConfig.text, out var config, out error)) return false;
            if (packageRoot != "Assets/Levels/" + config.LevelId)
            { error = "Package directory must match levelId: Assets/Levels/" + config.LevelId; return false; }
            if (!HMProtection.LevelPackages.LevelPackageConfig.TryResolveAssetPath(packageRoot, config.Scene, out var scene, out error)) return false;
            if (string.IsNullOrEmpty(config.Script.ConfigPath))
            {
                if (scriptConfig != null) { error = "scriptConfig must be empty when script.config is not declared."; return false; }
            }
            else
            {
                var expectedName = System.IO.Path.GetFileNameWithoutExtension(config.Script.ConfigPath);
                if (scriptConfig == null || scriptConfig.name != expectedName)
                { error = "scriptConfig must match the name declared by script.config."; return false; }
            }
            // Copy only after the whole document has passed validation.
            var fires = new FireInitialState[config.InitialFires.Length];
            for (int index = 0; index < fires.Length; index++)
                fires[index] = new FireInitialState { entityId = config.InitialFires[index].EntityId,
                    state = (HMProtection.Modules.Fire.FireState)System.Enum.Parse(typeof(HMProtection.Modules.Fire.FireState), config.InitialFires[index].State) };
            levelId = config.LevelId; displayName = config.DisplayName; scenePath = scene;
            runLegacyStages = config.Legacy.Enabled; legacyConfigPath = config.Legacy.ConfigResource;
            scriptEnabled = config.Script.Enabled; scriptEntry = config.Script.Entry; initialFires = fires;
            return true;
        }
        public bool ApplyInitialState(LevelSceneBindings bindings, out string error)
        {
            error = null;
            foreach (var entry in initialFires ?? System.Array.Empty<FireInitialState>())
            {
                if (!bindings.Scope.Registry.TryResolve(entry.entityId, out var handle, out var failure)
                    || !bindings.Scope.Registry.TryGetCapability<HMProtection.Modules.Fire.IFireCapability>(handle, out var fire, out failure))
                { error = failure.ToString(); return false; }
                if (!fire.TrySetState(entry.state, out error)) return false;
            }
            return true;
        }
    }
}
