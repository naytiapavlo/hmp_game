using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Explicit installer: preserves existing prefab/material GUIDs and all runtime APIs.
[InitializeOnLoad]
public static class FireVisualUpgrade
{
    const string Folder = "Assets/VFX/";
    static bool autoInstallAttempted;
    static int autoInstallAttempts;

    static FireVisualUpgrade()
    {
        // Importing a Shader Graph is asynchronous. A single delayed attempt lets
        // Unity finish importing the graph and atlases before rebuilding prefabs.
        EditorApplication.delayCall += AutoInstall;
    }

    static void AutoInstall()
    {
        if (autoInstallAttempted || SessionState.GetBool("HMProtection.FireVisualInstalled", false)) return;
        autoInstallAttempted = true;
        // 就绪判定：资产库不在忙 + 首尾两张图集都能真正加载到 Texture2D。
        // 此前的竞速问题：FindAssets 按名字能在导入完成前命中，而 Install 期间
        // 资产库仍在重导入 2048 图集，MaterialFor 的 LoadAssetAtPath 返回 null。
        bool ready = !EditorApplication.isUpdating
            && AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "T_Fire_Small_8x8.png") != null
            && AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "T_Smoke_SmokeOnly_8x8.png") != null;
        if (!ready)
        {
            autoInstallAttempts++;
            autoInstallAttempted = false;
            if (autoInstallAttempts < 90) EditorApplication.delayCall += AutoInstall;
            return;
        }
        try
        {
            Install();
            SessionState.SetBool("HMProtection.FireVisualInstalled", true);
        }
        catch (Exception e)
        {
            autoInstallAttempts++;
            if (autoInstallAttempts < 90)
            {
                autoInstallAttempted = false;
                EditorApplication.delayCall += AutoInstall;
            }
            else Debug.LogError("[FireVisual] Auto-install stopped after retries: " + e.Message);
        }
    }
    [MenuItem("Tools/Fire/Install Baked Visuals")]
    public static void Install()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[]{Folder.TrimEnd('/')}))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.Contains("_8x8")) continue;
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
        }
        Make("Small", .38f, .68f, 1.2f, 2.8f);
        Make("Medium", .78f, 1.4f, 2.3f, 4.2f);
        Make("Large", 1.35f, 2.3f, 3.8f, 5.5f);
        Make("SmokeOnly", 0f, .43f, 0f, 0f);
        AssetDatabase.SaveAssets();
        Debug.Log("[FireVisual] Installed four baked tiers; public FireVfx/FireEffectController APIs unchanged.");
    }

    static Material MaterialFor(string tier, bool smoke)
    {
        string name = smoke ? (tier=="Small" ? "MAT_FireSmoke" : "MAT_Smoke_"+tier)
                            : (tier=="Small" ? "MAT_FireFlame" : "MAT_Flame_"+tier);
        string path=Folder+name+".mat";
        var shader=AssetDatabase.LoadAssetAtPath<Shader>(Folder+(smoke?"SG_SmokeFlipbook":"SG_FireFlipbook")+".shadergraph");
        if(shader==null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Fire shader invalid: "+name);
        var mat=AssetDatabase.LoadAssetAtPath<Material>(path);
        if(mat==null){mat=new Material(shader);AssetDatabase.CreateAsset(mat,path);}
        mat.shader=shader;
        string texture=Folder+"T_"+(smoke?"Smoke":"Fire")+"_"+tier+"_8x8.png";
        var atlas=AssetDatabase.LoadAssetAtPath<Texture2D>(texture);
        if(atlas==null)
        {
            // 兜底：强制同步重导入一次再取；仍失败则打印资产库视角的深度诊断
            AssetDatabase.ImportAsset(texture, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            atlas=AssetDatabase.LoadAssetAtPath<Texture2D>(texture);
            if(atlas==null)
            {
                var main = AssetDatabase.LoadMainAssetAtPath(texture);
                Debug.LogError("[FireVisual] 图集不可加载：" + texture
                    + " | 文件存在=" + System.IO.File.Exists(texture)
                    + " | 主资产类型=" + (main == null ? "null（资产库未注册）" : main.GetType().Name)
                    + " | 已尝试全新重导入，若仍失败请重启编辑器");
            }
        }
        if(atlas==null)throw new FileNotFoundException("Missing baked atlas",texture);
        mat.SetTexture("_MainTex",atlas);
        mat.SetFloat("_Intensity",smoke?(tier=="SmokeOnly"?1.3f:.32f):2.2f);
        mat.SetFloat("_Distortion",smoke?.009f:.012f);
        mat.SetFloat("_SoftDistance",.025f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void Make(string tier,float flameHeight,float smokeHeight,float lightIntensity,float lightRange)
    {
        string path=Folder+(tier=="SmokeOnly"?"VFX_Smoke_Only":"VFX_Fire_"+tier)+".prefab";
        bool exists=AssetDatabase.LoadAssetAtPath<GameObject>(path)!=null;
        GameObject root=exists?PrefabUtility.LoadPrefabContents(path):new GameObject(Path.GetFileNameWithoutExtension(path));
        try
        {
            while(root.transform.childCount>0)UnityEngine.Object.DestroyImmediate(root.transform.GetChild(0).gameObject);
            root.transform.localScale=Vector3.one;
            if(root.GetComponent<FireVfx>()==null)root.AddComponent<FireVfx>();
            if(flameHeight>0)
            {
                Layer(root,"Flame",MaterialFor(tier,false),flameHeight,flameHeight*.47f,1.15f,2.6667f,.018f,.72f,12);
                Light(root,"Light",lightIntensity,lightRange,flameHeight*.35f);
                Light(root,"LightFill",lightIntensity*.22f,lightRange*1.4f,flameHeight*.6f);
            }
            Layer(root,"Smoke",MaterialFor(tier,true),smokeHeight,smokeHeight*.48f+flameHeight*.28f,
                tier=="SmokeOnly"?.8f:1.1f,2.6667f,.015f,tier=="SmokeOnly"?.55f:.8f,12);
            PrefabUtility.SaveAsPrefabAsset(root,path);
        }
        finally{if(exists)PrefabUtility.UnloadPrefabContents(root);else UnityEngine.Object.DestroyImmediate(root);}
    }
    static void Light(GameObject root,string name,float intensity,float range,float y)
    {
        var go=new GameObject(name);go.transform.SetParent(root.transform,false);go.transform.localPosition=new Vector3(0,y,0);
        var light=go.AddComponent<Light>();light.type=LightType.Point;light.color=new Color(1,.48f,.14f);
        light.intensity=intensity;light.range=range;light.shadows=LightShadows.None;
    }
    static void Layer(GameObject root,string name,Material material,float size,float y,float rate,float life,float speed,float alpha,int max)
    {
        var go=new GameObject(name);go.transform.SetParent(root.transform,false);go.transform.localPosition=new Vector3(0,y,0);
        var ps=go.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
        var main=ps.main;main.loop=true;main.prewarm=true;main.playOnAwake=true;main.duration=life;
        main.startLifetime=life;main.startSpeed=speed;main.startSize=size;main.startColor=Color.white;
        main.maxParticles=max;main.simulationSpace=ParticleSystemSimulationSpace.Local;
        main.scalingMode=ParticleSystemScalingMode.Hierarchy;
        var shape=ps.shape;shape.enabled=true;shape.shapeType=ParticleSystemShapeType.Box;shape.scale=new Vector3(size*.08f,.001f,size*.08f);
        var emission=ps.emission;emission.rateOverTime=rate;
        var color=ps.colorOverLifetime;color.enabled=true;
        color.color=new Gradient{colorKeys=new[]{new GradientColorKey(Color.white,0),new GradientColorKey(Color.white,1)},
            alphaKeys=new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(alpha,.18f),new GradientAlphaKey(alpha,.7f),new GradientAlphaKey(0,1)}};
        var renderer=go.GetComponent<ParticleSystemRenderer>();renderer.sharedMaterial=material;
        renderer.renderMode=ParticleSystemRenderMode.Billboard;renderer.alignment=ParticleSystemRenderSpace.View;
        renderer.maxParticleSize=1;renderer.sortMode=ParticleSystemSortMode.Distance;
        renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
        renderer.SetActiveVertexStreams(new List<ParticleSystemVertexStream>{ParticleSystemVertexStream.Position,
            ParticleSystemVertexStream.Color,ParticleSystemVertexStream.UV,ParticleSystemVertexStream.AgePercent,ParticleSystemVertexStream.StableRandomX});
    }
}
