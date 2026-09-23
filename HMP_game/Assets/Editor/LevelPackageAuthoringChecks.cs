using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using HMProtection.EntityAdapters;
using HMProtection.LevelPackages;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Static authoring checks: package validation must be repeatable and reject stale or unsafe manifests.</summary>
public static class LevelPackageAuthoringChecks
{
    const string Root = "Assets/Levels";
    const string PackageA = "third_scene_shared_modules_a";
    const string Report = "Library/LevelPackageAuthoringChecks.txt";
    static readonly string[] PackageIds = { "level_1_initial_fire", PackageA, "third_scene_shared_modules_b" };

    [MenuItem("Tools/Levels/Check Package Authoring")]
    public static void Run() => Execute(false);

    /// <summary>Batch entrypoint. Writes the report and returns a non-zero editor exit status on failure.</summary>
    public static void RunBatch() => Execute(true);

    static void Execute(bool batch)
    {
        var report = new List<string>();
        SceneSetup[] layout = null;
        Exception failure = null;
        bool passed = false;
        try
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Package authoring checks require Edit Mode.");
            for (int index = 0; index < SceneManager.sceneCount; index++)
                Require(!SceneManager.GetSceneAt(index).isDirty, "Save all loaded scenes before package authoring checks.");
            layout = EditorSceneManager.GetSceneManagerSetup();
            // Generated fields were historically created by older package tools. Normalize them
            // before capturing the idempotence baseline; this refresh is deliberately excluded.
            LevelPackageTools.RefreshGeneratedDefinitions();

            foreach (var id in PackageIds)
            {
                string root = Root + "/" + id;
                Require(AssetDatabase.IsValidFolder(root), "Missing default package: " + root);
                Require(AssetDatabase.LoadAssetAtPath<LevelDefinition>(root + "/Runtime/" + id + ".asset") != null,
                    "Missing runtime definition for " + id + ".");
            }

            string aScenePath = Root + "/" + PackageA + "/Scenes/" + PackageA + ".unity";
            var packageAScene = SceneManager.GetSceneByPath(aScenePath);
            if (!packageAScene.IsValid() || !packageAScene.isLoaded)
                packageAScene = EditorSceneManager.OpenScene(aScenePath, OpenSceneMode.Additive);
            Require(packageAScene.isLoaded, "Could not open package A scene.");

            LevelPackageTools.ValidateAll();
            var stillOpen = SceneManager.GetSceneByPath(aScenePath);
            Require(stillOpen.IsValid() && stillOpen.isLoaded,
                "ValidateAll closed a scene that was already open before validation.");
            report.Add("PASS validation preserves a pre-opened package scene");

            var before = HashPackageFiles();
            LevelPackageTools.CreateAllBatch();
            var after = HashPackageFiles();
            Require(HashesEqual(before, after), "CreateAllBatch rewrote existing package assets.");
            report.Add("PASS CreateAllBatch is byte-idempotent for existing package assets");

            var definition = AssetDatabase.LoadAssetAtPath<LevelDefinition>(Root + "/" + PackageA + "/Runtime/" + PackageA + ".asset");
            Require(definition != null && definition.packageConfig != null, "Package A definition/config is unavailable.");
            VerifyLuaStaleness(definition);
            report.Add("PASS changed Lua source is rejected until its runtime cache is refreshed");
            VerifyMissingScene(definition);
            report.Add("PASS missing scene reference is rejected");
            VerifyLuaSyntaxAndBuildGuard(definition);
            report.Add("PASS invalid enabled Lua is rejected by both validation and the build guard");
            Require(LevelPackageTools.ValidatePackage(definition, out var finalError), "Package did not recover after negative checks: " + finalError);
            report.Add("PASS temporary mutations restore a valid package");
            passed = true;
        }
        catch (Exception exception)
        {
            failure = exception;
            report.Add(exception.ToString());
        }
        finally
        {
            try
            {
                if (layout != null)
                {
                    bool hasActive = false;
                    foreach (var item in layout) if (item.isLoaded && item.isActive) hasActive = true;
                    if (hasActive) EditorSceneManager.RestoreSceneManagerSetup(layout);
                    else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
            catch (Exception exception)
            {
                passed = false;
                failure = failure ?? exception;
                report.Add("Scene layout restore failed: " + exception);
            }
        }
        WriteReport(passed ? "PASS" : "FAIL", report);
        if (batch) EditorApplication.Exit(passed ? 0 : 1);
        else if (failure != null) Debug.LogException(failure);
    }

    static void VerifyLuaStaleness(LevelDefinition definition)
    {
        string path = definition.packageRoot + "/Scripts/main.lua";
        string original = File.ReadAllText(path);
        try
        {
            File.WriteAllText(path, original + "\n-- authoring check mutation\n");
            ImportNow(path);
            Require(!LevelPackageTools.ValidatePackage(definition, out _), "Changed Lua source was accepted despite stale runtime definition.");
        }
        finally
        {
            File.WriteAllText(path, original);
            ImportNow(path);
        }
    }

    static void VerifyMissingScene(LevelDefinition definition)
    {
        string path = definition.packageRoot + "/Config/level.json";
        string original = File.ReadAllText(path);
        try
        {
            var json = JObject.Parse(original);
            json["scene"] = "Scenes/missing.unity";
            File.WriteAllText(path, json.ToString(Newtonsoft.Json.Formatting.None));
            ImportNow(path);
            Require(!LevelPackageTools.ValidatePackage(definition, out _), "Manifest with a missing Unity scene was accepted.");
        }
        finally
        {
            File.WriteAllText(path, original);
            ImportNow(path);
        }
    }

    static void VerifyLuaSyntaxAndBuildGuard(LevelDefinition definition)
    {
        string manifestPath = definition.packageRoot + "/Config/level.json";
        string luaPath = definition.packageRoot + "/Scripts/main.lua";
        string originalManifest = File.ReadAllText(manifestPath);
        string originalLua = File.ReadAllText(luaPath);
        try
        {
            var json = JObject.Parse(originalManifest);
            ((JObject)json["script"])["enabled"] = true;
            File.WriteAllText(manifestPath, json.ToString(Newtonsoft.Json.Formatting.None));
            // Cache the bad source so validation reaches MoonSharp compilation instead of only stale-cache detection.
            File.WriteAllText(luaPath, originalLua + "\nfunction (\n");
            ImportNow(manifestPath);
            ImportNow(luaPath);
            LevelPackageTools.RefreshGeneratedDefinitions();
            Require(!LevelPackageTools.ValidatePackage(definition, out _), "Lua syntax error was accepted for an enabled manifest.");
            bool threw = false;
            try { new LevelPackageBuildGuard().OnPreprocessBuild(null); }
            catch (BuildFailedException) { threw = true; }
            Require(threw, "Build guard accepted an enabled Lua syntax error.");
        }
        finally
        {
            File.WriteAllText(manifestPath, originalManifest);
            File.WriteAllText(luaPath, originalLua);
            ImportNow(manifestPath);
            ImportNow(luaPath);
            LevelPackageTools.RefreshGeneratedDefinitions();
        }
    }

    static void ImportNow(string path) => AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

    static Dictionary<string, string> HashPackageFiles()
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            string normalized = path.Replace('\\', '/');
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                hashes[normalized] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        return hashes;
    }

    static bool HashesEqual(Dictionary<string, string> left, Dictionary<string, string> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
            if (!right.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
        return true;
    }

    static void Require(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }

    static void WriteReport(string state, List<string> report)
    {
        File.WriteAllText(Report, state + "\n" + string.Join("\n", report));
    }
}
