// 第一人称玩家控制脚本（初版）。
// 挂载位置：场景「人物/body」物体上（由 Assets/Editor/PlayerBodySetup.cs 自动完成挂载与配置）。
//
// 设计约定（与场景现有层级一致）：
//   - 「人物」根物体在地面(y=0)，body 在其上约 0.91m，Main Camera 是 body 的子物体，
//     总高度约 1.56m，接近建模规范要求的 1.6m 第一人称眼高；
//   - body 只负责水平移动与水平转身（yaw），俯仰（pitch）只作用于眼睛相机，
//     这样挂在 body 下的手部等子物体会跟着水平视线摆动；
//   - 工程已启用 Input System Only（activeInputHandler=1），
//     因此必须用 UnityEngine.InputSystem 轮询输入，不能使用旧版 UnityEngine.Input。
using UnityEngine;
using UnityEngine.InputSystem;

// 挂载本脚本时 Unity 会自动补一个 CharacterController（角色胶囊控制器）
[RequireComponent(typeof(CharacterController))]
public class body : MonoBehaviour
{
    [Header("控制开关")]
    [Tooltip("玩家控制总开关：关闭后停止移动与转视角，并释放鼠标光标（默认开）")]
    public bool controlEnabled = true;

    [Header("交互系统状态（由 SeatController 等交互组件在运行时驱动，一般无需手动修改）")]
    [Tooltip("移动锁：为 true 时冻结位移（如坐姿、坐下/起身过渡期间），但保留视角转动")]
    [SerializeField] private bool movementLocked = false;
    [Tooltip("视角锁：为 true 时冻结鼠标视角（仅在坐下/起身的短过渡中使用，防止和插值打架）")]
    [SerializeField] private bool lookLocked = false;

    [Header("移动参数")]
    [Tooltip("行走速度（米/秒，真实尺度；正常步行约 1.4，快走约 2，跑动约 5）")]
    [SerializeField] private float walkSpeed = 1.4f;
    [Tooltip("跑步速度（米/秒）；按住 Ctrl（左右均可）期间用这个速度移动")]
    [SerializeField] private float runSpeed = 5f;
    [Tooltip("重力加速度（米/秒²）")]
    [SerializeField] private float gravity = -9.81f;
    [Tooltip("落地后维持的小幅下压速度，让 isGrounded 判定更稳定不抖动")]
    [SerializeField] private float groundedStickVelocity = -2f;

    [Header("视角转动")]
    [Tooltip("眼睛相机：俯仰作用在它上面。留空则自动取 body 层级下的第一个相机")]
    [SerializeField] private Camera playerCamera;
    [Tooltip("鼠标灵敏度（鼠标每移动 1 像素转过的角度）")]
    [SerializeField] private float lookSensitivity = 0.12f;
    [Tooltip("俯仰角下限（度）")]
    [SerializeField] private float minPitch = -85f;
    [Tooltip("俯仰角上限（度）")]
    [SerializeField] private float maxPitch = 85f;

    private CharacterController controller;
    private float verticalVelocity; // 垂直速度：落地时重置，空中按重力累积
    private float pitch;            // 相机当前俯仰角，单独保存避免反复读欧拉角出错
    private bool cursorLocked;      // 光标锁定状态缓存，避免每帧重复调用系统 API

    private void Awake()
    {
        // RequireComponent 正常会在挂载时自动补 CharacterController；
        // 但旧版存根时期手动挂上的组件可能没有，这里兜底防止空引用刷屏
        controller = GetComponent<CharacterController>();
        if (controller == null)
        {
            Debug.LogError("[body] body 物体上缺少 CharacterController，请删掉本组件后重新挂载 body 脚本。", this);
            enabled = false;
            return;
        }

        // 眼睛相机：Inspector 未指定时自动查找 body 层级下的相机（当前场景即 Main Camera）
        if (playerCamera == null) playerCamera = GetComponentInChildren<Camera>();
        if (playerCamera == null)
        {
            Debug.LogWarning("[body] body 下没有相机，无法转视角。请在 Inspector 指定 playerCamera。", this);
            enabled = false;
            return;
        }

        // 用相机当前俯仰角初始化，保证场景里预设的非零角度也能正确衔接
        pitch = playerCamera.transform.localEulerAngles.x;
        if (pitch > 180f) pitch -= 360f; // 欧拉角 0~360 转换为 -180~180
    }

    private void Update()
    {
        UpdateCursorState();

        // 总开关关闭：不移动、不转视角（Play 模式下可在 Inspector 实时勾选切换）
        if (!controlEnabled) return;

        // 交互状态：坐姿等场景只锁移动不锁视角；过渡瞬间连视角也短暂锁定
        if (!lookLocked) HandleLook();
        if (!movementLocked) HandleMovement();
    }

    // ==== 交互系统接口（供 Interactor / SeatController 调用，基础控制逻辑不变） ====

    // 移动锁当前状态（坐姿期间为 true）
    public bool MovementLocked => movementLocked;

