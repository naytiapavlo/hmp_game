// 可抓取物品：挂在能被玩家拿起/放下的物体上（由 InteractionSetup 装配）。
//
// 两类持握模式（对齐项目计划“轻物单手、重物双手”的设定）：
//   - OneHand  单手小物（笔、水杯）：吸附到右手锚点，右手播抓握姿态；
//   - TwoHands 双手重物（键盘、灭火器）：吸附到胸前锚点，双手播托举姿态。
//
// 持握朝向（holdAlignsToView）：
//   - 开：朝向跟随**摄像机视角**——无论在视野里怎么上下看，物品都相对画面朝上。
//     长条物（灭火器）用这个：低头时不会把物品翻过来、把内部亮给相机。
//   - 关（默认）：只跟水平朝向，在世界坐标里保持直立。水杯这类物件用这个——
//     它模型原点在底部、抓握点又在掌心凹槽里，加俯仰后相对手的朝向会变，
//     几何容易落进手部网格/近裁面里而"看不见"（2026-09-21 实测踩过）。
//     由 InteractionSetup 的持握配置表逐件指定。
//
// 实现（位置与朝向都不写世界位姿，全部交给层级解析）：
//   Anchor（ArmsPitchFollow 每帧贴手掌，旋转只含 yaw）
//     └─ HoldPose（本组件创建：局部旋转 = 俯仰，于是它的世界朝向 = 视角朝向）
//          └─ 物品（localPosition = holdPositionOffset、localRotation = holdRotationOffset）
//   这样：位置零帧延迟；局部旋转只含俯仰、不含 yaw —— 与父级 yaw 的更新时机无关，
//   快速左右转身也不会抖（曾因写世界旋转而被多转/少转一个 ΔYaw 导致"鬼畜抽动"）。
//
// 物理约定（由装配脚本准备，运行时维护）：
//   - 物体带 Rigidbody（平时 kinematic）+ BoxCollider（按网格包围盒生成，
//     替换掉不能配刚体的非凸 MeshCollider）；
//   - 拿起：碰撞体关闭（不挡准星射线、不推挤玩家胶囊），挂到锚点；
//   - 放下（Q）：脱离锚点、恢复碰撞与物理，轻放到身前。
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PickupItem : MonoBehaviour, IInteractable
{
    public enum CarryMode { OneHand, TwoHands }

    [Header("持握配置")]
    [Tooltip("持握模式：单手小物 / 双手重物")]
    [SerializeField] private CarryMode carryMode = CarryMode.OneHand;
    [Tooltip("持握位置偏移（在持物节点里：holdAlignsToView 开 = 视角对齐帧，关 = 锚点帧）。Play 模式实时微调")]
    [SerializeField] private Vector3 holdPositionOffset = Vector3.zero;
    [Tooltip("持握旋转偏移（欧拉角，同上，在持物节点里）。Play 模式实时微调")]
    [SerializeField] private Vector3 holdRotationOffset = Vector3.zero;
    [Tooltip("持握朝向是否跟随摄像机视角：开 = 相对画面始终朝上（长条物，如灭火器）；" +
             "关（默认）= 只随水平朝向、世界坐标保持直立（水杯等不便倾倒、且抓握点在掌心的物件）")]
    [SerializeField] private bool holdAlignsToView = false;
    [Tooltip("放下时向前轻放的速度（米/秒）")]
    [SerializeField] private float dropForwardVelocity = 0.8f;

    public CarryMode Carry => carryMode;

    private Rigidbody rb;
    private Collider col;
    private Interactor carryActor;  // 持握者（取俯仰用）
    private Transform holdPose;     // 锚点下的「持物节点」：只承载朝向，局部旋转 = 俯仰（或不转）
    private readonly List<Collider> ignoredPlayerColliders = new List<Collider>();  // 松手瞬间忽略的玩家碰撞体

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<Collider>();
        if (rb == null) Debug.LogWarning("[PickupItem] " + name + " 缺少 Rigidbody，放下后不会受重力。", this);
        if (col == null) Debug.LogWarning("[PickupItem] " + name + " 缺少 Collider，无法被准星命中。", this);
    }

    // 只写「持物节点的局部旋转」，不写物品的世界/局部位置：
    //   位置由层级（锚点 → 持物节点 → 物品）在渲染时解析 —— 天然无帧延迟；
    //   局部旋转只含俯仰、不含 yaw —— 与父级 yaw 的更新时机无关，快速转身不会抖。
    private void LateUpdate()
    {
        if (holdPose == null) return;
        holdPose.localRotation = holdAlignsToView && carryActor != null
            ? carryActor.ViewPitchRotation
            : Quaternion.identity;
    }

    public string GetInteractPrompt(Interactor actor)
    {
        if (actor == null) return "E 拿起";
        if (actor.CarriedItem == this) return null; // 已在手上的物体不再提示（碰撞也已关闭，理论上打不中）
        if (actor.CarriedItem != null) return "手已占用（按 Q 放下）";
        return carryMode == CarryMode.TwoHands ? "E 双手抱起" : "E 拿起";
    }

    public void Interact(Interactor actor)
    {
        if (actor == null || actor.CarriedItem != null) return;
        if (!Take(actor)) return;          // 吸附失败（拿不到锚点）时不要标记为"已在手上"
        actor.NotifyPickedUp(this);
    }

    // 吸附到对应锚点并关闭物理。返回 false = 拿不到锚点，已放弃吸附（物品保持原位）
    private bool Take(Interactor actor)
    {
        Transform anchor = carryMode == CarryMode.OneHand ? actor.RightHandAnchor : actor.CarryAnchor;
        if (anchor == null)
        {
            // 绝不吸附到空父级：那会把物品挂到场景根、位置变成世界原点，表现为"物品消失不见"
            Debug.LogError("[PickupItem] " + name + " 拿起失败：持物锚点 "
                           + (carryMode == CarryMode.OneHand ? "Anchor_RHand" : "Anchor_Carry")
                           + " 不存在（Interactor 未创建）。请重跑 Tools/Interactable/Re-run Interaction Setup。", this);
            return false;
        }
        carryActor = actor;

        // 持物节点：锚点的子物体、局部零位姿。持物节点的世界旋转 = 锚点(yaw) × 局部(俯仰) = 视角朝向，
        // 所以挂在它下面的物品，其 localPosition / localRotation 就是在「视角对齐帧」里的偏移，
        // 与朝向同源（这也是修掉"浮空"的那条要求），且不需要每帧写世界位姿。
        if (holdPose == null)
        {
            holdPose = new GameObject("HoldPose").transform;
            holdPose.SetParent(anchor, false);
            holdPose.localPosition = Vector3.zero;
            holdPose.localRotation = Quaternion.identity;
            holdPose.localScale = Vector3.one;
        }
        else if (holdPose.parent != anchor)
        {
            holdPose.SetParent(anchor, false);
            holdPose.localPosition = Vector3.zero;
            holdPose.localRotation = Quaternion.identity;
        }

        // worldPositionStays = true：**保留物品原有世界缩放**（模型可能自带非 1 缩放，
        // 早期版本直接写 localScale = 1 会改变尺寸），只覆盖位置与朝向偏移
        transform.SetParent(holdPose, true);
        transform.localPosition = holdPositionOffset;
        transform.localRotation = Quaternion.Euler(holdRotationOffset);
        LateUpdate();                          // 当帧先把俯仰摆好，避免拿起瞬间闪一帧

        if (col != null) col.enabled = false;
        if (rb != null)
        {
            rb.isKinematic = true;
            // 关键：插值会用物理快照覆盖渲染位置——持物是靠锚点每帧搬移 Transform 的，
            // 不关插值的话物品会“冻结”在拿起时的位置，不跟着人走
            rb.interpolation = RigidbodyInterpolation.None;
        }

        // 拿取诊断：持物"看不见/穿模"时，看这一行就能判断是离相机太近、还是位置/尺寸/朝向不对
        Transform cam = actor.ViewTransform;
        Debug.Log("[PickupItem] " + name + " 拿起：模式=" + carryMode + " 视角对齐=" + holdAlignsToView
                  + " 世界位置=" + transform.position.ToString("F2")
                  + " 世界尺寸=" + WorldSize().ToString("F3")
                  + " 世界欧拉=" + transform.rotation.eulerAngles.ToString("F0")
                  + " 锚点欧拉=" + anchor.rotation.eulerAngles.ToString("F0")
                  + " 距相机=" + (cam != null
                      ? Vector3.Distance(transform.position, cam.position).ToString("F2") + "m"
                      : "?（没找到相机）"), this);
        return true;
    }

    /// <summary>当前世界尺寸（所有渲染体的包围盒；诊断用）</summary>
    private Vector3 WorldSize()
    {
        Renderer[] rends = GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return Vector3.zero;
        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
        return b.size;
    }

    // 放下（由 Interactor 调用）：脱离锚点、恢复碰撞与重力。
    // toss=false「轻放」（E 键，原地松手自然落下）；toss=true「丢弃」（Q 键，向前轻抛）
    public void Drop(Transform dropper, bool toss)
    {
        transform.SetParent(null, true);
        carryActor = null;
        if (holdPose != null)   // 持物节点是运行时创建的辅助物体，放下即销毁，避免残留空物体
        {
            Destroy(holdPose.gameObject);
            holdPose = null;
        }

        // 1) 先把物品推出玩家身体。持物点就在胸前/掌心，原地恢复碰撞会与玩家胶囊体重叠，
        //    PhysX 解穿插时把物品和玩家一起弹开——这就是"丢出后双方被异常弹开"的根因。
        //    （灭火器 0.5m 高、碰撞盒跨"手±0.25m"，低头持握时几乎必然重叠，所以表现为"有概率"。）
        if (dropper != null) transform.position = PushClearOf(dropper, transform.position);

        if (col != null) col.enabled = true;
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate; // 恢复插值，落地/滚动平滑
            rb.linearVelocity = toss && dropper != null
                ? dropper.forward * dropForwardVelocity + Vector3.up * 0.3f
                : Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // 2) 兜底：贴墙等情况下推不出足够距离时，仍可能有残余重叠 → 短暂忽略与玩家的碰撞对，
        //    等物品离开身体后自动恢复（之后玩家仍能正常踢到地上的物品）
        if (dropper != null && col != null) BeginPlayerCollisionGrace(dropper);
    }

    /// <summary>把释放点沿身前水平方向推到玩家胶囊之外，确保恢复碰撞的瞬间不与玩家重叠</summary>
    private Vector3 PushClearOf(Transform dropper, Vector3 current)
    {
        CharacterController cc = dropper.GetComponent<CharacterController>();
        if (cc == null) cc = dropper.GetComponentInChildren<CharacterController>(true);
        Vector3 lossy = dropper.lossyScale;
        float playerRadius = cc != null
            ? cc.radius * Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.z))
            : 0.3f;

        // 物品半径取渲染包围盒（此时碰撞体刚被重新启用/或仍禁用，用包围盒更可靠）
        Vector3 size = WorldSize();
        float itemRadius = Mathf.Max(size.x, size.z) * 0.5f;

        Vector3 forward = dropper.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude < 1e-4f ? dropper.forward : forward.normalized;
        return current + forward * (playerRadius + itemRadius + 0.05f);
    }

    /// <summary>短暂忽略"本物品 × 玩家所有碰撞体"的碰撞对，避免松手瞬间的解穿插把双方弹开</summary>
    private void BeginPlayerCollisionGrace(Transform dropper)
    {
        foreach (Collider pc in dropper.GetComponentsInChildren<Collider>(true))
        {
            if (pc == null || pc == col) continue;
            Physics.IgnoreCollision(col, pc, true);
            ignoredPlayerColliders.Add(pc);
        }
        StartCoroutine(EndGraceAfter(0.4f));
    }

    private IEnumerator EndGraceAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        foreach (Collider pc in ignoredPlayerColliders)
            if (pc != null && col != null) Physics.IgnoreCollision(col, pc, false);
        ignoredPlayerColliders.Clear();
    }
}
