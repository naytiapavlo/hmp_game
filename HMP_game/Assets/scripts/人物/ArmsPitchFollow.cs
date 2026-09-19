// 第一人称手臂俯仰跟随：挂在 FPHands_01 根物体上（由 InteractionSetup 自动挂载）。
//
// 核心问题（v3 修）：body 的缩放是非均匀的 (0.5,1,0.5)。在非均匀缩放的父级下做旋转
// 会被“剪切”——手臂旋转时变形、位置漂移，挂在锚点上的持物也会被压扁（丢出后
// SetParent 保留坏缩放 → 水杯扁平）。
// 解法：Awake 时新建一个均匀缩放的 ArmsPivot 挂到「人物」根下（绕开 body），
// 把手臂整体挪进去（世界外观不变），之后所有 yaw/俯仰旋转都作用在 pivot 上——
// 祖先缩放全部均匀，旋转是纯刚体变换，无任何剪切。Play 模式内的重挂不落盘。
//
// 旋转规则：pivot 原点放在眼睛位置，每帧
//   pivot.rotation = bodyYaw × 俯仰角×followFactor
//   pivot.position = 当前眼睛位置
// 手臂作为子物体绕眼睛旋转，屏幕位置在任意俯仰角下与平视时一致（等效挂在相机上），
// 断面在任何角度都保持平视时的遮挡关系。Interactor 的持物锚点也挂到 pivot 下
// （见 Interactor.CreateAnchors），持物与手同步且不会被压扁。
using UnityEngine;

public class ArmsPitchFollow : MonoBehaviour
{
    [Tooltip("俯仰跟随比例（0~1）：1=完全跟随（屏幕位置完全锁定），0=完全不跟")]
    [SerializeField] private float followFactor = 0.85f;
    [Tooltip("低头时手臂整体额外下沉量（米/度）")]
    [SerializeField] private float sinkPerDegree = 0.0015f;
    [Tooltip("俯仰死区（度）：小范围抬头低头手臂不动，减少晃动感")]
    [SerializeField] private float deadZone = 6f;

    [Header("持物锚点贴合（真实手部）")]
    [Tooltip("单手物品锚点沿掌心方向离腕骨的距离（米）：0=腕关节，约 0.07=掌心")]
    [SerializeField] private float handGripOffset = 0.07f;
    [Tooltip("双手物品锚点离双腕中点向前的距离（米）")]
    [SerializeField] private float carryGripOffset = 0.10f;

    /// <summary>均匀缩放旋转支点（Awake 时创建）；Interactor 的持物锚点挂这里</summary>
    public Transform Pivot { get; private set; }

    private body playerBody;
    private Transform playerCameraT;
    private HandPoseController hands;       // 与本组件同挂在 FPHands_01 上
    private Transform rightHandAnchor;      // Pivot 下的两个持物锚点（懒查找）
    private Transform carryAnchor;

    private void Awake()
    {
        playerBody = GetComponentInParent<body>();
        if (playerBody == null)
        {
            Debug.LogError("[ArmsPitchFollow] 父层级里找不到 body，手臂俯仰跟随不生效。", this);
            enabled = false;
            return;
        }
        Camera cam = playerBody.PlayerCamera;
        playerCameraT = cam != null ? cam.transform : null;
        hands = GetComponent<HandPoseController>(); // 同物体上的姿态控制器，用于锚点贴合手部

        // 新建均匀缩放支点挂「人物」根下（缩放 1），绕开 body 的非均匀缩放；
        // 人物不存在时挂场景根，同样是均匀缩放
        Transform rigRoot = playerBody.transform.parent;
        Pivot = new GameObject("ArmsPivot").transform;
        Pivot.SetParent(rigRoot, false);
        Pivot.position = playerCameraT != null ? playerCameraT.position
                        : playerBody.transform.position + Vector3.up * 0.65f;
        Pivot.rotation = playerBody.transform.rotation; // 对齐当前 yaw，保证挪入后外观不变

        // 手臂整体挪入 pivot（保留世界变换，外观零变化）
        transform.SetParent(Pivot, true);
    }

    private void LateUpdate()
    {
        if (Pivot == null || playerBody == null) return;

        // 死区外才跟随，减去死区宽度避免边界跳变
        float pitch = playerBody.Pitch;
        float effective = 0f;
        if (pitch > deadZone) effective = pitch - deadZone;
        else if (pitch < -deadZone) effective = pitch + deadZone;

        // yaw 完全跟随 body；俯仰按比例跟随。支点原点即眼睛位置，
        // 手臂绕眼睛整体旋转 → 屏幕位置在任意俯仰角下保持一致
        Pivot.rotation = playerBody.transform.rotation * Quaternion.Euler(effective * followFactor, 0f, 0f);
        Pivot.position = playerCameraT != null ? playerCameraT.position : Pivot.position;

        // 低头微沉辅助（世界向下小幅位移）
        float sink = Mathf.Clamp(-effective, 0f, 90f) * sinkPerDegree;
        if (sink > 0f) Pivot.position += Vector3.down * sink;

        TrackAnchorsToHands();
    }

    // 持物锚点贴合真实手部：单手物 = 右腕骨沿掌心方向前移 handGripOffset；
    // 双手物 = 双腕中点沿平均掌心方向前移 carryGripOffset。锚点旋转保持随支点
    // （物品在视野里直立），位置跟手——物品自然坐在手心，不漂浮不穿模，
    // 且手势（推门/扶椅）摆动腕骨时物品跟着手一起动。
    private void TrackAnchorsToHands()
    {
        if (hands == null || Pivot == null) return;
        if (rightHandAnchor == null) { Transform t = Pivot.Find("Anchor_RHand"); if (t != null) rightHandAnchor = t; }
        if (carryAnchor == null) { Transform t = Pivot.Find("Anchor_Carry"); if (t != null) carryAnchor = t; }

        if (rightHandAnchor != null && hands.TryGetHandFrame(true, out Vector3 wristR, out Vector3 palmR))
            rightHandAnchor.position = wristR + palmR * handGripOffset;

        if (carryAnchor != null
            && hands.TryGetHandFrame(false, out Vector3 wristL, out Vector3 palmL)
            && hands.TryGetHandFrame(true, out Vector3 wristR2, out Vector3 palmR2))
        {
            Vector3 mid = (wristL + wristR2) * 0.5f;
            Vector3 palmAvg = palmL + palmR2;
            if (palmAvg.sqrMagnitude < 1e-6f) palmAvg = Vector3.forward;
            carryAnchor.position = mid + palmAvg.normalized * carryGripOffset;
        }
    }
}
