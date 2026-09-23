using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HMProtection.Core;
using HMProtection.Entities;
using HMProtection.EntityAdapters;
using HMProtection.Quiz;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Explicit, idempotent migration utility for an isolated Office entity-system pilot.
/// It never runs at editor load and never writes the production office scene.
/// </summary>
public static class EntitySetup
{
    private const string ProductionOfficePath = "Assets/Scenes/办公室场景.unity";
    private const string PilotOfficePath = "Assets/Scenes/办公室场景-实体试点.unity";
    private const string PilotRootName = "Entity System Pilot";

    [MenuItem("Tools/Entity System/Create Office Pilot and Install")]
    public static void CreateOfficePilotAndInstall()
    {
        try
        {
            EnsureNoDirtyLoadedScenes();
            if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(PilotOfficePath))
            {
                if (!AssetDatabase.CopyAsset(ProductionOfficePath, PilotOfficePath))
                    throw new InvalidOperationException("Could not create the office pilot scene from " + ProductionOfficePath + ".");
                AssetDatabase.SaveAssets();
            }
            EditorSceneManager.OpenScene(PilotOfficePath, OpenSceneMode.Single);
            InstallOfficePilotInternal();
            Debug.Log("[EntitySetup] Office pilot installed: " + PilotOfficePath);
        }
        catch (Exception exception)
        {
            Debug.LogError("[EntitySetup] " + exception.Message);
            throw;
        }
    }

    [MenuItem("Tools/Entity System/Install Current Office Pilot")]
    public static void InstallCurrentOfficePilot()
    {
        try
        {
            EnsureNoDirtyLoadedScenes();
            if (SceneManager.GetActiveScene().path != PilotOfficePath)
                throw new InvalidOperationException("Open the isolated pilot scene first: " + PilotOfficePath);
            InstallOfficePilotInternal();
            Debug.Log("[EntitySetup] Office pilot refreshed.");
        }
        catch (Exception exception)
        {
            Debug.LogError("[EntitySetup] " + exception.Message);
            throw;
        }
    }

    /// <summary>Batch-mode entry point: creates the pilot copy if necessary, installs it, validates it and saves only that pilot.</summary>
    public static void InstallOfficePilotBatch() => CreateOfficePilotAndInstall();

    private static void InstallOfficePilotInternal()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != PilotOfficePath) throw new InvalidOperationException("Entity installation is restricted to the isolated office pilot.");
        if (scene.isDirty) throw new InvalidOperationException("The office pilot has unsaved changes. Save or revert it before running EntitySetup.");

        var player = FindSingle<body>(scene, "player body");
        var actor = player.GetComponent<Interactor>();
        if (actor == null) throw new InvalidOperationException("The player body has no Interactor component.");
        var fire = FindSingle<FireEffectController>(scene, "fire controller");
        var flow = FindSingle<OfficeFireChoiceFlow>(scene, "office question flow");
        if (flow.presenter == null || flow.guidance == null) throw new InvalidOperationException("OfficeFireChoiceFlow needs explicit presenter and guidance references.");
        var bootstrapper = FindSingle<LevelBootstrapper>(scene, "level bootstrapper");

        Transform pilotRoot = FindOrCreateRoot(scene, PilotRootName).transform;
        var bindingSet = GetOrAdd<SceneBindingSet>(pilotRoot.gameObject);
        var host = GetOrAdd<EntityScopeHost>(pilotRoot.gameObject);
        var bridge = GetOrAdd<OfficeEntityBindings>(pilotRoot.gameObject);

        GameEntity playerEntity = ConfigureEntity(player.gameObject, "player.main", new[] { "player", "actor" });
        var spawnTransform = FindOrCreateChild(pilotRoot, "Spawn Anchor");
        var bootSerialized = new SerializedObject(bootstrapper);
        var spawnPath = bootSerialized.FindProperty("spawnAnchorPath")?.stringValue;
        var configuredSpawn = bootstrapper.ResolvePath(spawnPath);
        if (configuredSpawn == null)
            throw new InvalidOperationException("LevelBootstrapper spawnAnchorPath does not resolve in the pilot: " + spawnPath);
        spawnTransform.SetPositionAndRotation(configuredSpawn.position, configuredSpawn.rotation);
        GameEntity spawnEntity = ConfigureAnchoredEntity(spawnTransform.gameObject, "anchor.player.spawn", new[] { "anchor", "spawn" },
            MakeSlot("origin", spawnTransform));

        GameEntity fireEntity = ConfigureEntity(fire.gameObject, "fire.office.main", new[] { "fire", "hazard" });
        var fireAnchor = GetOrAdd<AnchorCapability>(fire.gameObject);
        fireAnchor.Configure(new[] { MakeSlot("origin", fire.transform) });
        var fireCapability = GetOrAdd<FireCapabilityAdapter>(fire.gameObject);
        fireCapability.Configure(fire);
        fireEntity.Configure("fire.office.main", new[] { "fire", "hazard" }, fire.gameObject, new EntityCapability[] { fireAnchor, fireCapability });

        var targets = CollectTargets(flow);
        var configuredTargets = new List<GameEntity>();
        var proxies = new List<EntityHitProxy>();
        var bridgeTargets = new List<OfficeEntityBindings.TargetBinding>();
        var managedColliders = new List<Collider>();
        foreach (var mapping in targets)
        {
            var target = mapping.Target;
            var targetEntity = ConfigureEntity(target.gameObject, mapping.EntityId, new[] { "interactable", "office_target" });
            var anchor = GetOrAdd<AnchorCapability>(target.gameObject);
            anchor.Configure(new[] { MakeSlot("approach", mapping.Approach) });
            var capability = GetOrAdd<OfficeChoiceCapabilityAdapter>(target.gameObject);
            capability.Configure(target, bridge);
            targetEntity.Configure(mapping.EntityId, new[] { "interactable", "office_target" }, target.gameObject,
                new EntityCapability[] { anchor, capability });
            var collider = target.GetComponent<BoxCollider>();
            if (collider == null) throw new InvalidOperationException("Office target '" + mapping.OptionId + "' has no BoxCollider.");
            var proxy = GetOrAdd<EntityHitProxy>(target.gameObject);
            proxy.Configure(targetEntity, new Collider[] { collider });
            configuredTargets.Add(targetEntity);
            proxies.Add(proxy);
            managedColliders.Add(collider);
            bridgeTargets.Add(new OfficeEntityBindings.TargetBinding
            {
                targetId = mapping.OptionId, entityId = mapping.EntityId, slot = "approach", label = mapping.Label
            });
        }

        var bridgeAnchors = ConfigureSceneChoiceAnchors(scene);
        var contentRoots = new List<Transform> { playerEntity.transform, spawnEntity.transform, fireEntity.transform };
        contentRoots.AddRange(configuredTargets.Select(item => item.transform));
        contentRoots.AddRange(bridgeAnchors.Entities.Select(item => item.transform));
        host.Configure("level_1_initial_fire", contentRoots, bindingSet, proxies, false);
        bindingSet.Configure(new[]
        {
            new SceneEntityBinding("player", "player.main"),
            new SceneEntityBinding("spawn", "anchor.player.spawn", true, "anchor", "origin"),
            new SceneEntityBinding("primaryFire", "fire.office.main", true, "fire", "origin")
        });

        bridge.Configure(host, player, actor, flow, flow.guidance, bridgeTargets.ToArray(), bridgeAnchors.Bindings.ToArray());
        bridge.managedColliders = managedColliders.ToArray();
        flow.entityBindings = bridge;
        flow.presenter.entityBindings = bridge;
        actor.entityBindings = bridge;
        bootstrapper.entityBindings = bridge;
        bridge.runner = bootstrapper.GetComponent<LevelFlowRunner>();

        if (!bridge.ValidateReferences(out var bridgeError))
            throw new InvalidOperationException("Office entity bridge validation failed: " + bridgeError);
        var validationFindings = HMProtection.Entities.Editor.EntitySceneValidator.Validate(scene);
        if (HMProtection.Entities.Editor.EntitySceneValidator.HasErrors(validationFindings))
            throw new InvalidOperationException("Entity pilot validation failed:\n" + string.Join("\n", validationFindings
                .Where(finding => finding.Severity == MessageType.Error)
                .Select(finding => finding.Message)));
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Unity did not save the office pilot scene.");
    }

    private static (List<GameEntity> Entities, List<OfficeEntityBindings.AnchorBinding> Bindings) ConfigureSceneChoiceAnchors(Scene scene)
    {
        var entities = new List<GameEntity>();
        var bindings = new List<OfficeEntityBindings.AnchorBinding>();
        foreach (var source in FindAll<SceneChoiceAnchor>(scene).OrderBy(anchor => anchor.anchorId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(source.anchorId)) throw new InvalidOperationException("SceneChoiceAnchor has an empty anchorId.");
            string entityId = "anchor.scene_choice." + NormalizeId(source.anchorId);
            var entity = ConfigureAnchoredEntity(source.gameObject, entityId, new[] { "anchor", "scene_choice" },
                MakeSlot("origin", source.transform));
            entities.Add(entity);
            bindings.Add(new OfficeEntityBindings.AnchorBinding { anchorId = source.anchorId, entityId = entityId, slot = "origin" });
        }
        return (entities, bindings);
    }

    private static List<TargetMapping> CollectTargets(OfficeFireChoiceFlow flow)
    {
        var result = new List<TargetMapping>();
        foreach (var target in FindAll<OfficeChoiceTarget>(flow.gameObject.scene))
        {
            if (!OfficeTargetIds.TryGetValue(target.optionId, out var entityId)) continue;
            var destination = flow.destinations?.FirstOrDefault(item => item != null && item.optionId == target.optionId);
            if (destination == null || destination.approach == null)
                throw new InvalidOperationException("Office target '" + target.optionId + "' has no explicit approach destination.");
            result.Add(new TargetMapping(target.optionId, entityId, destination.label, target, destination.approach));
        }
        if (result.Count != OfficeTargetIds.Count || result.Select(item => item.OptionId).Distinct(StringComparer.Ordinal).Count() != OfficeTargetIds.Count)
            throw new InvalidOperationException("Expected exactly four OfficeChoiceTarget objects: " + string.Join(", ", OfficeTargetIds.Keys));
        return result.OrderBy(item => item.EntityId, StringComparer.Ordinal).ToList();
    }

    private static readonly IReadOnlyDictionary<string, string> OfficeTargetIds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "rescue_documents", "target.office.printer" },
        { "open_window", "target.office.window" },
        { "raise_alarm", "target.office.entrance" },
        { "unplug_strip", "target.office.power_strip" }
    };

    private sealed class TargetMapping
    {
        public readonly string OptionId, EntityId, Label;
        public readonly OfficeChoiceTarget Target;
        public readonly Transform Approach;
        public TargetMapping(string optionId, string entityId, string label, OfficeChoiceTarget target, Transform approach)
        { OptionId = optionId; EntityId = entityId; Label = label; Target = target; Approach = approach; }
    }

    private static GameEntity ConfigureAnchoredEntity(GameObject gameObject, string id, IEnumerable<string> tags, params AnchorSlot[] slots)
    {
        var entity = ConfigureEntity(gameObject, id, tags);
        var anchor = GetOrAdd<AnchorCapability>(gameObject);
        anchor.Configure(slots);
        entity.Configure(id, tags, gameObject, new EntityCapability[] { anchor });
        return entity;
    }

    private static GameEntity ConfigureEntity(GameObject gameObject, string id, IEnumerable<string> tags)
    {
        var entity = GetOrAdd<GameEntity>(gameObject);
        entity.Configure(id, tags, gameObject, entity.Capabilities);
        return entity;
    }

    private static AnchorSlot MakeSlot(string name, Transform anchor)
    {
        var slot = new AnchorSlot();
        slot.Configure(name, anchor);
        return slot;
    }

    private static Transform FindOrCreateChild(Transform parent, string name)
    {
        var child = parent.Find(name);
        if (child != null) return child;
        return new GameObject(name).transform.SetParentAndReturn(parent);
    }

    private static GameObject FindOrCreateRoot(Scene scene, string name)
    {
        var found = scene.GetRootGameObjects().FirstOrDefault(item => item.name == name);
        if (found != null) return found;
        var created = new GameObject(name);
        SceneManager.MoveGameObjectToScene(created, scene);
        return created;
    }

    private static T FindSingle<T>(Scene scene, string description) where T : Component
    {
        var all = FindAll<T>(scene).ToArray();
        if (all.Length != 1) throw new InvalidOperationException("Expected exactly one " + description + ", found " + all.Length + ".");
        return all[0];
    }

    private static IEnumerable<T> FindAll<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>(true));
    private static T GetOrAdd<T>(GameObject gameObject) where T : Component => gameObject.GetComponent<T>() ?? gameObject.AddComponent<T>();
    private static string NormalizeId(string value) => new string(value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '.').ToArray()).Trim('.');

    private static void EnsureNoDirtyLoadedScenes()
    {
        for (int index = 0; index < SceneManager.sceneCount; index++)
        {
            var scene = SceneManager.GetSceneAt(index);
            if (scene.isDirty) throw new InvalidOperationException("Loaded scene has unsaved changes: " + scene.path + ". EntitySetup will not save it automatically.");
        }
    }

    private static Transform SetParentAndReturn(this Transform child, Transform parent) { child.SetParent(parent, false); return child; }
}
