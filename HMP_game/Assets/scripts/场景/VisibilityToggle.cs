// 通用层级显隐统一开关：挂在任意物体上，统一控制它整个子层级的可见性。
// 当前挂在「人物」根物体上：取消勾选即可同时隐藏身体胶囊和第一人称双手。
//
// 与 CeilingVisibility（天花板开关）同一套交互逻辑：
//   - 总开关只在状态「变化」的那一刻统一下发，平时不逐帧覆盖，
//     因此统一隐藏后仍可单独勾选某个物体的 Renderer 单独显示；
//   - 编辑模式与 Play 模式都生效（ExecuteAlways）；
//   - 子层级有增删时，用右键菜单「重新收集目标」刷新缓存。
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways] // 让开关在编辑器里（不进 Play 模式）也能即时生效
public class VisibilityToggle : MonoBehaviour
{
    [Tooltip("总开关：勾选=子层级全部可见；取消=子层级全部隐藏")]
    public bool visible = true;

    [Tooltip("只控制名字以该前缀开头的物体；留空=控制子层级全部渲染器（人物这里留空即可）")]
    [SerializeField] private string namePrefix = "";

    // 用 Renderer 基类：同时涵盖 MeshRenderer（身体胶囊）和
    // SkinnedMeshRenderer（第一人称双手）等所有可渲染组件
    private readonly List<Renderer> targets = new List<Renderer>();
    private bool lastApplied = true; // 上一次下发过的开关状态，用于边沿检测

    private void OnEnable()
    {
        Collect();
    }

    // 边沿检测：只在总开关变化的那一帧统一下发，其余时间不碰各渲染器
    private void Update()
    {
        if (visible == lastApplied) return;
        Apply();
    }

    // 把当前开关状态下发到所有缓存的目标渲染器
    [ContextMenu("立即应用开关")]
    public void Apply()
    {
        for (int i = 0; i < targets.Count; i++)
        {
            // 物体可能已被删除，判空跳过（Unity 的“假 null”用隐式转换判断）
            if (targets[i] != null) targets[i].enabled = visible;
        }
        lastApplied = visible;
    }

    // 对外接口：供以后的 UI 按钮、教学流程脚本等调用
    public void SetVisible(bool value)
    {
        visible = value;
        Apply();
    }

    // 重新扫描子层级，按前缀过滤收集所有渲染器
    [ContextMenu("重新收集目标")]
    public void Collect()
    {
        targets.Clear();
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            // 配了前缀就只收前缀匹配的物体；留空收全部
            if (!string.IsNullOrEmpty(namePrefix) &&
                !r.name.StartsWith(namePrefix, System.StringComparison.Ordinal)) continue;
            targets.Add(r);
        }
        Apply(); // 收集完立即按当前开关同步一次，保证状态一致
    }
}
