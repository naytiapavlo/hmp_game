using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class HandsQA
{
    static readonly string Output = @"C:\Users\Administrator\Desktop\hmp\hmp_game\模型\第一人称双手";
    static Camera cam;
    static GameObject hands;
    static double started;
    [Serializable] class Result
    {
        public bool passed;
        public string unityVersion, shader;
        public int meshes, triangles, bones;
        public float leftX, rightX, forwardZ, thumbMotion;
        public string error;
    }
    static Result result = new Result();
    public static void InspectAxis()
    {
        var obj=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/FPHands/SK_FPHands_01.fbx"));
        var text="Root name="+obj.name+" pos="+obj.transform.position+" euler="+obj.transform.eulerAngles+" scale="+obj.transform.localScale+"\n";
        foreach(var r in obj.GetComponentsInChildren<SkinnedMeshRenderer>())text+=r.name+" bounds="+r.bounds+" local="+r.transform.localToWorldMatrix+"\n";
        File.WriteAllText(Path.Combine(Output,"QA","unity_axes.txt"),text);
        EditorApplication.Exit(0);
    }
    public static void Run()
    {
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            PlayerSettings.colorSpace = ColorSpace.Linear;
            var modelPath = "Assets/FPHands/SK_FPHands_01.fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.globalScale = 1;
            importer.useFileScale = true;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.optimizeGameObjects = false;
            importer.isReadable = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();
            foreach (string path in Directory.GetFiles("Assets/FPHands/Textures", "*.png"))
            {
                var ti = (TextureImporter)AssetImporter.GetAtPath(path.Replace('\\','/'));
                ti.sRGBTexture = path.Contains("BaseColor");
                ti.textureType = path.Contains("Normal") ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.maxTextureSize = 2048;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.SaveAndReimport();
            }
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new Exception("URP Lit shader unavailable");
            var mat = new Material(shader) { name = "M_FPHands_01" };
            Texture2D Tex(string k) => AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/FPHands/Textures/T_FPHands_01_"+k+".png");
            mat.SetTexture("_BaseMap",Tex("BaseColor")); mat.SetColor("_BaseColor",Color.white);
            mat.SetTexture("_BumpMap",Tex("Normal")); mat.SetFloat("_BumpScale",1);mat.EnableKeyword("_NORMALMAP");
            mat.SetTexture("_MetallicGlossMap",Tex("MetallicSmoothness"));mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.SetFloat("_Smoothness",1);mat.SetFloat("_Metallic",0);
            mat.SetTexture("_OcclusionMap",Tex("AO"));mat.SetFloat("_OcclusionStrength",.65f);mat.EnableKeyword("_OCCLUSIONMAP");
            mat.SetFloat("_Cull",2);mat.SetFloat("_Surface",0);
            AssetDatabase.CreateAsset(mat,"Assets/FPHands/M_FPHands_01.mat");
            hands = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath));
            hands.name="FPHands_01";
            hands.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
            hands.transform.localScale=Vector3.one;
            var renderers=hands.GetComponentsInChildren<SkinnedMeshRenderer>();
            if(renderers.Length!=2)throw new Exception("Expected two skinned meshes, got "+renderers.Length);
            foreach(var r in renderers) {r.sharedMaterial=mat;r.updateWhenOffscreen=true;r.quality=SkinQuality.Bone4;}
            var l=renderers.Single(x=>x.name.Contains("_L_"));var rr=renderers.Single(x=>x.name.Contains("_R_"));
            result.leftX=l.bounds.center.x;result.rightX=rr.bounds.center.x;
            result.forwardZ=(l.bounds.center.z+rr.bounds.center.z)*.5f;
            result.meshes=renderers.Length;result.triangles=renderers.Sum(x=>x.sharedMesh.triangles.Length/3);
            result.bones=hands.GetComponentsInChildren<Transform>().Count(x=>x.name=="Root"||x.name.StartsWith("Forearm.")||x.name.StartsWith("Wrist.")||x.name.Contains(".0")||x.name.StartsWith("LittleMetacarpal."));
            result.unityVersion=Application.unityVersion;result.shader=shader.name;
            if(result.leftX>=0||result.rightX<=0||result.forwardZ<=.1)throw new Exception("Imported axes or handedness mismatch");
            if(result.triangles!=18616)throw new Exception("Triangle count changed");
            var thumb=hands.GetComponentsInChildren<Transform>().Single(x=>x.name=="Thumb.01.L");
            var m0=new Mesh();var m1=new Mesh();l.BakeMesh(m0);var q=thumb.localRotation;
            thumb.localRotation=q*Quaternion.Euler(12,0,0);l.BakeMesh(m1);thumb.localRotation=q;
            var a=m0.vertices;var b=m1.vertices;for(int i=0;i<a.Length;i++)result.thumbMotion=Mathf.Max(result.thumbMotion,Vector3.Distance(a[i],b[i]));
            if(result.thumbMotion<.001)throw new Exception("Skin deformation failed");
            UnityEngine.Object.DestroyImmediate(m0);UnityEngine.Object.DestroyImmediate(m1);
            PrefabUtility.SaveAsPrefabAsset(hands,"Assets/FPHands/FPHands_01.prefab");
            var rendererData=ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData,"Assets/QA_Renderer.asset");
            var pipeline=UniversalRenderPipelineAsset.Create(rendererData);
            pipeline.msaaSampleCount=4;pipeline.supportsHDR=false;
            AssetDatabase.CreateAsset(pipeline,"Assets/QA_Pipeline.asset");
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.23f,.25f,.28f);
            var key=new GameObject("QA_Key").AddComponent<Light>();key.type=LightType.Directional;key.intensity=1.5f;
            key.color=new Color(1,.93f,.87f);key.transform.rotation=Quaternion.Euler(38,-35,0);
            var fill=new GameObject("QA_Fill").AddComponent<Light>();fill.type=LightType.Directional;fill.intensity=.65f;
            fill.color=new Color(.78f,.86f,1);fill.transform.rotation=Quaternion.Euler(20,120,0);
            cam=new GameObject("QA_FirstPerson").AddComponent<Camera>();cam.fieldOfView=60;
            cam.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);cam.nearClipPlane=.01f;cam.farClipPlane=20;
            cam.backgroundColor=new Color(.065f,.080f,.10f);cam.clearFlags=CameraClearFlags.SolidColor;
            cam.allowHDR=false;cam.GetUniversalAdditionalCameraData().renderPostProcessing=false;
            AssetDatabase.SaveAssets();
            Directory.CreateDirectory("Assets/FPHands/Licenses");
            foreach(var license in Directory.GetFiles(Path.Combine(Output,"Licenses"),"*.txt"))
                File.Copy(license,Path.Combine("Assets/FPHands/Licenses",Path.GetFileName(license)),true);
            File.WriteAllText("Assets/FPHands/SourceNotice.txt", "Derived from Hafnia Hands, https://github.com/henningpohl/Hafnia-Hands (commit c9753a9912046d93d4733865ab7784a17c3460c4).\nHand meshes: Copyright Facebook Technologies, LLC and its affiliates; BSD-3-Clause.\nSkin textures: Copyright (c) 2021 Aske Mottelson; MIT.\nHafnia project: Henning Pohl and Aske Mottelson. Texture artist credited by the paper: Ranjeet Singh.\nPohl H, Mottelson A (2022), Hafnia Hands, doi:10.3389/frvir.2022.719506.\nAdaptation: continuous forearms, topology refinement, replacement skeleton, shared PBR atlas.\nRetain both full licenses provided in Licenses when distributing this asset.\n");
            AssetDatabase.Refresh();
            started=EditorApplication.timeSinceStartup;
            EditorApplication.update+=Finish;
        }
        catch(Exception ex){Fail(ex);}
    }
    static void Finish()
    {
        if(EditorApplication.timeSinceStartup-started<3)return;
        EditorApplication.update-=Finish;
        try
        {
            var rt=new RenderTexture(1600,900,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
            rt.Create();
            var request=new UniversalRenderPipeline.SingleCameraRequest{destination=rt};
            RenderPipeline.SubmitRenderRequest(cam,request);
            RenderTexture.active=rt;
            var image=new Texture2D(1600,900,TextureFormat.RGB24,false,false);
            image.ReadPixels(new Rect(0,0,1600,900),0,0);image.Apply();
            File.WriteAllBytes(Path.Combine(Output,"Previews","FPHands_UnityURP.png"),image.EncodeToPNG());
            RenderTexture.active=null;rt.Release();UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(image);
            AssetDatabase.ExportPackage("Assets/FPHands",Path.Combine(Output,"FPHands_URP.unitypackage"),ExportPackageOptions.Recurse);
            result.passed=true;
            File.WriteAllText(Path.Combine(Output,"QA","unity_validation.json"),JsonUtility.ToJson(result,true));
            Debug.Log("FPHANDS_QA_SUCCESS "+JsonUtility.ToJson(result));
            EditorApplication.Exit(0);
        }
        catch(Exception ex){Fail(ex);}
    }
    static void Fail(Exception ex)
    {
        result.passed=false;result.error=ex.ToString();
        File.WriteAllText(Path.Combine(Output,"QA","unity_validation.json"),JsonUtility.ToJson(result,true));
        Debug.LogException(ex);EditorApplication.Exit(1);
    }
}
