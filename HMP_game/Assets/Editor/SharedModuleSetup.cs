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
/// Explicit installer for the third-scene shared-module pilot. It only writes the copied scene and
/// two definition assets; production Scene 3 is never opened for modification.
/// </summary>
public static class SharedModuleSetup
{
    private const string SourceScene = "Assets/Scenes/第三场景.unity";
    private const string PilotScene = "Assets/Scenes/第三场景-通用模块试点.unity";
    private const string DefinitionsFolder = "Assets/GameContent/LevelDefinitions";
    private const string PilotRootName = "Shared Module Pilot";
    private const string SecondFireName = "Fire B - Common Module Pilot";

    [MenuItem("Tools/Shared Modules/Create Third-Scene Pilot and Install")]
    public static void CreateThirdScenePilotAndInstall()
    {
        EnsureNoDirtyLoadedScenes();
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(PilotScene) == null)
        {
            if (!AssetDatabase.CopyAsset(SourceScene, PilotScene))
                throw new InvalidOperationException("Could not create pilot scene from " + SourceScene + ".");
            AssetDatabase.SaveAssets();
        }
        EditorSceneManager.OpenScene(PilotScene, OpenSceneMode.Single);
        InstallCurrentThirdScenePilot();
    }

    [MenuItem("Tools/Shared Modules/Install Current Third-Scene Pilot")]
    public static void InstallCurrentThirdScenePilot()
    {
        EnsureNoDirtyLoadedScenes();
        var scene = SceneManager.GetActiveScene();
        if (scene.path != PilotScene)
            throw new InvalidOperationException("Shared-module installation is restricted to " + PilotScene + ".");

        RemoveOfficeSpecificComponents(scene);

        var player = FindSingle<body>(scene, "player body");
        var actor = player.GetComponent<Interactor>();
        var bootstrapper = FindSingle<LevelBootstrapper>(scene, "level bootstrapper");
        if (actor == null) throw new InvalidOperationException("Player body has no Interactor.");

        var pilotRoot = FindOrCreateRoot(scene, PilotRootName);
        var bindings = GetOrAdd<SceneBindingSet>(pilotRoot);
        var scopeHost = GetOrAdd<EntityScopeHost>(pilotRoot);
        var sessionHost = GetOrAdd<LevelSessionHost>(pilotRoot);
        var bridge = GetOrAdd<EntityInteractionBridge>(pilotRoot);

        var contentRoots = new List<Transform>();
        var proxies = new List<EntityHitProxy>();
        var claimedColliders = new HashSet<Collider>();

        var playerEntity = ConfigureEntity(player.gameObject, "player.main", new[] { "player", "actor" });
        contentRoots.Add(playerEntity.transform);
        var spawn = FindOrCreateChild(pilotRoot.transform, "Spawn Anchor");
        spawn.SetPositionAndRotation(player.transform.position, player.transform.rotation);
        var spawnEntity = ConfigureAnchoredEntity(spawn.gameObject, "anchor.player.spawn", new[] { "anchor", "spawn" }, MakeSlot("origin", spawn));
        contentRoots.Add(spawnEntity.transform);
        var seatAnchor = EnsurePilotSeat(scene, player.transform);
        var seatAnchorEntity = ConfigureAnchoredEntity(seatAnchor.gameObject, "anchor.seat.third.test", new[] { "anchor", "seat_anchor" },
            MakeSlot("origin", seatAnchor));
        contentRoots.Add(seatAnchorEntity.transform);

        ConfigureInteractables(scene, bridge, contentRoots, proxies, claimedColliders);
        var fires = ConfigureFires(scene, pilotRoot.transform, sessionHost, contentRoots);
        ConfigureSpray(scene, fires);

        scopeHost.Configure("third_scene_shared_modules", contentRoots, bindings, proxies, false);
        bindings.Configure(new[]
        {
            new SceneEntityBinding("player", "player.main"),
            new SceneEntityBinding("spawn", "anchor.player.spawn", true, "anchor", "origin"),
            new SceneEntityBinding("primaryFire", "fire.third.a", true, "fire", "origin"),
            new SceneEntityBinding("secondaryFire", "fire.third.b", true, "fire", "origin")
        });
        bridge.Configure(scopeHost, playerEntity, actor, sessionHost);
        actor.entityBindings = bridge;
        EditorUtility.SetDirty(actor);

        var firstDefinition = CreateOrUpdateDefinition("third_scene_shared_modules_a", "Third Scene Shared Modules A");
        var secondDefinition = CreateOrUpdateDefinition("third_scene_shared_modules_b", "Third Scene Shared Modules B");
        bootstrapper.definition = firstDefinition;
        bootstrapper.entityBindings = bridge;
        ConfigureBootstrapperForSharedPilot(bootstrapper);
        EditorUtility.SetDirty(bootstrapper);
        EditorUtility.SetDirty(pilotRoot);

        ValidatePilot(scene, bridge, scopeHost, fires, firstDefinition, secondDefinition);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[SharedModuleSetup] Installed isolated pilot: " + PilotScene);
    }

    /// <summary>Batch-mode entry point. It intentionally never edits the source third scene.</summary>
    public static void InstallThirdScenePilotBatch() => CreateThirdScenePilotAndInstall();

    private static void ConfigureInteractables(Scene scene, EntityInteractionBridge bridge, List<Transform> roots,
        List<EntityHitProxy> proxies, HashSet<Collider> claimed)
    {
        ConfigureGroup(FindAll<DoorController>(scene).OrderBy(item => item.name, StringComparer.Ordinal), "door", "door", bridge, roots, proxies, claimed,
            (component, source) => { var adapter = GetOrAdd<DoorCapabilityAdapter>(component.gameObject); adapter.Configure(component, source); return adapter; });
        ConfigureGroup(FindAll<SeatController>(scene).OrderBy(item => item.name, StringComparer.Ordinal), "seat", "seat", bridge, roots, proxies, claimed,
            (component, source) => { var adapter = GetOrAdd<SeatCapabilityAdapter>(component.gameObject); adapter.Configure(component, source); return adapter; });
        ConfigureGroup(FindAll<PickupItem>(scene).OrderBy(item => item.name, StringComparer.Ordinal), "pickup", "pickup", bridge, roots, proxies, claimed,
            (component, source) => { var adapter = GetOrAdd<PickupCapabilityAdapter>(component.gameObject); adapter.Configure(component, source); return adapter; });
    }

    // The source scene may contain remnants from an earlier office experiment. They are not part of
    // this pilot's definition and must not keep office-only flows alive in the copied scene.
    private static void RemoveOfficeSpecificComponents(Scene scene)
    {
        foreach (var component in FindOfficeSpecificComponents(scene).ToArray()) UnityEngine.Object.DestroyImmediate(component);
    }

    private static IEnumerable<Component> FindOfficeSpecificComponents(Scene scene)
    {
        return FindAll<OfficeFireChoiceFlow>(scene).Cast<Component>()
            .Concat(FindAll<OfficeEntityBindings>(scene).Cast<Component>())
            .Concat(FindAll<OfficeChoiceTarget>(scene).Cast<Component>())
            .Concat(FindAll<OfficeChoiceInteraction>(scene).Cast<Component>())
            .Concat(FindAll<SceneChoicePresenter>(scene).Cast<Component>())
            .Concat(FindAll<QuizViewController>(scene).Cast<Component>())
            .Concat(FindAll<QuizTimerHUD>(scene).Cast<Component>())
            .Concat(FindAll<QuizResultFeedback>(scene).Cast<Component>())
            .Concat(FindAll<InstructorLessonView>(scene).Cast<Component>())
            .Concat(FindAll<InstructorLessonLayout>(scene).Cast<Component>());
    }

    private static void ConfigureGroup<T>(IEnumerable<T> components, string prefix, string tag, EntityInteractionBridge bridge,
        List<Transform> roots, List<EntityHitProxy> proxies, HashSet<Collider> claimed,
        Func<T, EntityInteractionBridge, EntityCapability> makeCapability) where T : Component
    {
        var index = 0;
        foreach (var component in components)
        {
            var id = prefix + ".third." + (++index).ToString("D2");
            var capability = makeCapability(component, bridge);
            var entity = ConfigureEntity(component.gameObject, id, new[] { "interactable", tag }, capability);
            var collider = FindUnclaimedCollider(component, claimed);
            if (collider == null)
                throw new InvalidOperationException("Required " + prefix + " '" + component.name + "' has no unclaimed collider for entity hit routing.");
            claimed.Add(collider);
            var proxy = GetOrAdd<EntityHitProxy>(component.gameObject);
            proxy.Configure(entity, new[] { collider });
            roots.Add(entity.transform);
            proxies.Add(proxy);
        }
        if (index == 0) throw new InvalidOperationException("Third-scene pilot requires at least one " + prefix + " component.");
    }

    private static FireSuppression[] ConfigureFires(Scene scene, Transform pilotRoot, LevelSessionHost sessionHost, List<Transform> roots)
    {
        var first = FindAll<FireEffectController>(scene).FirstOrDefault(item => item.gameObject.name != SecondFireName);
        if (first == null) throw new InvalidOperationException("Third scene has no source FireEffectController.");
        var second = FindAll<FireEffectController>(scene).FirstOrDefault(item => item.gameObject.name == SecondFireName);
        if (second == null)
        {
            var clone = UnityEngine.Object.Instantiate(first.gameObject, first.transform.position + first.transform.right * 2f, first.transform.rotation, pilotRoot);
            clone.name = SecondFireName;
            second = clone.GetComponent<FireEffectController>();
            if (second == null) throw new InvalidOperationException("Copied fire instance has no FireEffectController.");
        }

        var controllers = new[] { first, second };
        var result = new FireSuppression[controllers.Length];
        for (var index = 0; index < controllers.Length; index++)
        {
            var controller = controllers[index];
            // The copied controller otherwise retains the source followTarget and both fires collapse
            // onto one prop in LateUpdate. The controller transform itself is the explicit fire anchor.
            SetObjectReference(controller, "followTarget", null);
            SetBoolean(controller, "debugMode", false);
            controller.sessionHost = sessionHost;
            var suppression = GetOrAdd<FireSuppression>(controller.gameObject);
            suppression.Configure(controller);
            var anchor = GetOrAdd<AnchorCapability>(controller.gameObject);
            anchor.Configure(new[] { MakeSlot("origin", controller.transform) });
            var adapter = GetOrAdd<FireCapabilityAdapter>(controller.gameObject);
            adapter.Configure(controller);
            var entity = ConfigureEntity(controller.gameObject, index == 0 ? "fire.third.a" : "fire.third.b",
                new[] { "fire", "hazard" }, anchor, adapter);
            roots.Add(entity.transform);
            result[index] = suppression;
            EditorUtility.SetDirty(controller);
        }
        return result;
    }

    /// <summary>Scene 3 has no authored SeatController. Create a visible, isolated test chair only in the pilot.</summary>
    private static Transform EnsurePilotSeat(Scene scene, Transform player)
    {
        var existing = scene.GetRootGameObjects().FirstOrDefault(item => item.name == "Shared Module Test Seat");
        GameObject seat;
        if (existing == null)
        {
            seat = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seat.name = "Shared Module Test Seat";
            SceneManager.MoveGameObjectToScene(seat, scene);
            seat.transform.position = player.position + player.forward * 1.5f + Vector3.up * .25f;
            seat.transform.rotation = Quaternion.Euler(0f, player.eulerAngles.y + 180f, 0f);
            seat.transform.localScale = new Vector3(.55f, .45f, .55f);
            var back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            back.name = "Backrest";
            back.transform.SetParent(seat.transform, false);
            back.transform.localPosition = new Vector3(0f, .55f, -.2f);
            back.transform.localScale = new Vector3(1f, 1.2f, .18f);
        }
        else seat = existing;
        GetOrAdd<SeatController>(seat);
        var anchor = FindOrCreateChild(seat.transform, "Seat Anchor");
        anchor.localPosition = new Vector3(0f, .25f, 0f);
        anchor.localRotation = Quaternion.identity;
        return anchor;
    }

    private static void ConfigureBootstrapperForSharedPilot(LevelBootstrapper bootstrapper)
    {
        var serialized = new SerializedObject(bootstrapper);
        serialized.FindProperty("autoStart").boolValue = true;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        // This pilot has an explicit non-legacy definition. Existing Scene 3 components remain
        // serialized for the source scene, but must not compete for timer/fire/input ownership here.
        var runner = bootstrapper.GetComponent<LevelFlowRunner>();
        if (runner != null) runner.enabled = false;
        var timer = bootstrapper.GetComponent<StageTimer>();
        if (timer != null) timer.enabled = false;
    }

    private static void SetObjectReference(UnityEngine.Object target, string property, UnityEngine.Object value)
    {
        var serialized = new SerializedObject(target);
        var field = serialized.FindProperty(property);
        if (field == null) throw new InvalidOperationException(target.GetType().Name + " is missing serialized field " + property + ".");
        field.objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetBoolean(UnityEngine.Object target, string property, bool value)
    {
        var serialized = new SerializedObject(target);
        var field = serialized.FindProperty(property);
        if (field == null) throw new InvalidOperationException(target.GetType().Name + " is missing serialized field " + property + ".");
        field.boolValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void ConfigureSpray(Scene scene, FireSuppression[] fires)
    {
        var spray = FindSingle<ExtinguisherSpray>(scene, "extinguisher spray");
        spray.ConfigureTargets(fires[0], fires);
        EditorUtility.SetDirty(spray);
    }

    private static LevelDefinition CreateOrUpdateDefinition(string levelId, string displayName)
    {
        if (!AssetDatabase.IsValidFolder("Assets/GameContent")) AssetDatabase.CreateFolder("Assets", "GameContent");
        if (!AssetDatabase.IsValidFolder(DefinitionsFolder)) AssetDatabase.CreateFolder("Assets/GameContent", "LevelDefinitions");
        var path = DefinitionsFolder + "/" + levelId + ".asset";
        var definition = AssetDatabase.LoadAssetAtPath<LevelDefinition>(path);
        if (definition == null)
        {
            definition = ScriptableObject.CreateInstance<LevelDefinition>();
            AssetDatabase.CreateAsset(definition, path);
        }
        definition.levelId = levelId;
        definition.displayName = displayName;
        definition.scenePath = PilotScene;
        definition.runLegacyStages = false;
        definition.legacyConfigPath = null;
        bool alternate = levelId.EndsWith("_b", StringComparison.Ordinal);
        definition.initialFires = new[]
        {
            new LevelDefinition.FireInitialState { entityId = "fire.third.a", state = alternate ? HMProtection.Modules.Fire.FireState.Large : HMProtection.Modules.Fire.FireState.Small },
            new LevelDefinition.FireInitialState { entityId = "fire.third.b", state = alternate ? HMProtection.Modules.Fire.FireState.SmokeOnly : HMProtection.Modules.Fire.FireState.Medium }
        };
        EditorUtility.SetDirty(definition);
        return definition;
    }

    private static void ValidatePilot(Scene scene, EntityInteractionBridge bridge, EntityScopeHost scopeHost,
        FireSuppression[] fires, LevelDefinition firstDefinition, LevelDefinition secondDefinition)
    {
        var officeComponents = FindOfficeSpecificComponents(scene).Select(component => component.GetType().Name + " on '" + component.gameObject.name + "'").ToArray();
        if (officeComponents.Length > 0)
            throw new InvalidOperationException("Shared-module pilot retains Office/quiz-specific components: " + string.Join(", ", officeComponents));
        if (fires.Length != 2 || fires.Any(item => item == null)) throw new InvalidOperationException("Pilot requires two explicit suppression sources.");
        if (firstDefinition == null || secondDefinition == null || firstDefinition.levelId == secondDefinition.levelId)
            throw new InvalidOperationException("Pilot requires two distinct LevelDefinition assets for the same scene.");
        if (bridge == null || scopeHost == null) throw new InvalidOperationException("Pilot bridge or scope host is missing.");
        if (!scopeHost.Validate(out var errors)) throw new InvalidOperationException("Entity validation failed: " + string.Join("\n", errors));
    }

    private static GameEntity ConfigureAnchoredEntity(GameObject gameObject, string id, IEnumerable<string> tags, params AnchorSlot[] slots)
    {
        var anchor = GetOrAdd<AnchorCapability>(gameObject);
        anchor.Configure(slots);
        return ConfigureEntity(gameObject, id, tags, anchor);
    }

    private static GameEntity ConfigureEntity(GameObject gameObject, string id, IEnumerable<string> tags, params EntityCapability[] capabilities)
    {
        var entity = GetOrAdd<GameEntity>(gameObject);
        IEnumerable<EntityCapability> configured = capabilities != null && capabilities.Length > 0 ? capabilities : entity.Capabilities;
        entity.Configure(id, tags, gameObject, configured);
        return entity;
    }

    private static AnchorSlot MakeSlot(string name, Transform transform)
    {
        var slot = new AnchorSlot();
        slot.Configure(name, transform);
        return slot;
    }

    private static Collider FindUnclaimedCollider(Component component, HashSet<Collider> claimed)
    {
        return component.GetComponentsInChildren<Collider>(true).FirstOrDefault(item => item != null && !claimed.Contains(item))
            ?? component.GetComponentsInParent<Collider>(true).FirstOrDefault(item => item != null && !claimed.Contains(item));
    }

    private static GameObject FindOrCreateRoot(Scene scene, string name)
    {
        var root = scene.GetRootGameObjects().FirstOrDefault(item => item.name == name);
        if (root != null) return root;
        root = new GameObject(name);
        SceneManager.MoveGameObjectToScene(root, scene);
        return root;
    }
    private static Transform FindOrCreateChild(Transform parent, string name)
    {
        var child = parent.Find(name);
        if (child != null) return child;
        var created = new GameObject(name).transform;
        created.SetParent(parent, false);
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
    private static void EnsureNoDirtyLoadedScenes()
    {
        for (var index = 0; index < SceneManager.sceneCount; index++)
        {
            var scene = SceneManager.GetSceneAt(index);
            if (scene.isDirty) throw new InvalidOperationException("Loaded scene has unsaved changes: " + scene.path);
        }
    }
}
