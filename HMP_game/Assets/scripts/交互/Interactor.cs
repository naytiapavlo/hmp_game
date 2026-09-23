// 玩家交互器：第一人称准星射线 + 按键分发 + 持物管理。
// 挂载位置：场景「人物/body」物体上（由 Assets/Editor/InteractionSetup.cs 自动完成挂载）。
//
// 工作方式：
//   - 每帧从 body.PlayerCamera（眼睛相机）中心向前发射射线（默认 2.8m）；
//     射线打到的第一个碰撞体如果在 Interactable 层且带 IInteractable 组件，
//     准星高亮并显示提示文案，按 E 调用其 Interact；
//   - 射线本身不做 Layer 过滤（用默认全层），靠“命中的层是不是 Interactable”判定，
//     这样墙体、玻璃会自然挡住交互，不会隔墙触发；
//   - 键位与 body.cs 一致直接轮询 Input System 设备：E 交互、Q 放下手中物；
//   - 坐姿期间 E 一律交给当前座椅（起身），不响应射线目标。
//
// 持物锚点说明：body 的缩放是 (0.5, 1, 0.5)（非均匀），锚点作为 body 子物体时
// 用 localScale (2,1,2) 抵消，保证吸附上去的物品世界缩放仍为 1、不变形。
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

public class Interactor : MonoBehaviour
{
    /// <summary>Optional entity interaction boundary. Office and future levels share this base contract.</summary>
    public HMProtection.EntityAdapters.EntityInteractionSource entityBindings;
    private HMProtection.Entities.EntityHandle focusedEntity;
    private bool hasEntityFocus;
    [Header("射线检测")]
    [Tooltip("交互距离（米），准星射线长度")]
    [SerializeField] private float interactRange = 2.8f;
    [Tooltip("可交互层掩码（装配脚本自动写入；留 0 时运行时会尝试按层名 Interactable 自动补）")]
    [SerializeField] private LayerMask interactableMask = 0;

    [Header("持物锚点（仅兜底用）")]
    [Tooltip("单手物品锚点初始偏移。正常运行时锚点会被 ArmsPitchFollow 每帧吸附到右腕骨掌心处，此值只在手部骨骼不可用时兜底")]
    [SerializeField] private Vector3 rightHandAnchorOffset = new Vector3(0.14f, -0.30f, 0.26f);
    [Tooltip("双手物品锚点初始偏移。正常运行时吸附到双腕中点，此值仅兜底")]
    [SerializeField] private Vector3 carryAnchorOffset = new Vector3(0f, -0.23f, 0.26f);

    /// <summary>当前手中物品（null = 空手）</summary>
    public PickupItem CarriedItem { get; private set; }

    /// <summary>当前坐着的座椅（null = 站立）；由 SeatController 维护</summary>
    public SeatController CurrentSeat { get; set; }

    /// <summary>单手物品吸附锚点（body 子物体，只随水平视线摆动）</summary>
    public Transform RightHandAnchor { get; private set; }

    /// <summary>双手物品吸附锚点</summary>
    public Transform CarryAnchor { get; private set; }

    /// <summary>持物对齐用：视角相对「水平朝向」的旋转（纯俯仰，不含 yaw）。
    /// 持物锚点的旋转只含 yaw（由 ArmsPitchFollow 每帧保证），所以在锚点下的持物节点上写"俯仰"，
    /// 就得到「相对画面朝上」的持握姿态；而且这个值不含 yaw —— 快速转身时不会因为
    /// 「父级 yaw 何时更新」与「何时写物品姿态」的先后差异而被多转一个 ΔYaw（那会表现为抖动）。</summary>
    public Quaternion ViewPitchRotation => Quaternion.Euler(ViewPitchDegrees, 0f, 0f);

    /// <summary>视角俯仰角（度，抬头为正）。取原始度数而不是从四元数读 eulerAngles——
    /// eulerAngles 会把负俯仰表示成 350°，再乘跟随系数就完全是另一个角度。</summary>
    public float ViewPitchDegrees => playerBody != null ? playerBody.Pitch : 0f;

    /// <summary>持物俯仰 = 视角俯仰 × 系数（度）。</summary>
    public Quaternion CarryPitchRotation(float pitchFactor) =>
        Quaternion.Euler(ViewPitchDegrees * pitchFactor, 0f, 0f);

    /// <summary>手臂支点**实际**俯仰帧（与 ArmsPitchFollow 每帧施加在支点上的角度严格一致，含死区与 followFactor）。
    /// 供两点持握的物体用：只有物体与手臂同幅俯仰，两只手相对物体的位姿才是常量，
    /// 左手（或任何第二接触点）才不会随俯仰滑开。见 ArmsPitchFollow.AppliedPitch 的说明。</summary>
    public Quaternion ArmPitchRotation
    {
        get
        {
            if (arms == null) arms = FindFirstObjectByType<ArmsPitchFollow>();
            return Quaternion.Euler(arms != null ? arms.AppliedPitch(ViewPitchDegrees) : ViewPitchDegrees, 0f, 0f);
        }
    }

