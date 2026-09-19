// 交互中继：把一个物体上的射线命中转发给另一个物体上的真正控制器。
//
// 用途：办公室 FBX 里门把手、铰链和门板是平级的独立网格，玩家准星打到把手上时
// 命中的是把手物体（没有 DoorController）。装配脚本会在这些“附属网格”上挂本中继，
// 指向门板上的 DoorController，从而整扇门的任何部分都能触发同一个交互。
using UnityEngine;

public class InteractableRelay : MonoBehaviour, IInteractable
{
    [Tooltip("真正的交互控制器组件（必须实现 IInteractable，如门板上的 DoorController）")]
    [SerializeField] private MonoBehaviour targetComponent = null;

    private IInteractable target;

    private void Awake()
    {
        target = targetComponent as IInteractable;
        if (target == null)
            Debug.LogError("[InteractableRelay] " + name + " 的 targetComponent 未设置或未实现 IInteractable。", this);
    }

    public string GetInteractPrompt(Interactor actor) => target != null ? target.GetInteractPrompt(actor) : null;

    public void Interact(Interactor actor)
    {
        if (target != null) target.Interact(actor);
    }
}
