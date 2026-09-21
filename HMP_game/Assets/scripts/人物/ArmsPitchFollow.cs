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

    [Header("持物锚点贴合（掌面坐标系）")]
    [Tooltip("单手物品锚点沿掌心方向（腕→中指根）离腕骨的距离（米）")]
    [SerializeField] private float gripAlongDist = 0.06f;
    [Tooltip("单手物品锚点沿掌面法线的偏移（米）：物品在手背一侧就翻正负号。（实测：-0.035 为掌侧）")]
    [SerializeField] private float gripNormalDist = -0.035f;
    [Tooltip("双手物品锚点离双腕中点沿掌心方向的距离（米）")]
    [SerializeField] private float carryGripOffset = 0.10f;

    [Header("手臂收纳（减少占屏，Play 模式实时调）")]
    [Tooltip("手臂根相对场景基准位置的偏移（支点局部空间，随俯仰一起转）：y 负=下沉，z 正=向前推（离镜头更远）；z 负=靠近镜头。（实测调优值）")]
    [SerializeField] private Vector3 armTuckOffset = new Vector3(0f, -0.2f, -0.2f);
    [Tooltip("手臂根相对基准姿态的附加旋转（度）：x 负=手臂向下俯、从视野中央收走")]
    [SerializeField] private Vector3 armTuckRotation = new Vector3(-12f, 0f, 0f);

    /// <summary>均匀缩放旋转支点（Awake 时创建）；Interactor 的持物锚点挂这里</summary>
    public Transform Pivot { get; private set; }

    /// <summary>视角俯仰（度）→ 去死区后的有效俯仰（度，**未乘** followFactor）。死区内为 0。</summary>
    public float EffectivePitch(float viewPitch)
    {
        if (viewPitch > deadZone) return viewPitch - deadZone;
        if (viewPitch < -deadZone) return viewPitch + deadZone;
        return 0f;
    }

    /// <summary>视角俯仰（度）→ 支点实际俯仰（度，含死区与 followFactor）。
    /// **两点持握**（右手持物 + 左手必须贴在物体另一处，如灭火器右拳握提把/左掌握喷头）时按一帧反算持物俯仰用这个，
    /// 不要用 body.Pitch：死区内手臂根本不转，而按 body.Pitch 算的持物转了最多 5.1°（0.4m 力臂上≈3.5cm），
    /// 那正是"两手相对位姿随俯仰变化 → 左手相对物体滑开"的根因。</summary>
    public float AppliedPitch(float viewPitch) => EffectivePitch(viewPitch) * followFactor;

    /// <summary>按「单手抓握」的掌面口径算出某只手的抓握点（right=true 右手）。
    /// 与 TrackAnchorsToHands 里 Anchor_RHand 的定位公式同源，左右手同一套距离——
    /// 灭火器的「喷头 ↔ 左手掌」偏差诊断要用同样口径才有意义。返回 false = 手部骨骼不可用。</summary>
    public bool TryGetGripPoint(bool right, out Vector3 point)
    {
        point = Vector3.zero;
        if (hands == null) return false;
        if (!hands.TryGetPalmFrame(right, out Vector3 wrist, out Vector3 along, out Vector3 normal)) return false;
        point = wrist + along * gripAlongDist + normal * gripNormalDist;
        return true;
    }

    private body playerBody;
    private Transform playerCameraT;
    private HandPoseController hands;       // 与本组件同挂在 FPHands_01 上
    private Transform rightHandAnchor;      // Pivot 下的两个持物锚点（懒查找）
    private Transform carryAnchor;
    private Vector3 armsBaseLocalPos;       // 手臂根挪入支点后的基准局部姿态（收纳微调叠加在其上）
    private Quaternion armsBaseLocalRot;

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

        // 手臂整体挪入 pivot（保留世界变换，外观零变化），并记录基准局部姿态
        transform.SetParent(Pivot, true);
        armsBaseLocalPos = transform.localPosition;
        armsBaseLocalRot = transform.localRotation;
    }

    private void LateUpdate()
    {
        if (Pivot == null || playerBody == null) return;

        // 死区外才跟随，减去死区宽度避免边界跳变（与 EffectivePitch 同一口径——持物对齐按它反算支点俯仰）
        float effective = EffectivePitch(playerBody.Pitch);

        // yaw 完全跟随 body；俯仰按比例跟随。支点原点即眼睛位置，
        // 手臂绕眼睛整体旋转 → 屏幕位置在任意俯仰角下保持一致
        Pivot.rotation = playerBody.transform.rotation * Quaternion.Euler(effective * followFactor, 0f, 0f);
        Pivot.position = playerCameraT != null ? playerCameraT.position : Pivot.position;

        // 低头微沉辅助（世界向下小幅位移）
        float sink = Mathf.Clamp(-effective, 0f, 90f) * sinkPerDegree;
        if (sink > 0f) Pivot.position += Vector3.down * sink;

        // 手臂收纳：在基准姿态上叠加固定偏移/旋转（支点局部空间，随俯仰旋转，
        // 任何视角下手臂相对画面收在下方，不会因为低头抬头又伸回屏幕中央）
        transform.localPosition = armsBaseLocalPos + armTuckOffset;
        transform.localRotation = armsBaseLocalRot * Quaternion.Euler(armTuckRotation);

        TrackAnchorsToHands();
    }

    /// <summary>Use a camera-relative hand pose while aiming the extinguisher.
    /// Reset from the authored baseline each frame; generic hand/prop follow stays unchanged.</summary>
    public void PrepareExtinguisherPose(Camera camera)
    {
        if (Pivot == null || camera == null) return;
        Pivot.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
        transform.localPosition = armsBaseLocalPos + armTuckOffset;
        transform.localRotation = armsBaseLocalRot * Quaternion.Euler(-55f, 0f, 0f);
        TrackAnchorsToHands();
    }

    /// <summary>Late-frame camera clearance for the extinguisher. Translate the whole hand rig
    /// and refresh anchors together; LateUpdate restores the authored baseline next frame.</summary>
    public void ShiftHeldPose(Vector3 worldOffset)
    {
        transform.position += worldOffset;
        TrackAnchorsToHands();
    }

    // 持物锚点贴合真实手部（掌面坐标系）：
    //   单手物 = 右腕 + 掌心方向×gripAlongDist + 掌面法线×gripNormalDist，
    //            落在卷曲手指围成的握持区（掌心一侧），侧面持握水杯；
    //   双手物 = 双腕中点沿平均掌心方向前移 carryGripOffset，抱在两手之间。
    // 锚点旋转保持随支点（物品在视野里直立），位置跟手；手势摆动腕骨时物品跟手一起动。
    private void TrackAnchorsToHands()
    {
        if (hands == null || Pivot == null) return;
        if (rightHandAnchor == null) { Transform t = Pivot.Find("Anchor_RHand"); if (t != null) rightHandAnchor = t; }
        if (carryAnchor == null) { Transform t = Pivot.Find("Anchor_Carry"); if (t != null) carryAnchor = t; }

        // 锚点旋转只跟随水平朝向（yaw），不随俯仰——持物在世界坐标里保持直立：
        // 低头从桌上拿起物品的瞬间，物品不会跟着视线前倾 40°（之前“杯口朝前”
        // 的根源），掌心从侧面握住物品
        Quaternion yawRot = playerBody.transform.rotation;

        if (rightHandAnchor != null
            && hands.TryGetPalmFrame(true, out Vector3 wristR, out Vector3 alongR, out Vector3 normalR))
        {
            rightHandAnchor.position = wristR + alongR * gripAlongDist + normalR * gripNormalDist;
            rightHandAnchor.rotation = yawRot;
        }

        if (carryAnchor != null
            && hands.TryGetHandFrame(false, out Vector3 wristL, out Vector3 palmL)
            && hands.TryGetHandFrame(true, out Vector3 wristR2, out Vector3 palmR2))
        {
            Vector3 mid = (wristL + wristR2) * 0.5f;
            Vector3 palmAvg = palmL + palmR2;
            if (palmAvg.sqrMagnitude < 1e-6f) palmAvg = Vector3.forward;
            carryAnchor.position = mid + palmAvg.normalized * carryGripOffset;
            carryAnchor.rotation = yawRot;
        }
    }
}
