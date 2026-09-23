using System;
using HMProtection.EntityAdapters;
using HMProtection.LevelPackages;
using HMProtection.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Assign the packaged lesson to the existing menu, preserving its CG flow.</summary>
public static class OfficeLuaMigrationSetup
{
    public const string DefinitionPath = "Assets/Levels/level_1_initial_fire/Runtime/level_1_initial_fire.asset";
    public const string MenuPath = "Assets/Scenes/初始界面.unity";
    [MenuItem("Tools/Levels/Install Office Lua Entry")]
    public static void Install()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode first.");
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save loaded scenes before installing the entry.");
        LevelPackageTools.RefreshGeneratedDefinitions();
        var definition = AssetDatabase.LoadAssetAtPath<LevelDefinition>(DefinitionPath);
        if (definition == null || !definition.scriptEnabled || definition.runLegacyStages || definition.scriptConfig == null)
            throw new InvalidOperationException("Office package must use Lua with a script config and no legacy driver.");
        var layout = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            var scene = EditorSceneManager.OpenScene(MenuPath, OpenSceneMode.Single);
            int count = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var select in root.GetComponentsInChildren<SceneSelectUI>(true))
                { select.officeDefinition = definition; EditorUtility.SetDirty(select); count++; }
            if (count != 1) throw new InvalidOperationException("Expected exactly one office scene selector.");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
        finally
        {
            bool hasActive = false;
            foreach (var item in layout) if (item.isLoaded && item.isActive) hasActive = true;
            if (hasActive) EditorSceneManager.RestoreSceneManagerSetup(layout);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        LevelPackageTools.ValidateAll();
        Debug.Log("Office packaged Lua entry installed.");
    }
}