    /// <summary>手部姿态控制器（灭火器的持握对齐诊断要用它取左右手掌坐标系）。</summary>
    public HandPoseController Hands => hands;

    /// <summary>眼睛相机变换（拿取诊断用：打印持物与相机的距离，便于排查"看不见/穿模"）。</summary>
    public Transform ViewTransform => playerBody != null && playerBody.PlayerCamera != null
        ? playerBody.PlayerCamera.transform : null;

    private body playerBody;
    private HandPoseController hands;
    private ArmsPitchFollow arms;
    private InteractionHUD hud;
    public InteractionHUD Hud => hud;
    private IInteractable focused; // 当前准星对准的可交互物（null = 没有）
    private Coroutine gestureRoutine; // 进行中的交互手势（推门/撑压），防止连点叠加

    private void Awake()
    {
        playerBody = GetComponent<body>();
        // 先在 body 下试着找一次；ArmsPitchFollow.Awake 可能把 FPHands（连同
        // HandPoseController）挪到 人物/ArmsPivot 下，找不到没关系，Start 会重新解析
        hands = GetComponentInChildren<HandPoseController>(true);
        hud = InteractionHUD.EnsureExists();

        // 装配脚本没写掩码时兜底：按层名找 Interactable 层
        if (interactableMask.value == 0)
        {
            int layer = LayerMask.NameToLayer("Interactable");
            if (layer >= 0) interactableMask = 1 << layer;
            else Debug.LogWarning("[Interactor] 未找到 Interactable 层，交互射线将打不中任何物体。请重跑 Tools/Interactable/Re-run Interaction Setup。", this);
        }
    }

    private void Start()
    {
        // HandPoseController 挂在 FPHands_01 上，而 ArmsPitchFollow.Awake 会把它挪到
        // 「人物/ArmsPivot」下——两个 Awake 的执行顺序不确定，Awake 里可能找不到。
        // Start 时所有 Awake 必然完成，从「人物」根下重新解析（两种挂法都在其下）
        if (hands == null && transform.parent != null)
            hands = transform.parent.GetComponentInChildren<HandPoseController>(true);
        if (hands == null)
            Debug.LogError("[Interactor] 找不到 HandPoseController（应在 FPHands_01 上），手部姿态与手势不会生效。", this);

        // 锚点创建同样放 Start：确保 ArmsPitchFollow 的支点已建好
        CreateAnchors();

        // 开机自检：调一次无操作姿态（None），Console 立刻能看到 [HandPose] 接线日志，
        // 手部链路通不用等第一次抓取才知道
        if (hands != null) hands.SetPose(HandPoseController.Hand.Both, HandPoseController.Pose.None);
    }

    private void Update()
    {
        if (HMProtection.UI.QuizLoadingOverlay.IsVisible) return;
        if (playerBody != null && playerBody.sessionHost != null
            && playerBody.sessionHost.IsBlocked(HMProtection.Sessions.ControlMask.Interaction))
        {
            focused = null;
            focusedEntity = default;
            hasEntityFocus = false;
            hud.SetFocus(false);
            hud.SetPrompt(null);
            return;
        }
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        bool ePressed = kb.eKey.wasPressedThisFrame;
        bool qPressed = kb.qKey.wasPressedThisFrame;

        // 坐姿：提示固定为“起身”，E 一律起身，Q 无效
        if (CurrentSeat != null)
        {
            hud.SetPrompt(CurrentSeat.GetInteractPrompt(this));
            hud.SetFocus(true);
            if (ePressed) CurrentSeat.Interact(this);
            return;
        }

        UpdateFocus();

        // E：优先交互准星目标；没瞄准任何可交互物且手上有东西 → 轻放（对齐计划书“E 拿起/放下”）
        if (ePressed)
        {
            if (hasEntityFocus) entityBindings.TryInteract(focusedEntity, out _);
            else if (focused != null) focused.Interact(this);
            else if (CarriedItem != null) PlaceCarried();
        }
        // Q：丢弃手中物（向前轻抛）
        if (qPressed && CarriedItem != null) DropCarried();
    }

