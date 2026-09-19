// 可抓取物品：挂在能被玩家拿起/放下的物体上（由 InteractionSetup 装配）。
//
// 两类持握模式（对齐项目计划“轻物单手、重物双手”的设定）：
//   - OneHand  单手小物（笔、水杯）：吸附到右手锚点，右手播抓握姿态；
//   - TwoHands 双手重物（键盘、灭火器）：吸附到胸前锚点，双手播托举姿态。
//
// 物理约定（由装配脚本准备，运行时维护）：
//   - 物体带 Rigidbody（平时 kinematic）+ BoxCollider（按网格包围盒生成，
//     替换掉不能配刚体的非凸 MeshCollider）；
//   - 拿起：碰撞体关闭（不挡准星射线、不推挤玩家胶囊），挂到锚点；
//   - 放下（Q）：脱离锚点、恢复碰撞与物理，轻放到身前。
using UnityEngine;

public class PickupItem : MonoBehaviour, IInteractable
{
    public enum CarryMode { OneHand, TwoHands }

    [Header("持握配置")]
    [Tooltip("持握模式：单手小物 / 双手重物")]
    [SerializeField] private CarryMode carryMode = CarryMode.OneHand;
    [Tooltip("持握时相对锚点的位置偏移（Play 模式下实时微调用）")]
    [SerializeField] private Vector3 holdPositionOffset = Vector3.zero;
    [Tooltip("持握时相对锚点的旋转偏移（欧拉角，Play 模式下实时微调用）")]
    [SerializeField] private Vector3 holdRotationOffset = Vector3.zero;
    [Tooltip("放下时向前轻放的速度（米/秒）")]
    [SerializeField] private float dropForwardVelocity = 0.8f;

    public CarryMode Carry => carryMode;

    private Rigidbody rb;
    private Collider col;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<Collider>();
        if (rb == null) Debug.LogWarning("[PickupItem] " + name + " 缺少 Rigidbody，放下后不会受重力。", this);
        if (col == null) Debug.LogWarning("[PickupItem] " + name + " 缺少 Collider，无法被准星命中。", this);
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
        Take(actor);
        actor.NotifyPickedUp(this);
    }

    // 吸附到对应锚点并关闭物理
    private void Take(Interactor actor)
    {
        Transform anchor = carryMode == CarryMode.OneHand ? actor.RightHandAnchor : actor.CarryAnchor;
        transform.SetParent(anchor, true); // 保留世界缩放，锚点链路缩放均匀，物品不会变形
        transform.localPosition = holdPositionOffset;
        transform.localRotation = Quaternion.Euler(holdRotationOffset);
        if (col != null) col.enabled = false;
        if (rb != null)
        {
            rb.isKinematic = true;
            // 关键：插值会用物理快照覆盖渲染位置——持物是靠锚点每帧搬移 Transform 的，
            // 不关插值的话物品会“冻结”在拿起时的位置，不跟着人走
            rb.interpolation = RigidbodyInterpolation.None;
        }
    }

    // 放下（由 Interactor 调用）：脱离锚点、恢复碰撞与重力。
    // toss=false「轻放」（E 键，原地松手自然落下）；toss=true「丢弃」（Q 键，向前轻抛）
    public void Drop(Transform dropper, bool toss)
    {
        transform.SetParent(null, true);
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
    }
}
