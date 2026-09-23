using System;
using System.Collections;
using System.Collections.Generic;
using HMProtection.EntityAdapters;
using HMProtection.Modules.Fire;
using UnityEngine;

/// <summary>Physics-level checks using ExtinguisherSpray's actual root-band sampler.</summary>
public static class SharedFirePhysicsChecks
{
    public static IEnumerator Run(EntityInteractionBridge bridge, Action<string> report)
    {
        var created = new List<GameObject>();
        try
        {
            Vector3 offset = new Vector3(0f, 200f, 0f);
            FireSuppression near = CreateFire("physics-fire-near", offset + new Vector3(0f, 0f, 2f), created);
            FireSuppression far = CreateFire("physics-fire-far", offset + new Vector3(0f, 0f, 4f), created);
            var toolRoot = new GameObject("physics-spray-tool"); created.Add(toolRoot);
            toolRoot.AddComponent<PickupItem>();
            var spray = toolRoot.AddComponent<ExtinguisherSpray>();
            toolRoot.transform.position = offset;
            yield return null; // Allow component Awake wiring before sampling.

            Ray rootRay = new Ray(offset, Vector3.forward);
            Need(spray.EvaluateTargetForTest(near, rootRay, Vector3.up) == SprayHitQuality.Effective,
                "root-band ray did not produce an effective spray sample.");
            Need(spray.EvaluateTargetForTest(far, rootRay, Vector3.up) == SprayHitQuality.TooFar,
                "out-of-range target was not rejected.");
            Need(spray.EvaluateTargetForTest(near, new Ray(offset + Vector3.up, Vector3.forward), Vector3.up) == SprayHitQuality.TooHigh,
                "upper-flame ray was not rejected.");

            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); created.Add(wall);
            wall.name = "physics-spray-wall";
            wall.transform.position = offset + new Vector3(0f, 0f, 1f);
            wall.transform.localScale = new Vector3(2f, 2f, .1f);
            Physics.SyncTransforms();
            Need(spray.EvaluateTargetForTest(near, rootRay, Vector3.up) == SprayHitQuality.None,
                "wall obstruction did not block the production spray sampler.");
            UnityEngine.Object.Destroy(wall); created.Remove(wall);
            yield return null;
            Physics.SyncTransforms();

            Need(Mathf.Approximately(ExtinguisherSpray.CoverageShareForEffectiveTarget(2), .5f),
                "two effective targets do not split one tool budget.");
            Need(Mathf.Approximately(ExtinguisherSpray.CoverageShareForEffectiveTarget(1), 1f),
                "one effective target does not retain the full tool budget.");

            var actorRoot = new GameObject("physics-tool-blocked-actor"); created.Add(actorRoot);
            var cameraRoot = new GameObject("Camera"); cameraRoot.transform.SetParent(actorRoot.transform, false);
            cameraRoot.AddComponent<Camera>().enabled = false;
            var actor = actorRoot.AddComponent<body>();
            actor.sessionHost = actorRoot.AddComponent<LevelSessionHost>(); // no running session = blocked by contract
            Need(!ExtinguisherSpray.IsToolUseAllowed(actor), "ToolUse-blocked actor can still submit spray input.");
            report?.Invoke("PASS fire physics: root/upper/range/obstruction, bounded multi-target budget and ToolUse gate");
        }
        finally
        {
            foreach (var root in created) if (root != null) UnityEngine.Object.Destroy(root);
        }
    }

    static FireSuppression CreateFire(string name, Vector3 position, List<GameObject> created)
    {
        var root = new GameObject(name); created.Add(root);
        root.transform.position = position;
        root.AddComponent<FireEffectController>();
        return root.AddComponent<FireSuppression>();
    }
    static void Need(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
}