    // 准星射线：更新当前对准的 IInteractable 与提示（穿过墙/玻璃或超出距离则为 null）
    private void UpdateFocus()
    {
        focused = null;
        focusedEntity = default;
        hasEntityFocus = false;
        Camera cam = playerBody != null ? playerBody.PlayerCamera : null;
        if (cam == null) return;

        string prompt = null;
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, interactRange))
        {
            // 只认 Interactable 层的命中；打到墙/家具 = 被挡住
            if (((1 << hit.collider.gameObject.layer) & interactableMask.value) != 0)
            {
                if (entityBindings != null && entityBindings.ManagesCollider(hit.collider))
                {
                    // Managed targets never silently fall back when the scope or binding is invalid.
                    hasEntityFocus = true;
                    entityBindings.TryGetInteraction(hit.collider, out focusedEntity, out prompt);
                }
                else
                {
                    focused = hit.collider.GetComponentInParent<IInteractable>();
                    prompt = focused != null ? focused.GetInteractPrompt(this) : null;
                }
            }
        }

        // 手里有东西且没瞄准别的可交互物时，提示放下方式
        if (string.IsNullOrEmpty(prompt) && CarriedItem != null) prompt = "E 放下 · Q 丢弃";
        hud.SetFocus(!string.IsNullOrEmpty(prompt));
        hud.SetPrompt(prompt);
    }

    // ==== 交互手势（推门 / 撑压）：播完自动恢复 ====

    // 播放一次交互手势：推门用右手 Push，坐/起身用双手 Press；duration 秒后恢复
    // 到当前持物状态对应的姿态（拿着东西回到握姿，空手回到放松）
    public void PlayHandGesture(HandPoseController.Pose pose, float duration)
    {
        if (hands == null) return;
        if (gestureRoutine != null) StopCoroutine(gestureRoutine);
        gestureRoutine = StartCoroutine(HandGestureRoutine(pose, duration));
    }

    private IEnumerator HandGestureRoutine(HandPoseController.Pose pose, float duration)
    {
        // 推门 = 右手；其余手势（撑压/托举等）= 双手
        HandPoseController.Hand hand = pose == HandPoseController.Pose.Push
            ? HandPoseController.Hand.Right
            : HandPoseController.Hand.Both;
        hands.SetPose(hand, pose);
        yield return new WaitForSeconds(duration);
        gestureRoutine = null;
        RestoreHandPoseForCarry();
    }

    // 恢复手部姿态到「当前持物状态」应有的样子
    private void RestoreHandPoseForCarry()
    {
        if (hands == null) return;
        if (CarriedItem == null) hands.SetPose(HandPoseController.Hand.Both, HandPoseController.Pose.None);
        else ApplyCarryPose(hands, CarriedItem.Carry);
    }

    // 持物模式 → 手部姿态。拿起、手势结束恢复、后续新增入口都走这里，
    // 保证「模式↔姿态」的对应关系只有一处（之前两处 if/else 各写一遍，加模式时漏一处就会不一致）。
    private static void ApplyCarryPose(HandPoseController hands, PickupItem.CarryMode mode)
    {
        if (hands == null) return;
        switch (mode)
        {
            case PickupItem.CarryMode.OneHand:
                hands.SetPose(HandPoseController.Hand.Right, HandPoseController.Pose.Grip);
                break;
            case PickupItem.CarryMode.TwoHands:
                hands.SetPose(HandPoseController.Hand.Both, HandPoseController.Pose.Carry);
                break;
            default:   // HandleAndNozzle：右手握提把/压把双柄、左手前伸握软管喷头（左右完全不对称）
                hands.SetPose(HandPoseController.Hand.Both, HandPoseController.Pose.Extinguisher);
                break;
        }
    }

    // 拿起物品（由 PickupItem.Interact 调用）：占用持物槽、吸附到锚点、切手部姿态
    public void NotifyPickedUp(PickupItem item)
    {
        CarriedItem = item;
        ApplyCarryPose(hands, item.Carry);
    }

    // 轻放手中物品（E 键，没瞄准其他目标时）：原地松手自然落下
    public void PlaceCarried()
    {
        if (CarriedItem == null) return;
        PickupItem item = CarriedItem;
        CarriedItem = null;
        item.Drop(transform, false);
        if (hands != null) hands.SetPose(HandPoseController.Hand.Both, HandPoseController.Pose.None);
    }

    // 丢弃手中物品（Q 键）：向前轻抛
    public void DropCarried()
    {
        if (CarriedItem == null) return;
        PickupItem item = CarriedItem;
        CarriedItem = null;
        item.Drop(transform, true);
        if (hands != null) hands.SetPose(HandPoseController.Hand.Both, HandPoseController.Pose.None);
    }

    // 创建两个持物锚点。优先挂在 ArmsPitchFollow 的均匀缩放支点（ArmsPivot）下：
    // 锚点随俯仰旋转时持物不会被压扁（body 的非均匀缩放 (0.5,1,0.5) 会剪切旋转的子物体）；
    // 找不到支点时退回 body 并做缩放抵消（旧行为兜底）。
    private void CreateAnchors()
    {
        ArmsPitchFollow follow = FindFirstObjectByType<ArmsPitchFollow>();
        arms = follow;   // 缓存：持物俯仰要与手臂支点严格同幅时按它反算
        Transform pivot = follow != null ? follow.Pivot : null;
        Transform parent = pivot != null ? pivot : transform;
        Vector3 scale = pivot != null ? Vector3.one : new Vector3(2f, 1f, 2f);

        RightHandAnchor = new GameObject("Anchor_RHand").transform;
        RightHandAnchor.SetParent(parent, false);
        RightHandAnchor.localPosition = rightHandAnchorOffset;
        RightHandAnchor.localScale = scale;

        CarryAnchor = new GameObject("Anchor_Carry").transform;
        CarryAnchor.SetParent(parent, false);
        CarryAnchor.localPosition = carryAnchorOffset;
        CarryAnchor.localScale = scale;
    }
}
