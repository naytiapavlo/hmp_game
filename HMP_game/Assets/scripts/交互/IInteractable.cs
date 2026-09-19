// 可交互物统一接口：所有能被玩家准星对准并用 E 键触发的组件都实现它。
// 实现方：PickupItem（抓取）、DoorController（开门）、SeatController（坐椅）、InteractableRelay（中继）。
//
// 约定：
//   - GetInteractPrompt 返回 null / 空串 = 当前不可交互（准星不亮、E 无效），
//     想提示「不可用原因」（如手已占用）就照常返回文案，Interact 里自行判空即可；
//   - Interact 由 Interactor 在 E 键按下时调用，具体行为由实现方决定
//     （同一个物体再次交互通常做反向动作：开门→关门、坐下→起身）。
public interface IInteractable
{
    // 准星对准时显示的提示文案；返回 null 表示当前不显示提示、E 键无效
    string GetInteractPrompt(Interactor actor);

    // 执行交互（E 键）
    void Interact(Interactor actor);
}
