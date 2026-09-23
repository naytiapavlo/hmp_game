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
            var nozzleMesh=nozzle.GetComponent<MeshFilter>().sharedMesh;
            var nozzleVertices=nozzleMesh.vertices; Vector3 nozzleRestScale=nozzle.localScale;
            Vector3 restPosition = nozzle.localPosition; Quaternion restRotation = nozzle.localRotation;
            var rig = root.AddComponent<ExtinguisherCarryRig>();
            Require(rig.Initialize(), "initialize rig");
            var cameraObject = new GameObject("Carry QA camera");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, scene);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.nearClipPlane = .3f; camera.fieldOfView = 60f;
            int count = 0;
            foreach (float yaw in new[] { 0f, 90f, 179f, 270f })
                foreach (float pitch in new[] { -70f, -30f, 0f, 30f, 70f, 85f })
                    foreach (float handHeightOffset in new[] { 0f, -.30f, .20f })
                    {
                        Quaternion body = Quaternion.Euler(0, yaw, 0);
                        Quaternion view = body * Quaternion.Euler(pitch, 0, 0);
                        Vector3 eye = new Vector3(3, 1.6f, -2);
                        Vector3 right = eye + view * new Vector3(.19f, -.28f + handHeightOffset, .42f);
                        camera.transform.SetPositionAndRotation(eye, view);
                        // Start from the captured failed orientation as well as changing aim.
                        root.transform.rotation = Quaternion.Euler(-25.440f, -44.032f, -85.337f);
                        Require(rig.ApplyPose(right, view * Vector3.forward, body * Vector3.forward), "apply pose");
                        Vector3 correction = rig.GetViewCorrection(camera);
                        right += correction;
                        Require(rig.ApplyPose(right, view * Vector3.forward, body * Vector3.forward), "apply view-safe pose");
                        if(pitch>=-25f && pitch<=85f)CheckViewClearance(root, camera);
                        if (Mathf.Abs(pitch) < .1f)
                        {
                            float handleY = camera.WorldToViewportPoint(rig.HandlePosition).y;
                            Require(handleY >= .18f && handleY <= .28f,
                                "level-view handle must stay visible in lower screen: y=" + handleY);
                        }
                        if(pitch>=-25f && pitch<=85f)
                            Require(rig.GetViewCorrection(camera).magnitude < .001f, "working-view correction must be idempotent");
                        Require(Vector3.Angle(root.transform.up, Vector3.up) <= 25.1f, "bottle must never lie sideways");
                        Require(Vector3.Distance(rig.HandlePosition, right) < .0001f, "right hand contact");
                        Vector3 tubeMount = root.transform.InverseTransformPoint(hose.transform.position);
                        var carryHandle = PropGeometry.FindDeep(root.transform, "CarryHandle");
                        float frontZ = float.NegativeInfinity;
                        foreach (var vertex in carryHandle.GetComponent<MeshFilter>().sharedMesh.vertices)
                            frontZ = Mathf.Max(frontZ, root.transform.InverseTransformPoint(carryHandle.TransformPoint(vertex)).z);
                        Require(Mathf.Abs(tubeMount.z - frontZ) < .01f,
                            "straight tube must enter the actual handle front: mount=" + tubeMount + " frontZ=" + frontZ);
                        Require(root.transform.Find("Rigid tube side fitting") == null, "no side fitting or right-angle bend");
                        Require(Vector3.Dot(hose.transform.forward,view*Vector3.forward)>.9999f,"entire rigid tube parallel to gaze");
                        Require(Mathf.Abs(hose.sharedMesh.bounds.size.z-.40f)<.0001f,"tube length stays fixed");
                        foreach(var point in hose.sharedMesh.vertices)Require(Mathf.Abs(new Vector2(point.x,point.y).magnitude-.012f)<.0001f,"straight tube constant radius");
                        Require(Vector3.Dot((outlet.position-rig.NozzleGripPosition).normalized, view*Vector3.forward) > .9999f, "outlet aims forward");
                        Require(nozzle.GetComponent<MeshFilter>().sharedMesh==nozzleMesh && nozzle.localScale==nozzleRestScale, "rigid nozzle cannot deform or scale");
                        Require(hose.sharedMesh != original, "deform private copy only");
                        foreach (Vector3 v in hose.sharedMesh.vertices)
                            Require(!float.IsNaN(v.x) && !float.IsInfinity(v.x) && v.sqrMagnitude < 4, "finite hose vertices, no explosion");
                        count++;
                    }
            rig.Restore();
            Require(hose.sharedMesh == original, "drop restores original hose mesh");
            Require(nozzle.localPosition == restPosition && Quaternion.Angle(nozzle.localRotation, restRotation) < .001f, "drop restores nozzle");
            var nozzleAfter=nozzleMesh.vertices;
            for(int i=0;i<nozzleVertices.Length;i++)Require(nozzleVertices[i]==nozzleAfter[i], "rigid nozzle vertices unchanged");
            Vector3[] after = original.vertices;
            for (int i=0;i<before.Length;i++) Require(before[i] == after[i], "imported mesh unchanged");
            Require(rig.ApplyPose(new Vector3(.2f,1.2f,.4f),Vector3.forward,Vector3.forward),"pick up again");
            rig.Restore();
            string report = "PASS: " + count + " yaw/pitch/hand-height cases; camera clearance, lower viewport, no correction drift, upright cylinder, both hand contacts, outlet aim, finite hose, drop/re-pickup, original mesh unchanged.\n";
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
