// 场景天花板可见性统一开关（挂在「场景」根物体上，由 CeilingVisibilitySetup 自动完成）。
//
// 用法（编辑模式与 Play 模式都有效，ExecuteAlways）：
//   - 选中场景里的「场景」物体，在 Inspector 勾/取消「Ceilings Visible」，
//     即可统一显示/隐藏全部天花板（SM_Ceiling_* 命名，约 134 块）；
//   - 想单独控制某一块天花板，直接勾选那个物体自己的 MeshRenderer 即可——
//     本脚本只在总开关「变化」的那一刻统一下发，平时不会每帧覆盖单独设置；
//   - 增删或重命名天花板后，在组件右键菜单里点「重新收集天花板」刷新缓存。
//
// 典型用途：培训时隐藏天花板，方便以俯视角向旁观学员展示房间内部与起火点。
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways] // 让开关在编辑器里（不进 Play 模式）也能即时生效
public class CeilingVisibility : MonoBehaviour
{
    [Tooltip("天花板总开关：勾选=全部可见；取消=全部隐藏（不影响墙面、地面与吊顶灯具）")]
    public bool ceilingsVisible = true;

    // 天花板网格命名前缀（建模规范：SM_Ceiling_Module_xx）
    private const string CeilingPrefix = "SM_Ceiling";

    private readonly List<MeshRenderer> ceilings = new List<MeshRenderer>();
    private bool lastApplied = true; // 上一次下发过的开关状态，用于边沿检测

    private void OnEnable()
    {
        CollectCeilings();
    }

    // 边沿检测：只在总开关状态发生「变化」的那一帧统一下发，
    // 其余时间不碰各渲染器，保证单独开关不被覆盖。
    private void Update()
    {
        if (ceilingsVisible == lastApplied) return;
        Apply();
    }

    // 把当前总开关状态下发到所有缓存的天花板渲染器
    [ContextMenu("立即应用开关")]
    public void Apply()
    {
        for (int i = 0; i < ceilings.Count; i++)
        {
            // 物体可能已被删除，判空跳过（Unity 的“假 null”用隐式转换判断）
            if (ceilings[i] != null) ceilings[i].enabled = ceilingsVisible;
        }
        lastApplied = ceilingsVisible;
    }

    // 对外接口：供以后的 UI 按钮、教学流程脚本等调用
    // 例：GetComponent<CeilingVisibility>().SetCeilingsVisible(false);
    public void SetCeilingsVisible(bool visible)
    {
        ceilingsVisible = visible;
        Apply();
    }

    // 重新扫描子层级，收集所有名字以 SM_Ceiling 开头的物体的渲染器
    [ContextMenu("重新收集天花板")]
    public void CollectCeilings()
    {
        ceilings.Clear();
        foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r.name.StartsWith(CeilingPrefix, System.StringComparison.Ordinal))
                ceilings.Add(r);
        }
        Apply(); // 收集完立即按当前开关同步一次，保证新加进来的块状态一致
    }
}
