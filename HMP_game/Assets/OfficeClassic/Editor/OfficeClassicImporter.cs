// Copy the OfficeClassic folder to Assets/OfficeClassic in a Unity URP project.
// Then run Tools > Office Classic > Prepare Materials and Prefab.
// No scene, physics, fire state or interaction code is installed automatically.
// Adapted for this repository: model file is SCN_Office_01.fbx (exported from the 9877 Office_Classic_01 scene).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class OfficeClassicImporter : AssetPostprocessor
{
    private const string Root = "Assets/OfficeClassic";

    private void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(Root + "/Textures/", StringComparison.Ordinal)) return;
        var importer = (TextureImporter)assetImporter;
        bool normal = assetPath.Contains("_Normal.");
        bool color = assetPath.Contains("_BaseColor.");
        importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        importer.sRGBTexture = color;
        importer.maxTextureSize = 1024;
        importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.filterMode = FilterMode.Trilinear;
        importer.anisoLevel = 4;
        importer.textureCompression = TextureImporterCompression.Compressed;
        if (assetPath.Contains("T_Display_") || assetPath.Contains("T_Keyboard_"))
            importer.wrapMode = TextureWrapMode.Clamp;
        if (assetPath.Contains("_MetallicSmoothness."))
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
    }

    private void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(Root + "/Models/", StringComparison.Ordinal)) return;
        var importer = (ModelImporter)assetImporter;
        importer.globalScale = 1f;
        importer.useFileScale = true;
        importer.importAnimation = false;
        importer.importCameras = false;
        importer.importLights = false;
        importer.importNormals = ModelImporterNormals.Import;
        importer.importTangents = ModelImporterTangents.CalculateMikk;
        importer.generateSecondaryUV = true;
        importer.isReadable = false;
        importer.addCollider = false;
    }

    private static Texture2D Texture(string name, string map)
    {
        return AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Textures/T_" + name + "_" + map + ".png");
    }

    private static void SetFloat(Material material, string name, float value)
    {
        if (material.HasProperty(name)) material.SetFloat(name, value);
    }

    private static Material BuildMaterial(string sourceName, Shader shader)
    {
        string materialName = Regex.Replace(sourceName, @"\.\d+$", "");
        string family = materialName.StartsWith("MAT_") ? materialName.Substring(4) : materialName;
        string baseFamily = family == "LightDiffuser" ? "PowderWhite" : family;
        string surfaceFamily = family == "Display" || family == "Keyboard" ? "PlasticBlack" : baseFamily;
        string path = Root + "/Materials/" + materialName + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = materialName };
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = shader;
        material.SetTexture("_BaseMap", Texture(baseFamily, "BaseColor"));
        material.SetColor("_BaseColor", Color.white);
        material.SetTexture("_BumpMap", Texture(surfaceFamily, "Normal"));
        material.EnableKeyword("_NORMALMAP");
        SetFloat(material, "_BumpScale", 0.45f);
        material.SetTexture("_MetallicGlossMap", Texture(surfaceFamily, "MetallicSmoothness"));
        material.EnableKeyword("_METALLICSPECGLOSSMAP");
        SetFloat(material, "_WorkflowMode", 1f);
        SetFloat(material, "_Smoothness", 1f);
        SetFloat(material, "_SmoothnessTextureChannel", 0f);
        material.SetTexture("_OcclusionMap", Texture(surfaceFamily, "AO"));
        material.EnableKeyword("_OCCLUSIONMAP");
        SetFloat(material, "_OcclusionStrength", 0.5f);
        bool glass = family == "Glass";
        SetFloat(material, "_Surface", glass ? 1f : 0f);
        SetFloat(material, "_Blend", 0f);
        SetFloat(material, "_ZWrite", glass ? 0f : 1f);
        SetFloat(material, "_SrcBlend", glass ? (float)BlendMode.SrcAlpha : (float)BlendMode.One);
        SetFloat(material, "_DstBlend", glass ? (float)BlendMode.OneMinusSrcAlpha : (float)BlendMode.Zero);
        SetFloat(material, "_Cull", glass ? (float)CullMode.Off : (float)CullMode.Back);
        material.SetOverrideTag("RenderType", glass ? "Transparent" : "Opaque");
        material.renderQueue = glass ? (int)RenderQueue.Transparent : -1;
        if (glass)
        {
            material.SetColor("_BaseColor", new Color(0.92f, 0.98f, 0.98f, 0.18f));
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetShaderPassEnabled("ShadowCaster", false);
        }
        else material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        if (family == "Display" || family == "LightDiffuser")
        {
            material.SetTexture("_EmissionMap", Texture(baseFamily, "BaseColor"));
            material.SetColor("_EmissionColor", Color.white * (family == "Display" ? 0.28f : 3.2f));
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
        }
        EditorUtility.SetDirty(material);
        return material;
    }

    [MenuItem("Tools/Office Classic/Prepare Materials and Prefab")]
    public static void Prepare()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) throw new InvalidOperationException("Open this asset in a configured Unity URP project first.");
        string modelPath = Root + "/Models/SCN_Office_01.fbx";
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (model == null) throw new FileNotFoundException("Copy Unity/OfficeClassic to Assets/OfficeClassic first.", modelPath);
        Directory.CreateDirectory(Root + "/Materials");
        Directory.CreateDirectory(Root + "/Prefabs");
        AssetDatabase.Refresh();
        var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
        var names = model.GetComponentsInChildren<Renderer>(true)
            .SelectMany(r => r.sharedMaterials).Where(m => m != null).Select(m => m.name).Distinct().ToArray();
        foreach (string name in names)
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), BuildMaterial(name, shader));
        AssetDatabase.SaveAssets();
        importer.SaveAndReimport();
        model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        try
        {
            instance.name = "SCN_Office_01";
            PrefabUtility.SaveAsPrefabAsset(instance, Root + "/Prefabs/SCN_Office_01.prefab");
        }
        finally { UnityEngine.Object.DestroyImmediate(instance); }
        AssetDatabase.SaveAssets();
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/SCN_Office_01.prefab");
        Debug.Log("Office Classic: URP materials and grouped prefab created. Add project-specific lighting, colliders and door interaction in Unity.");
    }
}
