// 路线指引系统——A* 寻路 + LOS 路径平滑（纯静态类，无场景依赖，可单测）。
// 设计文档：《路线指引系统架构设计.md》第三节。
//
// 算法要点：
//   - 8 邻域 A*，启发 = 欧氏距离，代价 = 实际移动距离；
//   - 禁止对角穿角：斜向移动要求两个相邻直行格均空闲（防止从两块家具的缝里斜穿）；
//   - LOS 平滑：Bresenham 可视性把 A* 折线合并为最少路点，消除网格锯齿；
//   - 起/终点落在阻挡格时环形搜索最近空闲格（贴墙目标常见，防止假性不可达）；
//   - y 坐标统一输出 0（规划只关心 XZ 平面；贴地高度由渲染层逐点射线处理）。
using System.Collections.Generic;
using UnityEngine;

public static class RoutePlanner
{
    /// <summary>
    /// 规划 start → destination 的地面路径。
    /// 返回 false = 真不可达（被围死）；waypoints 为平滑后的路径点（不含起点、含终点格中心）。
    /// </summary>
    public static bool TryPlanPath(Vector3 start, Vector3 destination, OccupancyGrid grid, out Vector3[] waypoints)
    {
        waypoints = null;
        if (grid == null || grid.IsEmpty) return false;

        grid.WorldToCell(start, out int sx, out int sz);
        grid.WorldToCell(destination, out int dx, out int dz);

        // 起终点救场：落在阻挡格 → 找最近空闲格
        int rescueRadius = Mathf.CeilToInt(0.6f / grid.CellSize) + 2;
        if (!grid.FindNearestFree(sx, sz, rescueRadius, out sx, out sz)) return false;
        if (!grid.FindNearestFree(dx, dz, rescueRadius, out dx, out dz)) return false;
        if (sx == dx && sz == dz)
        {
            waypoints = new[] { new Vector3(destination.x, 0f, destination.z) };
            return true;
        }

        // ---- A* 主循环（开表用小顶二元堆）----
        int w = grid.Width, h = grid.Height;
        int cellCount = w * h;
        float[] gScore = new float[cellCount];
        int[] cameFrom = new int[cellCount];
        bool[] closed = new bool[cellCount];
        for (int i = 0; i < cellCount; i++) { gScore[i] = float.MaxValue; cameFrom[i] = -1; }

        var heap = new MinHeap(cellCount);
        int startIndex = sz * w + sx;
        int destIndex = dz * w + dx;
        gScore[startIndex] = 0f;
        heap.Push(startIndex, Heuristic(sx, sz, dx, dz, grid.CellSize));

        bool reached = false;
        while (heap.Count > 0)
        {
            int current = heap.Pop();
            if (current == destIndex) { reached = true; break; }
            if (closed[current]) continue;
            closed[current] = true;

            int cx = current % w;
            int cz = current / w;
            for (int nz = -1; nz <= 1; nz++)
            {
                for (int nx = -1; nx <= 1; nx++)
                {
                    if (nx == 0 && nz == 0) continue;
                    int tx = cx + nx, tz = cz + nz;
                    if (grid.IsBlocked(tx, tz)) continue;
                    // 禁止对角穿角：斜走要求两个相邻直行格都空闲
                    if (nx != 0 && nz != 0 && (grid.IsBlocked(cx + nx, cz) || grid.IsBlocked(cx, cz + nz))) continue;

                    int nIndex = tz * w + tx;
                    if (closed[nIndex]) continue;
                    float step = (nx != 0 && nz != 0 ? 1.4142136f : 1f) * grid.CellSize;
                    float nG = gScore[current] + step;
                    if (nG < gScore[nIndex])
                    {
                        gScore[nIndex] = nG;
                        cameFrom[nIndex] = current;
                        heap.Push(nIndex, nG + Heuristic(tx, tz, dx, dz, grid.CellSize));
                    }
                }
            }
        }
        if (!reached) return false;

        // ---- 回溯路径（格坐标序列，起点→终点）----
        var cellPath = new List<int>();
        for (int i = destIndex; i != -1; i = cameFrom[i]) cellPath.Add(i);
        cellPath.Reverse();

        // ---- LOS 平滑：贪心跳到能直视的最远格 ----
        var smoothed = new List<Vector3>();
        int cursor = 0;
        while (cursor < cellPath.Count - 1)
        {
            int far = cursor + 1;
            for (int probe = cellPath.Count - 1; probe > cursor + 1; probe--)
            {
                int ax = cellPath[cursor] % w, az = cellPath[cursor] / w;
                int bx = cellPath[probe] % w, bz = cellPath[probe] / w;
                if (grid.HasLineOfSight(ax, az, bx, bz)) { far = probe; break; }
            }
            int c = cellPath[far];
            smoothed.Add(grid.CellCenterWorld(c % w, c / w, 0f));
            cursor = far;
        }

        // 终点精度处理：若“最后一个路点 → 目的地原始坐标”连线无阻挡，则把原始坐标
        // 追加为终点（箭头精确指到目标物）；有阻挡（目标在桌面/家具上）则停在可达格，
        // 防止最后一段直线爬上家具——此前“路线跨过桌子”的根因就是这里无脑替换。
        Vector3 rawDest = new Vector3(destination.x, 0f, destination.z);
        Vector3 last = smoothed[smoothed.Count - 1];
        if (GridLineClear(grid, last, rawDest, 0.5f))
        {
            if ((last - rawDest).sqrMagnitude > 0.04f) smoothed.Add(rawDest);
        }
        waypoints = smoothed.ToArray();
        return waypoints.Length > 0;
    }

