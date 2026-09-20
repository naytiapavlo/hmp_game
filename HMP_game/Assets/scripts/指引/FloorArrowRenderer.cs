// 路线指引系统——地面箭头带渲染。
// 设计文档：《路线指引系统架构设计.md》4.1 节；视觉基准 0c7a3b35-…png。
//
// 沿平滑路径等距铺设发光三角箭头（非连续条带，与参考图一致）：
//   - 第一个箭头在玩家脚下，最后一支指向目的地；
//   - 所有箭头合并为一个动态 Mesh（1 Draw Call），路径重算时重建顶点；
//   - 每个箭头位置向下射线贴地 + 抬升 0.03m 防 Z-fighting；
//   - uv.x = 箭头内尾→尖渐变，uv.y = 沿路径累计米数——流动脉动由着色器完成，CPU 每帧零开销。
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class FloorArrowRenderer : MonoBehaviour
{
    [Header("箭头尺寸（米）")]
    [Tooltip("箭头宽度")]
    [SerializeField] private float arrowWidth = 0.6f;
    [Tooltip("箭头长度（尖到尾）")]
    [SerializeField] private float arrowLength = 0.9f;
    [Tooltip("相邻箭头间距（沿路径弧长）")]
    [SerializeField] private float arrowSpacing = 1.1f;
    [Tooltip("离地抬升高度（防 Z-fighting）")]
    [SerializeField] private float lift = 0.03f;
    [Tooltip("箭头材质（HMProtection/GuidanceArrow，装配工具自动生成并赋值）")]
    [SerializeField] private Material arrowMaterial;

    private Mesh mesh;

    private void Awake()
    {
        mesh = new Mesh { name = "GuidanceArrowMesh" };
        mesh.MarkDynamic();
        GetComponent<MeshFilter>().sharedMesh = mesh;
        // 材质兜底：装配没赋值时按着色器名现做（正常流程不会走到）
        if (arrowMaterial == null)
        {
            Shader shader = Shader.Find("HMProtection/GuidanceArrow");
            if (shader != null) arrowMaterial = new Material(shader);
        }
        if (arrowMaterial != null) GetComponent<MeshRenderer>().sharedMaterial = arrowMaterial;
        Clear();
    }

    /// <summary>按路径重建箭头带。startPos = 玩家脚下（第一支箭头处），waypoints = 规划输出的路点。</summary>
    public void BuildPath(Vector3 startPos, Vector3[] waypoints)
    {
        if (waypoints == null || waypoints.Length == 0) { Clear(); return; }

        // 组装压平折线（起点 = 玩家脚下，y 归零只做平面规划；贴地由逐点射线处理）
        var flat = new List<Vector3>(waypoints.Length + 1) { new Vector3(startPos.x, 0f, startPos.z) };
        foreach (Vector3 p in waypoints) flat.Add(new Vector3(p.x, 0f, p.z));

        var segLens = new float[flat.Count - 1];
        float total = 0f;
        for (int i = 0; i < segLens.Length; i++)
        {
            segLens[i] = Vector3.Distance(flat[i], flat[i + 1]);
            total += segLens[i];
        }
        if (total < 0.2f) { Clear(); return; }

        var vertices = new List<Vector3>(256);
        var uvs = new List<Vector2>(256);
        var indices = new List<int>(256);

        // 沿弧长等距铺箭头，终点前 0.35m 停（给终点箭头让位，避免重叠）
        float stopAt = Mathf.Max(total - 0.35f, arrowSpacing * 0.5f);
        for (float d = 0f; d <= stopAt + 0.0001f; d += arrowSpacing)
        {
            SampleAt(flat, segLens, d, out Vector3 pos, out Vector3 dir);
            AddArrow(vertices, uvs, indices, pos, dir, d);
        }

        // 终点箭头：朝向 = 最后一段方向
        Vector3 lastDir = flat[flat.Count - 1] - flat[flat.Count - 2];
        if (lastDir.sqrMagnitude < 0.0001f) lastDir = Vector3.forward;
        AddArrow(vertices, uvs, indices, flat[flat.Count - 1], lastDir.normalized, total);

        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(indices, 0);
        // 顶点是世界坐标（物体保持原点单位姿态），手动扩包围盒防视锥剔除误差
        var bounds = mesh.bounds;
        bounds.Expand(0.5f);
        mesh.bounds = bounds;
        GetComponent<MeshRenderer>().enabled = true;
    }

    /// <summary>清空箭头带（隐藏）</summary>
    public void Clear()
    {
        if (mesh != null) mesh.Clear();
        if (TryGetComponent<MeshRenderer>(out var r)) r.enabled = false;
    }

    // 沿折线按弧长 d 采样位置与切线方向
    private static void SampleAt(List<Vector3> flat, float[] segLens, float d, out Vector3 pos, out Vector3 dir)
    {
        float acc = 0f;
        for (int i = 0; i < segLens.Length; i++)
        {
            if (d <= acc + segLens[i] || i == segLens.Length - 1)
            {
                float t = segLens[i] > 0.0001f ? Mathf.Clamp01((d - acc) / segLens[i]) : 0f;
                pos = Vector3.Lerp(flat[i], flat[i + 1], t);
                dir = (flat[i + 1] - flat[i]);
                if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
                dir.Normalize();
                return;
            }
            acc += segLens[i];
        }
        pos = flat[flat.Count - 1];
        dir = Vector3.forward;
    }

    // 生成一支箭头的三角面（世界坐标顶点，朝向 dir，贴地 + 抬升）
    private void AddArrow(List<Vector3> vertices, List<Vector2> uvs, List<int> indices,
                          Vector3 center, Vector3 dir, float pathDistance)
    {
        Vector3 ground = SnapToGround(center);
        Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
        float hw = arrowWidth * 0.5f;
        float hl = arrowLength * 0.5f;

        // 局部三角：尾部两肩（uv.x=0）+ 尖端（uv.x=1）；uv.y = 路径累计距离（流动波相位）
        Vector3 tailL = ground + rot * new Vector3(-hw, 0f, -hl);
        Vector3 tailR = ground + rot * new Vector3(hw, 0f, -hl);
        Vector3 tip = ground + rot * new Vector3(0f, 0f, hl);

        int baseIndex = vertices.Count;
        vertices.Add(tailL); uvs.Add(new Vector2(0f, pathDistance));
        vertices.Add(tailR); uvs.Add(new Vector2(0f, pathDistance));
        vertices.Add(tip); uvs.Add(new Vector2(1f, pathDistance));
        indices.Add(baseIndex); indices.Add(baseIndex + 1); indices.Add(baseIndex + 2);
    }

    // 向下射线贴地：从 1m 高处打 3m；打不到就贴 y=0 地面
    private Vector3 SnapToGround(Vector3 pos)
    {
        if (Physics.Raycast(new Vector3(pos.x, pos.y + 1f, pos.z), Vector3.down, out RaycastHit hit, 3f))
            return hit.point + Vector3.up * lift;
        return new Vector3(pos.x, lift, pos.z);
    }
}
