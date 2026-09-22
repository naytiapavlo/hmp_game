// 道具几何小工具：把「某个语义部件在物品局部空间里的位置/尺寸」算准。
//
// 为什么不能用语义节点的 transform：
//   Tripo/Blender 交付件的部件节点 transform 常常**全在原点**，几何偏移烘在网格顶点里。
//   实测灭火器：CarryHandle 节点局部坐标 = (0,0,0)，而提把网格实际在局部 (0, 0.418, -0.079)。
//   按节点 transform 反推持握点会算出 (0,0,0) → 瓶身被"穿"在手腕上、整瓶向上顶到脸前，
//   手臂直接看不见了（2026-09-22 实测踩过）。
//   所以一律优先用**渲染包围盒**求几何中心；部件没有渲染体（纯空挂点，如 Nozzle）才退回它的 transform。
using UnityEngine;

public static class PropGeometry
{
    /// <summary>按名查找后代（含自身）。找不到返回 null。</summary>
    public static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform hit = FindDeep(root.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>一组渲染体的合并世界包围盒；没有渲染体时返回零尺寸盒。</summary>
    public static Bounds WorldBounds(Renderer[] rends)
    {
        if (rends == null || rends.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
        return b;
    }

    /// <summary>把渲染体合并包围盒折算到 target 的局部空间。
    /// 取世界盒的 8 个角点再变换（只变换 min/max 两个角，子物体带旋转时会算小）。</summary>
    public static Bounds LocalBoundsOf(Renderer[] rends, Transform target)
    {
        if (rends == null || rends.Length == 0 || target == null)
            return new Bounds(Vector3.zero, Vector3.zero);

        Bounds world = WorldBounds(rends);
        Vector3 c = world.center, e = world.extents;
        Vector3 min = Vector3.zero, max = Vector3.zero;
        int n = 0;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 p = target.InverseTransformPoint(new Vector3(c.x + e.x * sx, c.y + e.y * sy, c.z + e.z * sz));
                    if (n++ == 0) { min = p; max = p; }
                    else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                }
        return new Bounds((min + max) * 0.5f, max - min);
    }

    /// <summary>部件在 root 局部空间里的几何中心：优先级 渲染包围盒中心 → 节点 transform 位置。
    /// usedGeometry 表示是否真的用了几何（false = 该部件没有渲染体，退回节点 transform）。
    /// 返回 false = 传入的引用为空。</summary>
    public static bool TryGetPartCenterLocal(Transform root, Transform part, out Vector3 centerLocal, out bool usedGeometry)
    {
        centerLocal = Vector3.zero;
        usedGeometry = false;
        if (root == null || part == null) return false;

        Renderer[] rends = part.GetComponentsInChildren<Renderer>(true);
        if (rends.Length > 0)
        {
            Bounds lb = LocalBoundsOf(rends, root);
            if (lb.size.sqrMagnitude > 1e-8f)      // 渲染体存在但尺寸为 0（无网格/被裁）时也不能信
            {
                centerLocal = lb.center;
                usedGeometry = true;
                return true;
            }
        }
        centerLocal = root.InverseTransformPoint(part.position);
        return true;
    }

    /// <summary>按名查找部件并返回它的几何中心（见 TryGetPartCenterLocal）。</summary>
    public static bool TryGetPartCenterLocal(Transform root, string partName, out Vector3 centerLocal, out bool usedGeometry)
    {
        return TryGetPartCenterLocal(root, FindDeep(root, partName), out centerLocal, out usedGeometry);
    }
}
