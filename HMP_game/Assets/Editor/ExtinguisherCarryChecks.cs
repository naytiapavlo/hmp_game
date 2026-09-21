using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ExtinguisherCarryChecks
{
    [MenuItem("Tools/Props/验收：灭火器直立持握与软管")]
    public static void Run()
    {
        const string path = "Assets/Props/Models/SM_Extinguisher_01.fbx";
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        Require(importer != null && importer.isReadable, "model Read/Write required");
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            GameObject root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), scene);
            var hose = PropGeometry.FindDeep(root.transform, "Hose").GetComponent<MeshFilter>();
            Mesh original = hose.sharedMesh; Vector3[] before = original.vertices;
            Transform nozzle = PropGeometry.FindDeep(root.transform, "NozzleBody");
            Transform outlet = PropGeometry.FindDeep(root.transform, "Nozzle");
            Vector3 restPosition = nozzle.localPosition; Quaternion restRotation = nozzle.localRotation;
            var rig = root.AddComponent<ExtinguisherCarryRig>();
            Require(rig.Initialize(), "initialize rig");
            var cameraObject = new GameObject("Carry QA camera");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, scene);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.nearClipPlane = .3f; camera.fieldOfView = 60f;
            int count = 0;
            foreach (float yaw in new[] { 0f, 90f, 179f, 270f })
                foreach (float pitch in new[] { -70f, -30f, 0f, 30f, 70f })
                    foreach (float gap in new[] { .30f, .39f, .48f })
                    foreach (float handHeightOffset in new[] { 0f, -.30f, .20f })
                    {
                        Quaternion body = Quaternion.Euler(0, yaw, 0);
                        Quaternion view = body * Quaternion.Euler(pitch, 0, 0);
                        Vector3 eye = new Vector3(3, 1.6f, -2);
                        Vector3 right = eye + view * new Vector3(.19f, -.28f + handHeightOffset, .42f);
                        Vector3 left = eye + view * new Vector3(.19f-gap, -.26f + handHeightOffset, .48f);
                        camera.transform.SetPositionAndRotation(eye, view);
                        // Start from the captured failed orientation as well as changing aim.
                        root.transform.rotation = Quaternion.Euler(-25.440f, -44.032f, -85.337f);
                        Require(rig.ApplyPose(right, left, view * Vector3.forward, body * Vector3.forward), "apply pose");
                        Vector3 correction = rig.GetViewCorrection(camera);
                        right += correction; left += correction;
                        Require(rig.ApplyPose(right, left, view * Vector3.forward, body * Vector3.forward), "apply view-safe pose");
                        CheckViewClearance(root, camera);
                        if (Mathf.Abs(pitch) < .1f)
                        {
                            float handleY = camera.WorldToViewportPoint(rig.HandlePosition).y;
                            Require(handleY >= .24f && handleY <= .38f,
                                "level-view handle must stay visible in lower screen: y=" + handleY);
                        }
                        Require(rig.GetViewCorrection(camera).magnitude < .001f, "view correction must not accumulate");
                        Require(Vector3.Dot(root.transform.up, Vector3.up) > .99999f, "bottle must remain upright");
                        Require(Vector3.Distance(rig.HandlePosition, right) < .0001f, "right hand contact");
                        Require(Vector3.Distance(rig.NozzleGripPosition, left) < .0001f, "left hand contact");
                        Require(Vector3.Dot((outlet.position-rig.NozzleGripPosition).normalized, view*Vector3.forward) > .9999f, "outlet aims forward");
                        Require(hose.sharedMesh != original, "deform private copy only");
                        foreach (Vector3 v in hose.sharedMesh.vertices)
                            Require(!float.IsNaN(v.x) && !float.IsInfinity(v.x) && v.sqrMagnitude < 4, "finite hose vertices, no explosion");
                        count++;
                    }
            rig.Restore();
            Require(hose.sharedMesh == original, "drop restores original hose mesh");
            Require(nozzle.localPosition == restPosition && Quaternion.Angle(nozzle.localRotation, restRotation) < .001f, "drop restores nozzle");
            Vector3[] after = original.vertices;
            for (int i=0;i<before.Length;i++) Require(before[i] == after[i], "imported mesh unchanged");
            Require(rig.ApplyPose(new Vector3(.2f,1.2f,.4f),new Vector3(-.2f,1.2f,.5f),Vector3.forward,Vector3.forward),"pick up again");
            rig.Restore();
            string report = "PASS: " + count + " yaw/pitch/hand-gap cases; camera clearance, lower viewport, no correction drift, upright cylinder, both hand contacts, outlet aim, finite hose, drop/re-pickup, original mesh unchanged.\n";
            File.WriteAllText("Library/ExtinguisherCarryChecks.txt", report);
            Debug.Log(report);
            UnityEngine.Object.DestroyImmediate(root);
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
    }

    private static void CheckViewClearance(GameObject root, Camera camera)
    {
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>())
        {
            Bounds bounds = filter.sharedMesh.bounds;
            for (int corner=0;corner<8;corner++)
            {
                Vector3 point = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1));
                Vector3 viewport = camera.WorldToViewportPoint(filter.transform.TransformPoint(point));
                Require(viewport.z >= camera.nearClipPlane + .099f,
                    "camera clips " + filter.name + ": depth=" + viewport.z);
                Require(viewport.y <= .481f, "upper view obscured by " + filter.name + ": viewport y=" + viewport.y);
            }
        }
    }

    public static void Batch()
    {
        try { Run(); EditorApplication.Exit(0); }
        catch (Exception e) { File.WriteAllText("Library/ExtinguisherCarryChecks.txt", "FAIL: " + e); Debug.LogException(e); EditorApplication.Exit(1); }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
