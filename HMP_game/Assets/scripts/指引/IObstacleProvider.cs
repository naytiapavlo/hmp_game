// 路线指引系统——障碍信息抽象与占用栅格数据结构。
// 设计文档：《路线指引系统架构设计.md》第一、三节。
//
// 接口契约（需求方指定）：路线生成的参数就是「目的地坐标 + 当前场景的障碍物信息」。
// IObstacleProvider 由各场景自行实现（默认实现 SceneObstacleScanner 从静态碰撞体收集），
// RoutePlanner 只消费 OccupancyGrid——换场景只换障碍数据，规划与渲染代码零改动。
using System;
using UnityEngine;

/// <summary>场景障碍信息抽象：由场景适配层实现，向规划层提供占用栅格。</summary>
public interface IObstacleProvider
{
    /// <summary>用障碍数据构建 XZ 平面占用栅格（cellSize 格边长，area 规划区域）</summary>
    OccupancyGrid BuildGrid(float cellSize, Bounds area);

    /// <summary>障碍数据失效通知（门开合、家具移动等动态变化时由实现方触发），规划侧应重建栅格</summary>
    event Action ObstaclesChanged;
}

/// <summary>
/// XZ 平面占用栅格：把世界坐标离散为格子，标记哪些格被障碍占据。
/// 纯数据结构、无 Unity 场景依赖，可供 EditMode 单元测试。
/// </summary>
public class OccupancyGrid
{
    public float CellSize { get; }
    public int Width { get; }    // X 方向格数
    public int Height { get; }   // Z 方向格数
    public Vector2 Origin { get; } // 世界坐标下左下角（minX, minZ）

    private readonly bool[] blocked;
    private int blockedCount;

    /// <summary>被标记为阻挡的格子总数（诊断用：为 0 说明障碍扫描失效）</summary>
    public int BlockedCount => blockedCount;

    public OccupancyGrid(float cellSize, Vector2 origin, int width, int height)
    {
        CellSize = Mathf.Max(cellSize, 0.01f);
        Origin = origin;
        Width = width;
        Height = height;
        blocked = new bool[width * height];
    }

    /// <summary>规划区域是否为空栅格（宽或高为 0）</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool InBounds(int cx, int cz) => cx >= 0 && cx < Width && cz >= 0 && cz < Height;

    /// <summary>该格是否阻挡（出界按阻挡处理，防止路径绕出规划区域）</summary>
    public bool IsBlocked(int cx, int cz) => !InBounds(cx, cz) || blocked[cz * Width + cx];

    /// <summary>世界坐标 → 格子坐标（不做合法性检查，配合 IsBlocked 使用）</summary>
    public void WorldToCell(Vector3 world, out int cx, out int cz)
    {
        cx = Mathf.FloorToInt((world.x - Origin.x) / CellSize);
        cz = Mathf.FloorToInt((world.z - Origin.y) / CellSize);
    }

    /// <summary>格子中心的世界坐标（Y 取传入值；规划只关心 XZ，贴地由渲染层处理）</summary>
    public Vector3 CellCenterWorld(int cx, int cz, float y)
    {
        return new Vector3(Origin.x + (cx + 0.5f) * CellSize, y, Origin.y + (cz + 0.5f) * CellSize);
    }

    /// <summary>把一个 XZ 矩形区域标记为阻挡（碰撞体包围盒投影 + 外扩 padding 后调用）。
    /// 下边界用 Floor（把包含边缘的格子也标上，宁可多挡不可漏挡）。</summary>
    public void BlockRect(float minX, float minZ, float maxX, float maxZ)
    {
        int x0 = Mathf.Max(0, Mathf.FloorToInt((minX - Origin.x) / CellSize));
        int z0 = Mathf.Max(0, Mathf.FloorToInt((minZ - Origin.y) / CellSize));
        int x1 = Mathf.Min(Width - 1, Mathf.FloorToInt((maxX - Origin.x) / CellSize));
        int z1 = Mathf.Min(Height - 1, Mathf.FloorToInt((maxZ - Origin.y) / CellSize));
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int i = z * Width + x;
                if (!blocked[i]) { blocked[i] = true; blockedCount++; }
            }
        }
    }

    /// <summary>被占格周围的环形搜索：找最近的空闲格（起/终点落在障碍里时救场）。</summary>
    /// <param name="maxRadius">最大搜索半径（格）</param>
    public bool FindNearestFree(int cx, int cz, int maxRadius, out int fx, out int fz)
    {
        if (!IsBlocked(cx, cz)) { fx = cx; fz = cz; return true; }
        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    // 只扫当前环的边（|dx|==r 或 |dz|==r），避免重复扫内部
                    if (Mathf.Abs(dx) != r && Mathf.Abs(dz) != r) continue;
                    if (!IsBlocked(cx + dx, cz + dz)) { fx = cx + dx; fz = cz + dz; return true; }
                }
            }
        }
        fx = cx; fz = cz;
        return false;
    }

    /// <summary>格线可视性（Bresenham 直线逐格检查）：两点间无阻挡格返回 true，用于路径平滑。</summary>
    public bool HasLineOfSight(int ax, int az, int bx, int bz)
    {
        int dx = Mathf.Abs(bx - ax);
        int dz = Mathf.Abs(bz - az);
        int sx = ax < bx ? 1 : -1;
        int sz = az < bz ? 1 : -1;
        int err = dx - dz;
        int x = ax, z = az;
        while (true)
        {
            if (IsBlocked(x, z)) return false;
            if (x == bx && z == bz) return true;
            int e2 = err * 2;
            if (e2 > -dz) { err -= dz; x += sx; }
            if (e2 < dx) { err += dx; z += sz; }
        }
    }
}
