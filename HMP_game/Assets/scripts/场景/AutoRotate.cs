// 持续慢速自转：挂上后物体绕指定轴一直匀速旋转，适合菜单背景展示。
// 当前挂在「初始界面」场景的「旋转轴」物体上（由 AutoRotateSetup 自动完成）。
//
// 说明：
//   - 旋转在 Play 模式下持续（编辑模式不转，避免把场景弄成"未保存"状态）；
//   - rotating 开关可随时启停（Inspector 勾选或代码调用 StartRotate/StopRotate）；
//   - 速度单位是「度/秒」：5°/s ≈ 每 72 秒转一圈，想更慢就调小。
using UnityEngine;

public class AutoRotate : MonoBehaviour
{
    [Header("旋转开关")]
    [Tooltip("旋转总开关：勾选=持续旋转，取消=停住不动（默认开）")]
    public bool rotating = true;

    [Header("旋转参数")]
    [Tooltip("旋转轴：默认 Y 轴。配合「世界空间旋转」即绕水平面转圈")]
    [SerializeField] private Vector3 axis = Vector3.up;

    [Tooltip("旋转速度（度/秒）；5°/s 约每 72 秒一圈")]
    [SerializeField] private float speed = 5f;

    [Tooltip("绕世界空间的轴旋转（默认开）：相机云台带俯仰倾角时，轨道仍是水平圆；\n关闭则绕物体自身倾斜的轴转（斜锥面）")]
    [SerializeField] private bool rotateInWorldSpace = true;

    private void Update()
    {
        if (!rotating) return;
        // 用 deltaTime 保证不同帧率下转速一致；
        // 世界空间下绕 Y 轴 = 水平转圈（转盘效果），相机俯仰角保持不变
        transform.Rotate(axis, speed * Time.deltaTime, rotateInWorldSpace ? Space.World : Space.Self);
    }

    // 对外接口：开始/停止旋转（供 UI 按钮或流程脚本调用）
    public void StartRotate() => rotating = true;
    public void StopRotate() => rotating = false;
}
