// 路线指引系统——门面组件（对关卡层与 UI 层的唯一入口）。
// 设计文档：《路线指引系统架构设计.md》第二节。
//
// 用法：
//   GuidanceSystem.Find().ShowRoute(目的地世界坐标, "目的地名称");  // 显示/换路线
//   GuidanceSystem.Find().HideRoute();                              // 隐藏
//   OnDestinationReached 事件：玩家水平距离进入 arriveRadius 时触发并自动隐藏
//   （关卡层据此推进状态，如第一关“取到物品判定成功”）。
//
// 内部职责：进关构建一次障碍栅格并缓存；玩家偏离路径 >0.6m 或目的地移动时节流重算；
// 规划失败（真被围死）退化为直线箭头指向目标并告警，不阻断玩法。
// 调试：debugMode 开启时按 F8，准星指向的地面点生成一条路线（全屋巡检用）。
using System;
using UnityEngine;
using UnityEngine.InputSystem;

public class GuidanceSystem : MonoBehaviour
{
    [Header("规划参数")]
    [Tooltip("占用栅格边长（米）")]
    [SerializeField] private float cellSize = 0.25f;
    [Tooltip("玩家偏离当前路径超过该距离（米）触发重算")]
    [SerializeField] private float rerouteDeviation = 0.6f;
    [Tooltip("重算检查间隔（秒）")]
    [SerializeField] private float rerouteInterval = 0.5f;
    [Tooltip("到达判定半径（米，水平距离）")]
    [SerializeField] private float arriveRadius = 1.2f;

    [Header("引用（装配工具自动接线；缺省时运行时自建兜底）")]
    [SerializeField] private SceneObstacleScanner scanner;
    [SerializeField] private FloorArrowRenderer pathRenderer;
    [SerializeField] private DestinationMarker destinationMarker;
    [SerializeField] private Material guidanceMaterial;

    [Header("调试")]
    [Tooltip("开启后 Play 中按 F8：准星指向的地面点生成一条指引路线（全屋巡检用）")]
    [SerializeField] private bool debugMode = true;

    /// <summary>到达目的地（水平距离 &lt; arriveRadius）。参数：目的地坐标、目的地名称</summary>
    public event Action<Vector3, string> OnDestinationReached;

    private OccupancyGrid grid;
    private bool gridDirty = true;
    private Vector3 currentDestination;
    private string currentLabel = "";
    private Vector3 plannedDestination;
    private Vector3 planStart;          // 上次规划时的玩家位置（折线起点）
    private Vector3[] currentPath = Array.Empty<Vector3>();
    private bool active;
    private bool arrivedFired;
    private bool loggedFallback;
    private float nextCheckTime;
    private Transform playerCache;
    private bool explicitPlayer;
    public bool HasPlannedPath { get; private set; }
    public void BindPlayer(Transform value) { playerCache = value; explicitPlayer = true; }

    /// <summary>找到当前场景的指引系统（每场景一个，由装配工具挂载）</summary>
    public static GuidanceSystem Find() => FindFirstObjectByType<GuidanceSystem>();

    private void Awake()
    {
        EnsureReferences();
        if (scanner != null)
        {
            scanner.ObstaclesChanged += OnObstaclesChanged;
        }
    }

    /// <summary>显示/切换路线：目的地世界坐标 + 显示名称</summary>
    public void ShowRoute(Vector3 destinationWorldPos, string label)
    {
        currentDestination = destinationWorldPos;
        currentLabel = label ?? "";
        active = true;
        arrivedFired = false;
        loggedFallback = false;
        EnsureGrid();
        PlanAndApply();
        Debug.Log("[Guidance] 路线显示 → " + currentLabel + " @" + destinationWorldPos.ToString("F1"), this);
    }

    /// <summary>隐藏路线</summary>
    public void HideRoute()
    {
        active = false;
        if (pathRenderer != null) pathRenderer.Clear();
        if (destinationMarker != null) destinationMarker.Hide();
    }

    private void Update()
    {
        // 调试：F8 准星指向处生成路线
        if (!HMProtection.UI.QuizLoadingOverlay.IsVisible && debugMode && Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame) DebugPickTarget();
        if (!active) return;

        Transform player = Player();
        if (player == null) return;

        // 到达判定（水平距离）
        Vector2 dxz = new Vector2(player.position.x - currentDestination.x, player.position.z - currentDestination.z);
        if (!arrivedFired && dxz.magnitude <= arriveRadius)
        {
            arrivedFired = true;
            Debug.Log("[Guidance] 到达目的地：" + currentLabel, this);
            OnDestinationReached?.Invoke(currentDestination, currentLabel);
            HideRoute();
            return;
        }

        // 跟随重算（节流）：目的地移动 或 玩家偏离折线
        if (Time.unscaledTime < nextCheckTime) return;
        nextCheckTime = Time.unscaledTime + rerouteInterval;
        bool destMoved = (currentDestination - plannedDestination).sqrMagnitude > 0.09f;
        bool deviated = DistanceToPolyline(player.position) > rerouteDeviation;
        if (destMoved || deviated) PlanAndApply();
    }

