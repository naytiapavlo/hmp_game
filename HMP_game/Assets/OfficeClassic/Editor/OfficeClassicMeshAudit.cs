// Verification tool for the imported office model: counts triangles over ALL submeshes
// (a submesh-0-only count under-reports meshes that carry several materials), checks the
// hierarchy, the assigned material assets and the effective render pipeline.
// Run from Tools > Office Classic > Verify Imported Model.
// The report is written to Library/OfficeClassic_MeshAudit.json (Library/ is git-ignored).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class OfficeClassicMeshAudit
{
    private const string ModelPath = "Assets/OfficeClassic/Models/SCN_Office_01.fbx";
    private const string PrefabPath = "Assets/OfficeClassic/Prefabs/SCN_Office_01.prefab";
    private const string MaterialsDir = "Assets/OfficeClassic/Materials";
    private const string ReportPath = "Library/OfficeClassic_MeshAudit.json";

    private static string J(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 32) sb.Append(' ');
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static string F(float v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

    [MenuItem("Tools/Office Classic/Verify Imported Model")]
    public static void Run()
    {
        try
        {
            WriteReport();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    private static void WriteReport()
    {
        var sb = new StringBuilder();
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        GameObject target = prefab != null ? prefab : model;

        sb.Append("{\n");
        sb.Append("  \"inspected\": ").Append(J(prefab != null ? "prefab" : "model")).Append(",\n");
        sb.Append("  \"rootName\": ").Append(J(target != null ? target.name : null)).Append(",\n");
        sb.Append("  \"qualityLevel\": ").Append(J(QualitySettings.names.Length > QualitySettings.GetQualityLevel()
            ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "?")).Append(",\n");
        sb.Append("  \"qualitySettings.renderPipeline\": ").Append(J(QualitySettings.renderPipeline != null
            ? QualitySettings.renderPipeline.GetType().Name + ":" + QualitySettings.renderPipeline.name : "null")).Append(",\n");
        sb.Append("  \"GraphicsSettings.currentRenderPipeline\": ").Append(J(GraphicsSettings.currentRenderPipeline != null
            ? GraphicsSettings.currentRenderPipeline.GetType().Name + ":" + GraphicsSettings.currentRenderPipeline.name : "null")).Append(",\n");

        if (target == null)
        {
            sb.Append("  \"error\": \"target not loadable\"\n}\n");
            File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
            return;
        }

        MeshFilter[] filters = target.GetComponentsInChildren<MeshFilter>(true);
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        Transform[] all = target.GetComponentsInChildren<Transform>(true);

        long trisAllSubmeshes = 0;
        long trisSubmesh0 = 0;
        long vertsFilterSum = 0;
        long trisDistinct = 0;
        long vertsDistinct = 0;
        var seen = new HashSet<string>();
        var multiSubmesh = new List<string>();
        var meshRows = new List<string>();
        var meshNames = new HashSet<string>();

        foreach (MeshFilter f in filters)
        {
            Mesh m = f.sharedMesh;
            if (m == null) continue;
            long t0 = m.GetIndexCount(0) / 3;
            long tall = 0;
            for (int s = 0; s < m.subMeshCount; s++) tall += m.GetIndexCount(s) / 3;
            trisSubmesh0 += t0;
            trisAllSubmeshes += tall;
            vertsFilterSum += m.vertexCount;
            if (m.subMeshCount != 1) multiSubmesh.Add(m.name + " (submeshes=" + m.subMeshCount + ", tris=" + tall + ")");
            string meshKey;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string meshGuid, out long meshLocalId))
                meshKey = meshGuid + ":" + meshLocalId;
            else
                meshKey = "name:" + m.name;
            if (seen.Add(meshKey))
            {
                trisDistinct += tall;
                vertsDistinct += m.vertexCount;
            }
            if (meshNames.Add(f.gameObject.name))
                meshRows.Add("    {\"name\": " + J(f.gameObject.name) + ", \"tris\": " + tall
                             + ", \"verts\": " + m.vertexCount + ", \"submeshes\": " + m.subMeshCount + "}");
        }

        bool hasBounds = false;
        Bounds b = new Bounds();
        foreach (Renderer r in renderers)
        {
            if (!hasBounds) { b = r.bounds; hasBounds = true; } else b.Encapsulate(r.bounds);
        }

        sb.Append("  \"meshFilterCount\": ").Append(filters.Length).Append(",\n");
        sb.Append("  \"rendererCount\": ").Append(renderers.Length).Append(",\n");
        sb.Append("  \"transformCount\": ").Append(all.Length).Append(",\n");
        sb.Append("  \"distinctMeshCount\": ").Append(seen.Count).Append(",\n");
        sb.Append("  \"trianglesAllSubmeshes\": ").Append(trisAllSubmeshes).Append(",\n");
        sb.Append("  \"trianglesSubmesh0Only\": ").Append(trisSubmesh0).Append(",\n");
        sb.Append("  \"trianglesDistinctMeshes\": ").Append(trisDistinct).Append(",\n");
        sb.Append("  \"vertsFilterSum\": ").Append(vertsFilterSum).Append(",\n");
        sb.Append("  \"vertsDistinctMeshes\": ").Append(vertsDistinct).Append(",\n");
        sb.Append("  \"boundsMin\": [").Append(F(b.min.x)).Append(", ").Append(F(b.min.y)).Append(", ").Append(F(b.min.z)).Append("],\n");
        sb.Append("  \"boundsMax\": [").Append(F(b.max.x)).Append(", ").Append(F(b.max.y)).Append(", ").Append(F(b.max.z)).Append("],\n");
        sb.Append("  \"meshesWithMultipleSubmeshes\": [")
          .Append(string.Join(", ", multiSubmesh.Select(J).ToArray())).Append("],\n");
        sb.Append("  \"groupAnchors\": ").Append(all.Count(t => t.name.StartsWith("GRP_", StringComparison.Ordinal))).Append(",\n");
        sb.Append("  \"ceilingMeshObjects\": ").Append(all.Count(t => t.name.StartsWith("SM_Ceiling", StringComparison.Ordinal))).Append(",\n");

        var matPaths = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null)
            .Select(m => AssetDatabase.GetAssetPath(m)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        sb.Append("  \"rendererMaterialAssetCount\": ").Append(matPaths.Count).Append(",\n");
        sb.Append("  \"rendererMaterialsInMaterialsFolder\": ")
          .Append(matPaths.Count(p => p.StartsWith(MaterialsDir + "/", StringComparison.Ordinal))).Append(",\n");
        sb.Append("  \"rendererMaterialAssetPaths\": [")
          .Append(string.Join(", ", matPaths.Select(J).ToArray())).Append("],\n");
        sb.Append("  \"shadersUsed\": [")
          .Append(string.Join(", ", renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null)
              .Select(m => m.shader != null ? m.shader.name : "null").Distinct().OrderBy(s => s, StringComparer.Ordinal)
              .Select(J).ToArray())).Append("],\n");
        sb.Append("  \"meshes\": [\n").Append(string.Join(",\n", meshRows.ToArray())).Append("\n  ]\n}\n");

        File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
        Debug.Log("OfficeClassicMeshAudit done: trianglesAllSubmeshes=" + trisAllSubmeshes
                  + " distinctMeshes=" + seen.Count + " report=" + ReportPath);
    }
}