    /// <summary>世界坐标连线检查：a→b 逐点采样（步长 ≤ 0.5 格）查占用，全程空闲返回 true。</summary>
    private static bool GridLineClear(OccupancyGrid grid, Vector3 a, Vector3 b, float stepMeters)
    {
        float step = Mathf.Min(stepMeters, grid.CellSize * 0.5f);
        Vector2 dir = new Vector2(b.x - a.x, b.z - a.z);
        float len = dir.magnitude;
        if (len < 0.0001f) return true;
        dir /= len;
        int samples = Mathf.CeilToInt(len / step);
        for (int i = 0; i <= samples; i++)
        {
            Vector2 p = new Vector2(a.x, a.z) + dir * Mathf.Min(i * step, len);
            grid.WorldToCell(new Vector3(p.x, 0f, p.y), out int cx, out int cz);
            if (grid.IsBlocked(cx, cz)) return false;
        }
        return true;
    }

    private static float Heuristic(int x, int z, int tx, int tz, float cellSize)
    {
        float dx = (x - tx) * cellSize;
        float dz = (z - tz) * cellSize;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>轻量小顶堆（A* 开表用）。惰性删除会让同一节点多次入堆，
    /// 实际入堆次数可达格子数的数倍，因此容量必须可增长——此前固定容量
    /// 满后静默丢弃导致大场景搜索半途失败、路线退化为直线。</summary>
    private sealed class MinHeap
    {
        private int[] indices;
        private float[] priorities;
        public int Count { get; private set; }

        public MinHeap(int capacity)
        {
            capacity = Mathf.Max(capacity, 256);
            indices = new int[capacity];
            priorities = new float[capacity];
        }

        public void Push(int index, float priority)
        {
            if (Count == indices.Length)
            {
                int newCapacity = indices.Length * 2;
                System.Array.Resize(ref indices, newCapacity);
                System.Array.Resize(ref priorities, newCapacity);
            }
            int i = Count++;
            indices[i] = index;
            priorities[i] = priority;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (priorities[parent] <= priorities[i]) break;
                Swap(parent, i);
                i = parent;
            }
        }

        public int Pop()
        {
            int result = indices[0];
            Count--;
            indices[0] = indices[Count];
            priorities[0] = priorities[Count];
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, smallest = i;
                if (l < Count && priorities[l] < priorities[smallest]) smallest = l;
                if (r < Count && priorities[r] < priorities[smallest]) smallest = r;
                if (smallest == i) break;
                Swap(smallest, i);
                i = smallest;
            }
            return result;
        }

        private void Swap(int a, int b)
        {
            (indices[a], indices[b]) = (indices[b], indices[a]);
            (priorities[a], priorities[b]) = (priorities[b], priorities[a]);
        }
    }
}
