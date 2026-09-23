// 平开门控制器：挂在门板网格物体上（由 InteractionSetup 自动挂载），E 键开/关门。
//
// 背景约束：办公室 FBX 里门板、把手、铰链是平级网格，且都是预制体实例的子物体，
// 不能重新挂父级到自建的门轴空物体下。因此这里不改层级，用 RotateAround 世界空间
// 旋转：把门板 + 把手一起绕「铰链轴线」旋转。铰链位置由装配脚本从 _Hinge_01 网格
// 包围盒中心算出，存成门板局部坐标（场景挪动也能跟着走）。
//
// 开门方向不固定：每次开门按交互者所在一侧动态决定——自由端朝远离玩家的一侧摆动
// （推门效果），从两侧接近都能正确开，不会朝玩家脸上甩。
//
// TODO（后续）：开合音效挂点。
using UnityEngine;

public class DoorController : MonoBehaviour, IInteractable
{
    [Header("门轴配置（装配脚本自动写入）")]
    [Tooltip("铰链轴位置（门板局部坐标）")]
    [SerializeField] private Vector3 hingeLocalPosition = Vector3.zero;
    [Tooltip("需要一起绕门轴旋转的部件：门板自身 + 各把手")]
    [SerializeField] private Transform[] rotatingParts = null;

    [Header("开合参数")]
    [Tooltip("开门角度（度，取正值；实际往哪边开由玩家站位动态决定）")]
    [SerializeField] private float openAngle = 100f;
    [Tooltip("开合角速度（度/秒）")]
    [SerializeField] private float angularSpeed = 110f;

    private float currentAngle; // 当前已旋转角度（0 = 关）
    private float targetAngle;  // 目标角度

    private bool Animating => Mathf.Abs(currentAngle - targetAngle) > 0.01f;
    /// <summary>Semantic state for entity capabilities; the visual rotation remains owned here.</summary>
    public bool IsAnimating => Animating;
    public bool IsOpen => Mathf.Abs(targetAngle) > 0.5f;

    public string GetInteractPrompt(Interactor actor)
    {
        if (Animating) return null; // 动作没做完时不响应
        return Mathf.Abs(targetAngle) > 0.5f ? "E 关门" : "E 开门";
    }

    public void Interact(Interactor actor)
    {
        TrySetOpen(actor, !IsOpen);
    }

    /// <summary>Accepts an open/close request once. The controller remains the sole owner of animation.</summary>
    public bool TrySetOpen(Interactor actor, bool open)
    {
        if (Animating || IsOpen == open) return false;

        if (actor != null) actor.PlayHandGesture(HandPoseController.Pose.Push, 0.55f);

        if (!open) { targetAngle = 0f; return true; }

        // 开门：自由端朝远离交互者的一侧摆动。正角度下自由端的初始运动方向 =
        // up × (铰链→自由端)，与“铰链→玩家”同向时改用负角度即可反向。
        Vector3 hingeWorld = transform.TransformPoint(hingeLocalPosition);
        Vector3 handleDir = HorizontalDir(HandleWorld(hingeWorld) - hingeWorld);
        Vector3 toPlayer = HorizontalDir(actor != null ? actor.transform.position - hingeWorld : transform.forward);
        float d = Vector3.Dot(Vector3.Cross(Vector3.up, handleDir), toPlayer);
        targetAngle = (d > 0f ? -1f : 1f) * openAngle;
        return true;
    }

    // 自由端参考点：离铰链最远的旋转部件（把手装在自由端）；没有部件时退回门板中心
    private Vector3 HandleWorld(Vector3 hingeWorld)
    {
        Vector3 far = transform.position;
        float best = -1f;
        if (rotatingParts != null)
        {
            for (int i = 0; i < rotatingParts.Length; i++)
            {
                Transform part = rotatingParts[i];
                if (part == null) continue;
                Vector3 v = part.position - hingeWorld;
                v.y = 0f;
                if (v.sqrMagnitude > best) { best = v.sqrMagnitude; far = part.position; }
            }
        }
        return far;
    }

    private static Vector3 HorizontalDir(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
    }

    private void Update()
    {
        if (!Animating) return;
        float step = Mathf.MoveTowards(currentAngle, targetAngle, angularSpeed * Time.deltaTime);
        float delta = step - currentAngle;
        currentAngle = step;

        if (rotatingParts == null || rotatingParts.Length == 0) return;
        Vector3 hingeWorld = transform.TransformPoint(hingeLocalPosition);
        for (int i = 0; i < rotatingParts.Length; i++)
        {
            if (rotatingParts[i] != null)
                rotatingParts[i].RotateAround(hingeWorld, Vector3.up, delta); // 门轴竖直，绕世界 Y
        }
    }
}