    // ==== 内部 ====

    private void OnObstaclesChanged()
    {
        gridDirty = true;
        if (active) PlanAndApply();
    }

    private void PlanAndApply()
    {
        HasPlannedPath = false;
        Transform player = Player();
        if (player == null) return;
        EnsureGrid();

        Vector3 start = player.position;
        if (RoutePlanner.TryPlanPath(start, currentDestination, grid, out Vector3[] waypoints))
        {
            HasPlannedPath = true;
            currentPath = waypoints;
            planStart = start;
            pathRenderer.BuildPath(start, waypoints);
            loggedFallback = false;
        }
        else
        {
            // 兜底：真不可达（被围死）→ 直线箭头指向目标 + 告警一次，不阻断玩法
            if (!loggedFallback)
            {
                Debug.LogWarning("[Guidance] 未找到可行路径（目的地被围死？），退化为直线指引 → " + currentLabel, this);
                loggedFallback = true;
            }
            currentPath = new[] { currentDestination };
            planStart = start;
            pathRenderer.BuildPath(start, currentPath);
        }
        plannedDestination = currentDestination;
        if (destinationMarker != null) destinationMarker.SetTarget(currentDestination, currentLabel);
    }

    private void EnsureGrid()
    {
        if (!gridDirty && grid != null) return;
        if (scanner == null) return;
        grid = scanner.BuildGrid(cellSize, scanner.GetSceneBounds());
        gridDirty = false;
        // 诊断：阻挡格为 0 = 障碍扫描失效（路线会退化为直线），必须排查
        Debug.Log("[Guidance] 规划栅格就绪：阻挡格 " + grid.BlockedCount + " / 总格 "
                  + (grid.Width * grid.Height), this);
    }

    // 玩家到当前折线（planStart → 各路点）的 XZ 最短距离
    private float DistanceToPolyline(Vector3 point)
    {
        if (currentPath.Length == 0) return float.MaxValue;
        float best = float.MaxValue;
        Vector3 prev = planStart;
        for (int i = 0; i < currentPath.Length; i++)
        {
            best = Mathf.Min(best, DistanceToSegment(point, prev, currentPath[i]));
            prev = currentPath[i];
        }
        return best;
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector2 ab = new Vector2(b.x - a.x, b.z - a.z);
        Vector2 ap = new Vector2(p.x - a.x, p.z - a.z);
        float abSqr = ab.sqrMagnitude;
        float t = abSqr > 0.0001f ? Mathf.Clamp01(Vector2.Dot(ap, ab) / abSqr) : 0f;
        Vector2 closest = new Vector2(a.x, a.z) + ab * t;
        Vector2 d = new Vector2(p.x, p.z) - closest;
        return d.magnitude;
    }

    private Transform Player()
    {
        if (playerCache != null) return playerCache;
        if (explicitPlayer) return null;
        GameObject body = GameObject.Find("body");
        if (body != null) playerCache = body.transform;
        return playerCache;
    }

    // 调试：准星射线打到的第一个表面生成路线（注意：打到家具上时目的地就在家具上，
    // 路线会止步于家具旁的可达格——这是正确行为，不是 bug）
    private void DebugPickTarget()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, 20f))
        {
            Debug.Log("[Guidance] F8 调试：命中 " + hit.collider.name + " @" + hit.point.ToString("F1"), this);
            ShowRoute(hit.point, "调试目标");
        }
    }

    // 引用兜底：装配工具没接线的极端情况下运行时自建（正常流程不走到）
    private void EnsureReferences()
    {
        if (scanner == null) scanner = gameObject.GetComponent<SceneObstacleScanner>();
        if (scanner == null) scanner = gameObject.AddComponent<SceneObstacleScanner>();

        if (pathRenderer == null)
        {
            var child = transform.Find("路线渲染");
            if (child == null)
            {
                child = new GameObject("路线渲染").transform;
                child.SetParent(transform, false);
            }
            pathRenderer = child.GetComponent<FloorArrowRenderer>();
            if (pathRenderer == null) pathRenderer = child.gameObject.AddComponent<FloorArrowRenderer>();
        }

        if (destinationMarker == null)
        {
            var child = transform.Find("目的地标记");
            if (child == null)
            {
                child = new GameObject("目的地标记").transform;
                child.SetParent(transform, false);
            }
            destinationMarker = child.GetComponent<DestinationMarker>();
            if (destinationMarker == null) destinationMarker = child.gameObject.AddComponent<DestinationMarker>();
        }
    }
}
