using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ExtinguisherViewChecks
{
    private static void Call(Component c, string method) => c.GetType().GetMethod(method, BindingFlags.NonPublic|BindingFlags.Instance).Invoke(c,null);
    [MenuItem("Tools/Props/验收：灭火器真实手臂与相机俯仰")]
    public static void Run()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var report = new StringBuilder(); int failures=0;
        try
        {
            var player = new GameObject("View QA player"); SceneManager.MoveGameObjectToScene(player,scene);
            var bodyObject = new GameObject("body"); bodyObject.transform.SetParent(player.transform,false);
            bodyObject.transform.localScale = new Vector3(.5f,1f,.5f);
            var cameraObject = new GameObject("Camera"); cameraObject.transform.SetParent(bodyObject.transform,false);
            cameraObject.transform.localPosition = new Vector3(.023f,.651f,.03f);
            cameraObject.transform.localScale = new Vector3(.7272f,.3636f,.7272f);
            var camera = cameraObject.AddComponent<Camera>(); camera.nearClipPlane=.3f;camera.fieldOfView=60f;camera.aspect=16f/9f;
            var playerBody = bodyObject.AddComponent<body>();
            var bodyData = new SerializedObject(playerBody);bodyData.FindProperty("playerCamera").objectReferenceValue=camera;bodyData.ApplyModifiedPropertiesWithoutUndo();
            var handRoot=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/FPHands/FPHands_01.prefab"),scene);
            handRoot.transform.SetParent(bodyObject.transform,false);
            handRoot.transform.localPosition=new Vector3(-.086f,.802f,.236f);
            handRoot.transform.localScale=new Vector3(1.9201893f,1.5834f,1.5834f);
            // Apply the scene's nested left-arm position override, identified by prefab local ID.
            foreach(Transform t in handRoot.GetComponentsInChildren<Transform>())
            {
                var source=PrefabUtility.GetCorrespondingObjectFromSource(t);
                if(source!=null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source,out string guid,out long id) && id==7168930113270127656L)
                {var p=t.localPosition;p.x=-.166f;t.localPosition=p;}
            }
            var hands=handRoot.AddComponent<HandPoseController>();Call(hands,"Awake");hands.SetPose(HandPoseController.Hand.Both,HandPoseController.Pose.Extinguisher);
            var arms=handRoot.AddComponent<ArmsPitchFollow>();Call(arms,"Awake");
            var prop=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Props/Models/SM_Extinguisher_01.fbx"),scene);
            var rig=prop.AddComponent<ExtinguisherCarryRig>();rig.Initialize();
            float minHandle=1,maxHandle=0;
            foreach(float pitch in new[]{-80f,-60f,-30f,0f,20f,35f,50f,70f,80f})
            {
                playerBody.SetPitch(pitch);Call(arms,"LateUpdate");
                Vector3 genericPosition=handRoot.transform.position;
                Quaternion genericRotation=handRoot.transform.rotation;
                arms.PrepareExtinguisherPose(camera);
                arms.TryGetGripPoint(true,out Vector3 right);arms.TryGetGripPoint(false,out Vector3 left);
                rig.ApplyPose(right,left,camera.transform.forward,bodyObject.transform.forward);
                Vector3 correction=rig.GetViewCorrection(camera);
                arms.ShiftHeldPose(correction);right+=correction;left+=correction;
                rig.ApplyPose(right,left,camera.transform.forward,bodyObject.transform.forward);
                float handleY=camera.WorldToViewportPoint(rig.HandlePosition).y;

                minHandle=Mathf.Min(minHandle,handleY);maxHandle=Mathf.Max(maxHandle,handleY);
                float minDepth=float.PositiveInfinity;float top=0;
                if(Vector3.Distance(rig.HandlePosition,right)>.001f || Vector3.Distance(rig.NozzleGripPosition,left)>.001f)
                    throw new InvalidOperationException("grip contact lost");
                foreach(var renderer in handRoot.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    var baked=new Mesh();renderer.BakeMesh(baked, true);
                    foreach(Vector3 v in baked.vertices)
                    {
                        Vector3 view=camera.WorldToViewportPoint(renderer.transform.TransformPoint(v));
                        minDepth=Mathf.Min(minDepth,view.z);top=Mathf.Max(top,view.y);
                    }
                    UnityEngine.Object.DestroyImmediate(baked);
                }
                bool ok=handleY>=.24f && handleY<=.38f && minDepth>=camera.nearClipPlane+.02f && top<.52f;
                if(!ok)failures++;
                report.AppendLine($"pitch={pitch}: handleY={handleY:F4}, armMinDepth={minDepth:F4}, armTop={top:F4} {(ok?"PASS":"FAIL")}");
                Vector3 heldPosition=prop.transform.position;
                Call(arms,"LateUpdate");arms.PrepareExtinguisherPose(camera);
                arms.TryGetGripPoint(true,out right);arms.TryGetGripPoint(false,out left);
                rig.ApplyPose(right,left,camera.transform.forward,bodyObject.transform.forward);
                correction=rig.GetViewCorrection(camera);arms.ShiftHeldPose(correction);
                rig.ApplyPose(right+correction,left+correction,camera.transform.forward,bodyObject.transform.forward);
                if(Vector3.Distance(prop.transform.position,heldPosition)>.001f)
                    throw new InvalidOperationException("held pose accumulates between frames");
                rig.Restore();Call(arms,"LateUpdate");
                if(Vector3.Distance(handRoot.transform.position,genericPosition)>.001f || Quaternion.Angle(handRoot.transform.rotation,genericRotation)>.01f)
                    throw new InvalidOperationException("generic arms pose not restored after drop");
            }
            if(maxHandle-minHandle>.06f){failures++;report.AppendLine("FAIL: pitch changes grip framing by more than 6% of screen height");}
            report.AppendLine($"{(failures==0?"PASS":"FAIL")}: real hand mesh, scene camera hierarchy, {failures} failures");
            File.WriteAllText("Library/ExtinguisherViewChecks.txt",report.ToString());Debug.Log(report.ToString());
            if(failures>0)throw new InvalidOperationException("Extinguisher view checks failed; see report");
        }
        finally {EditorSceneManager.ClosePreviewScene(scene);}
    }
    public static void Batch(){try{ExtinguisherCarryChecks.Run();Run();EditorApplication.Exit(0);}catch(Exception e){Debug.LogException(e);EditorApplication.Exit(1);}}
}
