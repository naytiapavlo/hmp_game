using System;
using System.Collections.Generic;
using System.IO;
using HMProtection.EntityAdapters;
using HMProtection.Modules.Fire;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using MoonSharp.Interpreter;

namespace HMProtection.LevelPackages
{
    public static class LevelPackageTools
    {
        const string Root = "Assets/Levels";
        const string OfficeScene = "Assets/Scenes/办公室场景-实体试点.unity";
        const string SharedScene = "Assets/Scenes/第三场景-通用模块试点.unity";
        const string Catalog = "Assets/StreamingAssets/Quiz/scene_choices.json";
        const string Legacy = "Assets/Resources/Configs/Levels/level_1_initial_fire.json";
        const string LuaApi = "Assets/StreamingAssets/Lua/component_api.lua";
        static readonly string[] ids = { "level_1_initial_fire", "third_scene_shared_modules_a", "third_scene_shared_modules_b" };

        [MenuItem("Tools/Levels/Create Packages")]
        public static void CreateAll() { CreateAllCore(false); }
        public static void CreateAllBatch() { CreateAllCore(true); }
        [MenuItem("Tools/Levels/Validate Packages")]
        public static void ValidateAllMenu() { ValidateAll(); Debug.Log("Level package validation passed."); }
        [MenuItem("Tools/Levels/Refresh Runtime Definitions")]
        public static void RefreshGeneratedDefinitions()
        {
            foreach (var id in PackageIds())
            {
                string root = Root + "/" + id;
                var definition = AssetDatabase.LoadAssetAtPath<LevelDefinition>(root + "/Runtime/" + id + ".asset");
                if (definition == null) throw new InvalidOperationException("Missing runtime definition for " + id);
                definition.packageConfig = AssetDatabase.LoadAssetAtPath<TextAsset>(root + "/Config/level.json");
                definition.packageRoot = root;
                if (definition.packageConfig == null) throw new InvalidOperationException("Missing Config/level.json for " + id);
                if (!LevelPackageConfig.TryParse(definition.packageConfig.text, out var config, out var error)) throw new InvalidOperationException(error);
                if (!LevelPackageConfig.TryResolveAssetPath(root, config.Script.Entry, out var luaPath, out error) || !File.Exists(luaPath)) throw new InvalidOperationException(error ?? "Lua entry is missing.");
                definition.scriptConfig = LoadScriptConfig(root, config, out error);
                if (error != null) throw new InvalidOperationException(error);
                if (!File.Exists(LuaApi)) throw new InvalidOperationException("Shared Lua component API is missing: " + LuaApi);
                definition.luaSource = File.ReadAllText(luaPath);
                definition.luaApiSource = File.ReadAllText(LuaApi);
                if (!definition.Validate(out error)) throw new InvalidOperationException(error);
                EditorUtility.SetDirty(definition);
            }
            EnsureBuildScenes();
            AssetDatabase.SaveAssets();
            foreach (var id in PackageIds())
            {
                string root = Root + "/" + id;
                var definition = AssetDatabase.LoadAssetAtPath<LevelDefinition>(root + "/Runtime/" + id + ".asset");
                WriteSharedAudit(root, definition.scenePath);
            }
        }

