// 座椅控制器：挂在椅子网格物体上（由 InteractionSetup 自动挂载），E 键坐下 / 再按 E 起身。
//
// 坐下流程：
//   1. 冻结玩家移动（body.SetMovementLocked）并短暂锁视角，关闭 CharacterController
//      （否则插值移动时胶囊会跟场景碰撞打架）；
//   2. 把 body 位置/朝向插值到「坐姿锚点」：座面高度由椅子网格包围盒估算，
//      眼高 = 座面 + eyeHeightAboveSeat（约 0.72m，正常坐姿眼高）；俯仰缓动归零；
//   3. 坐定后恢复视角转动（可以环顾），但移动保持锁定 → 坐姿期间不能穿墙移动。
// 起身：插值回到椅子前方 standOffsetDistance 处（沿用坐下前的身体高度），
//   恢复 CharacterController 与移动。
//
// 坐着时玩家手中不能有东西（先 Q 放下）；E 优先起身（Interactor 已做坐姿优先分支）。
// TODO（后续）：椅子朝向若与预期相反（背对桌子坐），把 faceYawOffset 改 180。
using UnityEngine;

public class SeatController : MonoBehaviour, IInteractable
{
    [Header("坐姿参数（可按椅子类型微调）")]
    [Tooltip("座面高度 ≈ 网格包围盒 min.y + size.y × 该比例（工位椅总高约 0.9m、座面约 0.45m → 0.5）")]
    [SerializeField] private float seatHeightRatio = 0.5f;
    [Tooltip("坐姿眼高相对座面的高度（米）")]
    [SerializeField] private float eyeHeightAboveSeat = 0.72f;
    [Tooltip("入座/起身过渡时长（秒）")]
    [SerializeField] private float transitionDuration = 0.45f;
    [Tooltip("起身站位离椅子中心的距离（米，沿坐姿朝向）")]
    [SerializeField] private float standOffsetDistance = 0.45f;
    [Tooltip("坐下面朝方向修正（度），个别椅子算错了就手动加减")]
    [SerializeField] private float faceYawOffset = 0f;

    [Header("坐姿朝向（装配脚本自动写入）")]
    [Tooltip("坐姿面朝的世界 Yaw 角（度）。椅子网格节点的旋转不可靠（多为恒等旋转），" +
             "装配时按「最近的桌面/桌子/接待台」方向算出并写在这里")]
    [SerializeField] private float facingYawDeg = 0f;
    [Tooltip("facingYawDeg 是否有效；false 时退回用椅子自身 transform 朝向")]
    [SerializeField] private bool hasComputedFacing = false;

    private enum SeatState { Idle, SittingDown, Seated, StandingUp }

    private SeatState state = SeatState.Idle;
    private float t;                    // 过渡进度 0~1
    private Vector3 fromPos, toPos;     // 过渡起止（body 世界坐标）
    private float fromYaw, toYaw;       // 过渡起止朝向
    private float fromPitch;            // 入座时的俯仰角
    private Interactor actor;
    private body playerBody;
    private CharacterController controller;
    private float seatSurfaceY;         // 座面世界高度（起身要用）

    private bool Animating => state == SeatState.SittingDown || state == SeatState.StandingUp;

    public string GetInteractPrompt(Interactor who)
    {
        if (Animating) return null;
        if (who != null && who.CurrentSeat == this) return "E 起身";
        if (who != null && who.CarriedItem != null) return "先按 Q 放下手中物品";
        return "E 坐下";
    }

    public void Interact(Interactor who)
    {
        if (Animating || who == null) return;
        if (who.CurrentSeat == this) BeginStand();
        else if (who.CarriedItem == null) BeginSit(who);
    }

