// 路线指引系统——场景障碍扫描器（IObstacleProvider 默认实现）。
// 设计文档：《路线指引系统架构设计.md》3.2 节。
//
// 从场景根「场景」收集静态碰撞体，投影为 XZ 占用栅格：
//   - 高度过滤：只取包围盒与玩家行进高度带 [0.1m, 1.8m] 相交的碰撞体
//     （不过滤的话天花板（≈2.7m）会把整张图封死）；
//   - 排除清单：可交互物（门板/把手会转、可抓取物会被拿走），按组件识别；
//   - 每个碰撞体外扩 padding 再投影（留出玩家胶囊半径余量）。
// 栅格构建 <10ms（办公室 765 碰撞体实测规模），进关时构建一次并缓存；
// ObstaclesChanged 预留给门开合等动态变化（v2 接入）。
using System;
using UnityEngine;

public class SceneObstacleScanner : MonoBehaviour, IObstacleProvider
{
    [Tooltip("场景内容根物体名（与 InteractionSetup 约定一致）")]
    [SerializeField] private string sceneRootName = "场景";
    [Tooltip("可跨过高度（米）：顶面低于此高度的碰撞体（地板厚板/地毯/门槛）视为可通行——" +
             "玩家 CharacterController stepOffset=0.3 可直接迈过")]
    [SerializeField] private float walkOverHeight = 0.35f;
    [Tooltip("行进高度带上限（米）：高于此带的碰撞体（天花板/吊件）不阻挡")]
    [SerializeField] private float maxHeight = 1.8f;
    [Tooltip("单个障碍的最大投影面积（m²）：超过的判定为建筑外壳/整片楼板之类的整体包围盒，" +
             "不是真实家具，忽略并记名（实测办公室曾因此被封死 97% 格子）")]
    [SerializeField] private float maxObstacleArea = 40f;
    [Tooltip("障碍投影外扩（米）：为玩家胶囊半径留余量")]
    [SerializeField] private float padding = 0.1f;

    /// <summary>障碍数据失效通知（门开合、家具移动等动态变化时触发），规划侧会重建栅格</summary>
    public event Action ObstaclesChanged;

    /// <summary>外部（如门控制器）在障碍状态变化后调用，通知规划侧重建栅格并重算当前路线</summary>
    public void NotifyObstaclesChanged() => ObstaclesChanged?.Invoke();

    private Transform sceneRootCache;

    /// <summary>场景内容的 XZ 包围范围（扩 2m 余量），供规划侧确定栅格覆盖区域</summary>
    public Bounds GetSceneBounds()
    {
        Transform root = FindSceneRoot();
        Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
        if (root == null) return bounds;
        bool first = true;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (first) { bounds = r.bounds; first = false; }
            else bounds.Encapsulate(r.bounds);
        }
        bounds.Expand(2f);
        return bounds;
    }

    public OccupancyGrid BuildGrid(float cellSize, Bounds area)
    {
        int width = Mathf.Max(1, Mathf.CeilToInt(area.size.x / cellSize));
        int height = Mathf.Max(1, Mathf.CeilToInt(area.size.z / cellSize));
        var grid = new OccupancyGrid(cellSize, new Vector2(area.min.x, area.min.z), width, height);

        Transform root = FindSceneRoot();
        int projected = 0, skippedLow = 0, skippedHigh = 0, skippedInteractive = 0;
        var bigSkipped = new System.Text.StringBuilder();
        var largest = new System.Collections.Generic.List<(string name, float area)>();
        if (root != null)
        {
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c.isTrigger) continue;

                // 排除会动的交互物：门板/把手（旋转）、可抓取物（会被拿走）
                if (IsInteractive(c.transform)) { skippedInteractive++; continue; }

                Bounds b = c.bounds;
                // 顶面低于可跨过高度：地板厚板/地毯/门槛——直接迈过，不算障碍
                if (b.max.y < walkOverHeight) { skippedLow++; continue; }
                // 高于行进高度带：天花板/吊件，不阻挡
                if (b.min.y > maxHeight) { skippedHigh++; continue; }

                // 超大面积的“障碍”是建筑外壳/楼板的整体包围盒（AABB 会把环形外壳算成整块），
                // 不是真实家具：忽略并记名，防止整图被封死
                float areaXZ = (b.max.x - b.min.x + padding * 2f) * (b.max.z - b.min.z + padding * 2f);
                if (areaXZ > maxObstacleArea)
                {
                    bigSkipped.Append(c.name).Append('(').Append(areaXZ.ToString("F0")).Append("m²) ");
                    continue;
                }

                grid.BlockRect(b.min.x - padding, b.min.z - padding, b.max.x + padding, b.max.z + padding);
                projected++;
                largest.Add((c.name, areaXZ));
            }
        }
        largest.Sort((a, b2) => b2.area.CompareTo(a.area));
        string top = "";
        for (int i = 0; i < Mathf.Min(3, largest.Count); i++)
            top += largest[i].name + "(" + largest[i].area.ToString("F1") + "m²) ";

        Debug.Log("[Guidance] 栅格构建完成：投射障碍 " + projected + "（最大：" + top + "），可跨跳过 " + skippedLow
                  + "，高处跳过 " + skippedHigh + "，交互物跳过 " + skippedInteractive
                  + (bigSkipped.Length > 0 ? "，超大外壳忽略：" + bigSkipped : "")
                  + "，阻挡格 " + grid.BlockedCount + "/" + (width * height), this);
        return grid;
    }

    // 沿父链查交互组件（门板上有 DoorController、把手上是 InteractableRelay、道具上是 PickupItem）
    private static bool IsInteractive(Transform t)
    {
        for (Transform cur = t; cur != null; cur = cur.parent)
        {
            if (cur.GetComponent<DoorController>() != null) return true;
            if (cur.GetComponent<InteractableRelay>() != null) return true;
            if (cur.GetComponent<PickupItem>() != null) return true;
            // 「场景」根即停（根上的组件不算交互物）
            if (cur.name == "场景" || cur.parent == null) break;
        }
        return false;
    }

    private Transform FindSceneRoot()
    {
        if (sceneRootCache != null) return sceneRootCache;
        foreach (Transform root in GetSceneRootsHelper())
            if (root.name == sceneRootName) { sceneRootCache = root; return root; }
        return null;
    }

    private static Transform[] GetSceneRootsHelper()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        var result = new Transform[roots.Length];
        for (int i = 0; i < roots.Length; i++) result[i] = roots[i].transform;
        return result;
    }
}