        static void CreateAllCore(bool batch)
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var loaded = SceneManager.GetSceneAt(index);
                if (loaded.isLoaded && loaded.isDirty) throw new InvalidOperationException("Save all loaded scenes before creating level packages.");
            }
            foreach (var id in ids) CreatePackage(id, id == "level_1_initial_fire" ? OfficeScene : SharedScene);
            EnsureBuildScenes();
            ValidateAll();
            if (batch) AssetDatabase.SaveAssets();
        }
        static void CreatePackage(string id, string sourceScene)
        {
            string root = Root + "/" + id;
            if (AssetDatabase.IsValidFolder(root)) { ValidatePackageRoot(root, id, out var existing); if (!string.IsNullOrEmpty(existing)) throw new InvalidOperationException(existing); return; }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(sourceScene) == null || AssetDatabase.LoadAssetAtPath<TextAsset>(Catalog) == null || !File.Exists(LuaApi)
                || (id == "level_1_initial_fire" && AssetDatabase.LoadAssetAtPath<TextAsset>(Legacy) == null))
                throw new InvalidOperationException("Source scene or source configuration is missing for " + id);
            CreateFolders(root);
            string scene = root + "/Scenes/" + id + ".unity";
            if (!AssetDatabase.CopyAsset(sourceScene, scene)) throw new InvalidOperationException("Could not copy source scene " + sourceScene);
            if (!AssetDatabase.CopyAsset(Catalog, root + "/Config/scene_choices.json")) throw new InvalidOperationException("Could not copy question catalog.");
            bool office = id == "level_1_initial_fire";
            if (office && !AssetDatabase.CopyAsset(Legacy, root + "/Config/Resources/Levels/level_1_initial_fire/legacy.json")) throw new InvalidOperationException("Could not copy legacy config.");
            File.WriteAllText(Path.Combine(root, "Config/level.json"), Manifest(id, office));
            File.WriteAllText(Path.Combine(root, "Scripts/main.lua"), LuaStub(id));
            File.WriteAllText(Path.Combine(root, "Art/README.md"), "Place only level-specific source art here. Shared visual assets remain referenced from their existing project locations.\n");
            AssetDatabase.Refresh();
            var definition = ScriptableObject.CreateInstance<LevelDefinition>();
            definition.levelId = id; definition.displayName = office ? "Initial Fire" : id == "third_scene_shared_modules_a" ? "Shared Modules A" : "Shared Modules B";
            definition.scenePath = scene; definition.runLegacyStages = office; definition.legacyConfigPath = office ? "Levels/level_1_initial_fire/legacy" : "";
            definition.questionCatalog = AssetDatabase.LoadAssetAtPath<TextAsset>(root + "/Config/scene_choices.json");
            definition.initialFires = office ? Array.Empty<LevelDefinition.FireInitialState>() : id.EndsWith("_a")
                ? new[] { new LevelDefinition.FireInitialState { entityId = "fire.third.a", state = FireState.Small }, new LevelDefinition.FireInitialState { entityId = "fire.third.b", state = FireState.Medium } }
                : new[] { new LevelDefinition.FireInitialState { entityId = "fire.third.a", state = FireState.Large }, new LevelDefinition.FireInitialState { entityId = "fire.third.b", state = FireState.SmokeOnly } };
            definition.packageConfig = AssetDatabase.LoadAssetAtPath<TextAsset>(root + "/Config/level.json");
            definition.packageRoot = root;
            definition.luaSource = File.ReadAllText(Path.Combine(root, "Scripts/main.lua"));
            definition.luaApiSource = File.ReadAllText(LuaApi);
            definition.scriptConfig = null;
            string asset = root + "/Runtime/" + id + ".asset"; AssetDatabase.CreateAsset(definition, asset);
            if (!definition.Validate(out var definitionError)) throw new InvalidOperationException("Generated definition invalid: " + definitionError);
            EditorUtility.SetDirty(definition); AssetDatabase.SaveAssets();
            OpenAndAssign(scene, definition);
            WriteSharedAudit(root, scene);
        }
        static void CreateFolders(string root)
        {
            if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets", "Levels");
            AssetDatabase.CreateFolder(Root, Path.GetFileName(root));
            foreach (var part in new[] { "Scenes", "Config", "Scripts", "Art", "Art/Models", "Art/Materials", "Art/Textures", "Art/Prefabs", "Art/Audio", "Runtime", "Config/Resources", "Config/Resources/Levels", "Config/Resources/Levels/level_1_initial_fire" })
            {
                string current = root;
                foreach (var piece in part.Split('/')) { string next = current + "/" + piece; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, piece); current = next; }
            }
        }
        static void OpenAndAssign(string scenePath, LevelDefinition definition)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var found = new List<HMProtection.Core.LevelBootstrapper>();
            foreach (var root in scene.GetRootGameObjects()) found.AddRange(root.GetComponentsInChildren<HMProtection.Core.LevelBootstrapper>(true));
            if (found.Count != 1) throw new InvalidOperationException("Copied scene must have exactly one LevelBootstrapper; found " + found.Count + ".");
            var boot = found[0];
            boot.definition = definition; EditorUtility.SetDirty(boot); EditorSceneManager.SaveScene(scene);
        }
        static string Manifest(string id, bool office)
        {
            bool first = id == "third_scene_shared_modules_a";
            var fires = new JArray();
            if (!office)
            {
                fires.Add(new JObject { ["entityId"] = "fire.third.a", ["state"] = first ? "Small" : "Large" });
                fires.Add(new JObject { ["entityId"] = "fire.third.b", ["state"] = first ? "Medium" : "SmokeOnly" });
            }
            return new JObject {
                ["schemaVersion"] = 1, ["levelId"] = id,
                ["displayName"] = office ? "初起火灾应对" : first ? "通用模块训练 A" : "通用模块训练 B",
                ["scene"] = "Scenes/" + id + ".unity",
                ["script"] = new JObject { ["enabled"] = false, ["entry"] = "Scripts/main.lua" },
                ["artRoots"] = new JArray("Art"),
                ["legacy"] = new JObject { ["enabled"] = office, ["configResource"] = office ? "Levels/level_1_initial_fire/legacy" : "" },
                ["initialFires"] = fires
            }.ToString();
        }
        static string LuaStub(string id) => "local component_api = require('component_api')\nlocal module = {}\nfunction module.new(host, json) return { api = component_api.new(host, json) } end\nfunction module.update(self) return self.api:update() end\nfunction module.stop(self) return self.api:dispose() end\nreturn module\n";
        static IEnumerable<string> PackageIds()
        {
            if (!AssetDatabase.IsValidFolder(Root)) yield break;
            foreach (var folder in AssetDatabase.GetSubFolders(Root)) yield return Path.GetFileName(folder);
        }
        static void EnsureBuildScenes()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (var id in PackageIds())
            {
                string manifest = Root + "/" + id + "/Config/level.json";
                if (!File.Exists(manifest) || !LevelPackageConfig.TryParse(File.ReadAllText(manifest), out var config, out _)
                    || !LevelPackageConfig.TryResolveAssetPath(Root + "/" + id, config.Scene, out var path, out _)) continue;
                int existing = scenes.FindIndex(item => item.path == path);
                if (existing < 0) scenes.Add(new EditorBuildSettingsScene(path, true));
                else scenes[existing] = new EditorBuildSettingsScene(path, true);
            }
            EditorBuildSettings.scenes = scenes.ToArray();
        }
        public static void ValidateAll()
        {
            var report = new List<string>();
            foreach (var id in PackageIds()) { ValidatePackageRoot(Root + "/" + id, id, out var error); if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error); report.Add("PASS " + id); }
            File.WriteAllText("Library/LevelPackageValidation.txt", string.Join("\n", report));
        }
        public static bool ValidatePackage(LevelDefinition definition, out string error)
        {
            error = null; if (definition == null || string.IsNullOrWhiteSpace(definition.packageRoot)) { error = "Definition/package root is missing."; return false; }
            ValidatePackageRoot(definition.packageRoot, definition.levelId, out error); return string.IsNullOrEmpty(error);
        }
        static void ValidatePackageRoot(string root, string id, out string error)
        {
            error = null; string config = root + "/Config/level.json";
            if (EditorApplication.isPlayingOrWillChangePlaymode) { error = "Package validation is unavailable in Play Mode."; return; }
            if (!AssetDatabase.IsValidFolder(root) || !File.Exists(root + "/Art/README.md") || !File.Exists(root + "/Config/shared-assets.json") || !File.Exists(config)) { error = "Package " + id + " is incomplete."; return; }
            if (!LevelPackageConfig.TryParse(File.ReadAllText(config), out var parsed, out error) || parsed.LevelId != id) { error = error ?? "Package ID mismatch."; return; }
            if (!LevelPackageConfig.TryResolveAssetPath(root, parsed.Scene, out var scene, out error) || AssetDatabase.LoadAssetAtPath<SceneAsset>(scene) == null) { error = error ?? "Manifest scene is missing."; return; }
            if (!LevelPackageConfig.TryResolveAssetPath(root, parsed.Script.Entry, out var lua, out error) || !File.Exists(lua)) { error = error ?? "Manifest Lua entry is missing."; return; }
            var scriptConfig = LoadScriptConfig(root, parsed, out error);
            if (error != null) return;
            if (!File.Exists(LuaApi)) { error = "Shared Lua component API is missing: " + LuaApi; return; }
            foreach (var art in parsed.ArtRoots) if (!LevelPackageConfig.TryResolveAssetPath(root, art, out var artPath, out error) || !AssetDatabase.IsValidFolder(artPath)) { error = error ?? "Manifest art root is missing."; return; }
            if (parsed.Legacy.Enabled)
            {
                var resource = Resources.Load<TextAsset>(parsed.Legacy.ConfigResource);
                if (resource == null || !AssetDatabase.GetAssetPath(resource).StartsWith(root + "/", StringComparison.Ordinal))
                { error = "Enabled legacy config must resolve to a TextAsset inside this package."; return; }
            }
            bool enabledInBuild = false;
            foreach (var item in EditorBuildSettings.scenes) if (item.path == scene && item.enabled) enabledInBuild = true;
            if (!enabledInBuild) { error = "Package scene is not enabled in Build Settings: " + scene; return; }
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(root + "/Config/scene_choices.json") == null)
            { error = "Package question catalog is missing."; return; }
            var definition = AssetDatabase.LoadAssetAtPath<LevelDefinition>(root + "/Runtime/" + id + ".asset");
            string apiSource = File.ReadAllText(LuaApi);
            string luaSource = File.ReadAllText(lua);
            if (definition == null || definition.packageRoot != root || definition.packageConfig == null || definition.questionCatalog != AssetDatabase.LoadAssetAtPath<TextAsset>(root + "/Config/scene_choices.json") || definition.scriptConfig != scriptConfig || AssetDatabase.GetAssetPath(definition.packageConfig) != config || definition.packageConfig.text != File.ReadAllText(config) || definition.luaSource != luaSource || definition.luaApiSource != apiSource || !definition.Validate(out error)) { error = error ?? "Runtime definition is stale or does not match package files."; return; }
            if (parsed.Script.Enabled && !TryCompileLua(apiSource, luaSource, out error)) return;
            var opened = SceneManager.GetSceneByPath(scene);
            bool openedHere = !opened.IsValid() || !opened.isLoaded;
            if (openedHere) opened = EditorSceneManager.OpenScene(scene, OpenSceneMode.Additive);
            else if (opened.isDirty) { error = "Save this package scene before validation: " + scene; return; }
            try
            {
                var boots = new List<HMProtection.Core.LevelBootstrapper>(); foreach (var item in opened.GetRootGameObjects()) boots.AddRange(item.GetComponentsInChildren<HMProtection.Core.LevelBootstrapper>(true));
                if (boots.Count != 1 || boots[0].definition != definition) { error = "Copied scene must contain one bootstrapper bound to this definition."; return; }
                foreach (var item in opened.GetRootGameObjects()) foreach (var component in item.GetComponentsInChildren<Component>(true)) if (component == null) { error = "Scene contains a missing MonoBehaviour script."; return; }
            }
            finally { if (openedHere) EditorSceneManager.CloseScene(opened, true); }
        }
        static void WriteSharedAudit(string root, string scene)
        {
            var shared = new JArray(); foreach (var dependency in AssetDatabase.GetDependencies(scene, true)) if (dependency.StartsWith("Assets/", StringComparison.Ordinal) && !dependency.StartsWith(root + "/", StringComparison.Ordinal)) shared.Add(dependency);
            File.WriteAllText(Path.Combine(root, "Config/shared-assets.json"), new JObject { ["scene"] = scene, ["sharedAssets"] = shared }.ToString()); AssetDatabase.Refresh();
        }
        static TextAsset LoadScriptConfig(string root, LevelPackageConfig config, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(config.Script.ConfigPath)) return null;
            if (!LevelPackageConfig.TryResolveAssetPath(root, config.Script.ConfigPath, out var path, out error)) return null;
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) error = "Manifest script config is missing or is not a TextAsset: " + path;
            return asset;
        }
        static bool TryCompileLua(string apiSource, string luaSource, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(apiSource) || string.IsNullOrWhiteSpace(luaSource))
            {
                error = "Enabled Lua packages require non-empty API and entry sources.";
                return false;
            }
            try
            {
                // LoadString parses and compiles the chunks; it does not execute either source.
                new Script(CoreModules.Preset_Complete).LoadString(apiSource);
                new Script(CoreModules.Preset_Complete).LoadString(luaSource);
                return true;
            }
            catch (SyntaxErrorException exception)
            {
                error = "Lua syntax error: " + exception.DecoratedMessage;
                return false;
            }
            catch (Exception exception)
            {
                error = "Lua compilation failed: " + exception.Message;
                return false;
            }
        }
    }
}