    private void BeginSit(Interactor who)
    {
        actor = who;
        playerBody = who.GetComponent<body>();
        controller = who.GetComponent<CharacterController>();

        Renderer rend = GetComponent<Renderer>();
        Bounds bounds = rend != null ? rend.bounds : new Bounds(transform.position, Vector3.one);
        seatSurfaceY = bounds.min.y + bounds.size.y * seatHeightRatio;

        // 坐姿目标：椅子中心上方，眼高对齐（body 原点在眼睛下方约 camera.localPosition.y 处）
        Camera cam = playerBody != null ? playerBody.PlayerCamera : null;
        float eyeOffset = cam != null ? cam.transform.localPosition.y : 0.651f;
        toPos = new Vector3(bounds.center.x, seatSurfaceY + eyeHeightAboveSeat - eyeOffset, bounds.center.z);
        // 坐姿朝向：优先用装配脚本算出的 facingYawDeg（椅子网格自身旋转不可靠）
        float seatYaw = hasComputedFacing
            ? facingYawDeg
            : Quaternion.LookRotation(transform.forward, Vector3.up).eulerAngles.y;
        toYaw = seatYaw + faceYawOffset;

        fromPos = who.transform.position;
        fromYaw = who.transform.eulerAngles.y;
        fromPitch = playerBody != null ? playerBody.Pitch : 0f;

        // 坐下手势：双手向下撑（坐姿不允许持物，手势后自动回到放松姿态）
        who.PlayHandGesture(HandPoseController.Pose.Press, 0.5f);

        // 冻结玩家控制并关胶囊，过渡期间由本组件全权移动 body
        if (controller != null) controller.enabled = false;
        if (playerBody != null)
        {
            playerBody.SetMovementLocked(true);
            playerBody.SetLookLocked(true);
        }
        who.CurrentSeat = this;

        t = 0f;
        state = SeatState.SittingDown;
    }

    private void BeginStand()
    {
        // 起身目标：沿坐姿朝向前方 standOffsetDistance 处，高度沿用坐下前的身体高度
        float standYaw = hasComputedFacing
            ? facingYawDeg
            : Quaternion.LookRotation(transform.forward, Vector3.up).eulerAngles.y;
        Vector3 forward = Quaternion.Euler(0f, standYaw + faceYawOffset, 0f) * Vector3.forward;
        Vector3 standXZ = transform.position + forward * standOffsetDistance;
        toPos = new Vector3(standXZ.x, fromPos.y, standXZ.z); // fromPos = 入座前的站姿身体高度

        fromPos = actor.transform.position;
        fromYaw = actor.transform.eulerAngles.y;
        toYaw = fromYaw;
        fromPitch = playerBody != null ? playerBody.Pitch : 0f;

        // 起身手势：双手向下撑借力
        actor.PlayHandGesture(HandPoseController.Pose.Press, 0.5f);

        if (playerBody != null) playerBody.SetLookLocked(true);

        t = 0f;
        state = SeatState.StandingUp;
    }

    private void Update()
    {
        switch (state)
        {
            case SeatState.SittingDown:
                t += Time.deltaTime / transitionDuration;
                if (t >= 1f) { ApplyTransition(1f); state = SeatState.Seated; OnSeated(); }
                else ApplyTransition(t);
                break;
            case SeatState.StandingUp:
                t += Time.deltaTime / transitionDuration;
                if (t >= 1f) { ApplyTransition(1f); state = SeatState.Idle; OnStoodUp(); }
                else ApplyTransition(t);
                break;
        }
    }

    // 平滑插值 body 的位置/朝向/俯仰（SmoothStep 缓入缓出）
    private void ApplyTransition(float progress)
    {
        if (actor == null) return;
        float k = Mathf.SmoothStep(0f, 1f, progress);
        actor.transform.position = Vector3.Lerp(fromPos, toPos, k);
        actor.transform.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(fromYaw, toYaw, k), 0f);
        if (playerBody != null) playerBody.SetPitch(Mathf.Lerp(fromPitch, 0f, k));
    }

    private void OnSeated()
    {
        if (playerBody != null) playerBody.SetLookLocked(false); // 坐定后恢复环顾
    }

    private void OnStoodUp()
    {
        if (controller != null) controller.enabled = true;
        if (playerBody != null)
        {
            playerBody.SetLookLocked(false);
            playerBody.SetMovementLocked(false);
        }
        if (actor != null) actor.CurrentSeat = null;
    }

    // 编辑器里选中椅子时画出坐姿朝向箭头（青色），便于逐把核对装配结果
    private void OnDrawGizmosSelected()
    {
        float yaw = (hasComputedFacing ? facingYawDeg
                    : Quaternion.LookRotation(transform.forward, Vector3.up).eulerAngles.y) + faceYawOffset;
        Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Vector3 origin = transform.position + Vector3.up * 1.1f;
        Vector3 tip = origin + fwd * 0.9f;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(origin, tip);
        Gizmos.DrawLine(tip, tip - Quaternion.Euler(0f, 35f, 0f) * fwd * 0.28f);
        Gizmos.DrawLine(tip, tip - Quaternion.Euler(0f, -35f, 0f) * fwd * 0.28f);
    }
}