    // 眼睛相机只读引用（交互射线、HUD 都从它发出）
    public Camera PlayerCamera => playerCamera;

    // 当前俯仰角（度）：坐下过渡时 SeatController 读取并缓动到 0
    public float Pitch => pitch;

    public void SetMovementLocked(bool locked) => movementLocked = locked;

    public void SetLookLocked(bool locked) => lookLocked = locked;

    // 直接设置俯仰角（坐下/起身过渡用）：同时更新缓存值和相机姿态，避免与 HandleLook 冲突
    public void SetPitch(float pitchDeg)
    {
        pitch = Mathf.Clamp(pitchDeg, minPitch, maxPitch);
        if (playerCamera != null) playerCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    // 根据总开关锁定/释放鼠标：开启时锁定并隐藏光标（第一人称标准行为），
    // 关闭时释放光标，方便调试或后续 UI 阶段接管输入。
    private void UpdateCursorState()
    {
        bool shouldLock = controlEnabled;
        if (shouldLock == cursorLocked) return;
        cursorLocked = shouldLock;
        Cursor.lockState = shouldLock ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !shouldLock;
    }

    // 光标锁的持有者负责释放：body 锁上的光标，必须在 body 消失时交还。
    // 必须做的原因（2026-09-22 实测）：关卡场景卸载时 body 被销毁，而 Cursor 是**全局状态**，
    // Locked + 不可见 会留给下一个场景。主菜单（初始界面）是纯 UI 场景，没人会解锁
    // ——表现为「第一关三题答完 → 点 MAIN MENU 回到主界面，鼠标不见了」。
    // 这条路径上的上游是谁锁的并不重要：结算面板 TrainingSettlementView.Dismiss() 会把光标还原成
    // 「弹出结算前」的状态（游戏里就是 Locked），答题/讲解视图也各自存档还原，
    // 只要关卡场景一卸载，光标最终都停在这个全局状态上；所以修在持有者这一层才覆盖全部出口。
    //
    // 只做 OnDestroy、不做 OnDisable：结算/讲解/答题会把 body 临时 enabled=false，
    // 那条路径有各自的 Cursor 存档与还原，body 不参与，避免两套逻辑互相打架。
    // 也只释放自己锁上的（cursorLocked）：没锁过就别去动别人的光标状态。
    private void OnDestroy()
    {
        if (!cursorLocked) return;
        cursorLocked = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    // 鼠标视角：水平转身转 body 自身（yaw），垂直俯仰只转相机（pitch）
    private void HandleLook()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        // Input System 的 delta 已经按帧累积，直接用即可，不要再乘 Time.deltaTime
        Vector2 delta = mouse.delta.ReadValue();

        // 水平：鼠标右移 delta.x > 0，绕自身 Y 轴正转即向右看
        transform.Rotate(0f, delta.x * lookSensitivity, 0f);

        // 垂直：鼠标下移 delta.y > 0，俯仰角减小即向下看，并夹在上下限内防止翻转
        pitch = Mathf.Clamp(pitch - delta.y * lookSensitivity, minPitch, maxPitch);
        playerCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    // WASD 行走/跑步 + 重力：
    // 方向相对 body 朝向取值；body 永不俯仰，forward/right 天然水平，无需再压平 y。
    // 按住 Ctrl（左右均可）临时切换为跑步速度，松开回到走路速度。
    private void HandleMovement()
    {
        Vector2 input = ReadMoveInput();
        Vector3 direction = transform.forward * input.y + transform.right * input.x;
        float speed = IsRunning() ? runSpeed : walkSpeed;
        Vector3 horizontal = Vector3.ClampMagnitude(direction, 1f) * speed; // 斜向移动不超速

        // 落地时保持一个小的下压速度让脚贴地；离地（台阶、坡道边缘）后按重力下落
        if (controller.isGrounded) verticalVelocity = groundedStickVelocity;
        else verticalVelocity += gravity * Time.deltaTime;

        // CharacterController.Move 自带碰撞滑动，撞墙、上下坡由场景碰撞体（MeshCollider）决定
        controller.Move((horizontal + Vector3.up * verticalVelocity) * Time.deltaTime);
    }

    // 是否处于跑步状态：按住任意一个 Ctrl 键即算
    private static bool IsRunning()
    {
        Keyboard kb = Keyboard.current;
        return kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
    }

    // 读取 WASD 输入，返回 [-1,1] 的移动向量（x=右为正，y=前为正）。
    // 直接轮询键盘设备，无需配置 InputActions 资产，初版最简。
    private static Vector2 ReadMoveInput()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return Vector2.zero; // 无键盘设备（理论上 PC 不会发生）时静默忽略

        Vector2 move = Vector2.zero;
        if (kb.wKey.isPressed) move.y += 1f;
        if (kb.sKey.isPressed) move.y -= 1f;
        if (kb.dKey.isPressed) move.x += 1f;
        if (kb.aKey.isPressed) move.x -= 1f;
        return move;
    }
}
