using System;
using System.Linq;
using HMProtection.Entities;
using HMProtection.EntityAdapters;
using HMProtection.Quiz;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Explicit, repeatable migration from the retained authoring scene to the shipped lesson.</summary>
public static class OfficePowerStripRepair
{
    const string Source = "Assets/Scenes/办公室场景-实体试点.unity";
    const string Destination = "Assets/Levels/level_1_initial_fire/Scenes/level_1_initial_fire.unity";
    const string StripName = "SM_Hazard_OverloadSocket_01_Aged";

    public static void RunAndCheckBatch()
    {
        Run();
        OfficeLuaMigrationChecks.BeginBatch();
    }

    [MenuItem("Tools/Levels/Sync Power Strip From Entity Pilot")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode before migrating.");
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save open scenes before migrating.");
        var setup = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            var source = EditorSceneManager.OpenScene(Source, OpenSceneMode.Single);
            var original = Components<Transform>(source).Single(t => t.name == StripName);
            var position = original.position;
            var rotation = original.rotation;
            var scale = original.localScale;
            var scene = EditorSceneManager.OpenScene(Destination, OpenSceneMode.Single);
            var strip = Components<Transform>(scene).Single(t => t.name == StripName);
            strip.SetPositionAndRotation(position, rotation);
            strip.localScale = scale;
            PrefabUtility.RecordPrefabInstancePropertyModifications(strip);

            var target = Components<OfficeChoiceTarget>(scene).Single(t => t.optionId == "unplug_strip");
            var duplicate = Components<Transform>(scene).Single(t => t.name == "SM_Choice_PowerStrip");
            foreach (var renderer in duplicate.GetComponentsInChildren<Renderer>(true))
            { renderer.enabled = false; PrefabUtility.RecordPrefabInstancePropertyModifications(renderer); }
            foreach (var collider in duplicate.GetComponentsInChildren<Collider>(true))
                if (collider != target.GetComponent<BoxCollider>())
                { collider.enabled = false; PrefabUtility.RecordPrefabInstancePropertyModifications(collider); }
            target.visualTarget = strip;
            target.SyncVisualBounds();
            var colliders = strip.GetComponentsInChildren<Collider>(true)
                .Concat(new Collider[] { target.GetComponent<BoxCollider>() }).Distinct().ToArray();
            int layer = LayerMask.NameToLayer("Interactable");
            if (layer < 0) throw new InvalidOperationException("Interactable layer is missing.");
            foreach (var collider in colliders)
            {
                collider.gameObject.layer = layer;
                PrefabUtility.RecordPrefabInstancePropertyModifications(collider.gameObject);
            }
            var proxy = target.GetComponent<EntityHitProxy>();
            if (proxy == null) throw new InvalidOperationException("Expected existing entity hit proxy.");
            proxy.Configure(target.GetComponent<GameEntity>(), colliders);
            var bindings = Components<OfficeEntityBindings>(scene).Single();
            bindings.managedColliders = bindings.managedColliders.Concat(colliders).Distinct().ToArray();

            // Keep the existing entity and anchor identities; only move their transforms/references.
            var anchor = Components<SceneChoiceAnchor>(scene).Single(a => a.anchorId == "office.fire.power_strip");
            var capability = target.GetComponent<AnchorCapability>();
            var approach = strip.Find("InteractionApproach");
            if (approach == null)
            {
                var oldSlot = new SerializedObject(capability).FindProperty("slots").GetArrayElementAtIndex(0).FindPropertyRelative("anchor");
                var oldApproach = (Transform)oldSlot.objectReferenceValue;
                var direction = oldApproach.position - anchor.transform.position;
                direction.y = 0;
                if (direction.sqrMagnitude < .01f) direction = Vector3.back;
                var point = target.transform.position + direction.normalized * 1.1f;
                point.y = oldApproach.position.y;
                approach = new GameObject("InteractionApproach").transform;
                approach.SetParent(strip, true);
                approach.position = point;
            }
            anchor.transform.SetParent(strip, true);
            anchor.transform.position = target.transform.position;
            var slot = new AnchorSlot(); slot.Configure("approach", approach);
            capability.Configure(new[] { slot });
            foreach (var destination in bindings.flow.destinations)
                if (destination.optionId == "unplug_strip") destination.approach = approach;
            if (!bindings.host.Validate(out var errors))
                throw new InvalidOperationException("Invalid scene entity bindings: " + string.Join("; ", errors));
            if (Vector3.Distance(strip.position, position) > .001f || target.GetComponent<BoxCollider>().size.sqrMagnitude <= 0f)
                throw new InvalidOperationException("Power-strip pose or interaction bounds failed validation.");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("Power strip migrated to official first level at " + position + "; visual, interaction and guidance share the same prop.");
        }
        finally
        {
            if (setup.Length > 0 && setup.All(item => !string.IsNullOrEmpty(item.path)))
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }

    static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects()
        .SelectMany(root => root.GetComponentsInChildren<T>(true)).ToArray();
}
