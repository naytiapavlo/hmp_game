// 程序化第一人称手部姿态控制：挂在 FPHands_01 实例根物体上（由 InteractionSetup 自动挂载）。
// 手部资产 SK_FPHands_01 没有动画 clip、没有 Animator，全部用骨骼局部旋转程序化实现，
// 除指骨与腕骨外还控制前臂骨（绕肘摆动整只手臂），让每个手势都有真实的“手臂动作”：
//   - Pose.Grip  单手抓握：抬臂收拢 + 四指收拢 + 拇指扣 + 腕部内收（拿水杯）
//   - Pose.Carry 双手托举：双臂抬向胸前 + 十指半收 + 双腕内旋（抱键盘/灭火器）
//   - Pose.Push 推门手势：右臂前伸 + 手掌张开 + 腕部前顶（开关门）
//   - Pose.Press 扶椅手势：双臂向下向外张开、手落向扶手方向 + 四指半收（坐下/起身扶椅子）
//   - Pose.None 放松还原
// 指节角度参考 模型/第一人称双手/Scripts/validate_export.py 的抓握校验姿势。
//
// 重要实现说明：SetPose 与编辑器测试菜单走同一条「直接写入骨骼旋转」的路径
// （编辑器实测有效），不依赖 Update 插值，保证运行时与编辑器行为一致。
//
// 编辑模式调试（选中 FPHands_01 → HandPoseController 组件右键菜单）：
//   - 「测试：抓握（右手）」「测试：托举（双手）」立刻摆姿势；
//   - 「测试：放松」回到已捕获的默认姿态；
//   - 「还原：FBX 原始姿态」从 FBX 源资产读回所有骨骼的原始旋转，
//     可以把任何被测试污染的姿态彻底复位（只还原旋转，不动位置偏移），
//     并重新捕获默认——场景姿态被弄乱时优先点它。
#if UNITY_EDITOR
using UnityEditor;
#endif
using System.Collections.Generic;
using UnityEngine;

public class HandPoseController : MonoBehaviour
{
    public enum Hand { Left, Right, Both }
    public enum Pose { None, Grip, Carry, Push, Press }

    [Tooltip("右手骨骼是否用相反的卷曲符号（手指往外翘/腕部方向反了就勾选它）")]
    [SerializeField] private bool mirrorRightHand = false;

    [Header("手臂摆动（前臂骨旋转，度）——角度不合适可在 Play 模式实时调")]
    [Tooltip("抓握（单手拿物）时前臂：x=抬臂俯仰，y=向内收拢偏航（左手为正，右手自动镜像），z=侧倾")]
    [SerializeField] private Vector3 gripForearm = new Vector3(-18f, 6f, 0f);
    [Tooltip("托举（双手抱物）时前臂：双臂抬起朝胸前收拢")]
    [SerializeField] private Vector3 carryForearm = new Vector3(-28f, 8f, 0f);
    [Tooltip("推门手势前臂：手臂向前伸出推门")]
    [SerializeField] private Vector3 pushForearm = new Vector3(-26f, 0f, 0f);
    [Tooltip("扶椅手势前臂（坐下/起身）：双臂向下向外张开，手落向扶手/椅面方向。y=向外张开的偏航幅度")]
    [SerializeField] private Vector3 pressForearm = new Vector3(-40f, 20f, 0f);

    private static readonly string[] FingerNames = { "Index", "Middle", "Ring", "Little" };

    // 指节卷曲角（度，绕指骨局部 X）：[第1节, 第2节, 第3节]
    private static readonly float[] GripFinger = { -25f, -40f, -22f };
    private static readonly float[] GripThumb = { -12f, -15f, -8f };
    private static readonly float[] CarryFinger = { -12f, -30f, -18f };
    private static readonly float[] CarryThumb = { -8f, -10f, 0f };
    // 推门手势：手掌张开前推（四指自然伸直）
    private static readonly float[] PushFinger = { -8f, -10f, -6f };
    private static readonly float[] PushThumb = { -4f, -5f, 0f };
    // 撑压手势（扶椅）：坐下/起身时双手向下向外扶住扶手/椅面（四指半收）
    private static readonly float[] PressFinger = { -15f, -20f, -12f };
    private static readonly float[] PressThumb = { -8f, -8f, 0f };
    // 腕部姿态（度，绕腕骨局部欧拉角）：推门/扶椅为固定值；抓握/托举暴露成可调参数
    private static readonly Vector3 PushWrist = new Vector3(-32f, 0f, 0f);
    private static readonly Vector3 PressWrist = new Vector3(26f, 0f, 0f);

    [Header("腕部姿态（度，Play 模式实时调）")]
    [Tooltip("抓握腕部欧拉：x=俯仰（负=下压，别太负否则手腕下卷），y=偏航，z=翻滚（让掌心转向身体一侧，侧面持握的关键）")]
    [SerializeField] private Vector3 gripWristEuler = new Vector3(-6f, 12f, 20f);
    [Tooltip("托举腕部欧拉（双手抱物，掌心相向）")]
    [SerializeField] private Vector3 carryWristEuler = new Vector3(-10f, 14f, 18f);

    // 每手：5指×3节 + 腕 + 前臂，双手共 34
    private const int ExpectedBoneCount = 34;

    // 供持物锚点贴合手部用（ArmsPitchFollow 每帧查询）
    public Transform WristLeft { get; private set; }
    public Transform WristRight { get; private set; }
    private Transform middleBaseLeft;   // Middle.01.*：掌心延长方向
    private Transform middleBaseRight;
    private Transform indexBaseLeft;    // Index.01.* 与 Little.01.*：掌面横轴 → 掌面法线
    private Transform indexBaseRight;
    private Transform littleBaseLeft;
    private Transform littleBaseRight;

    private readonly Dictionary<Transform, Quaternion> defaultRots = new Dictionary<Transform, Quaternion>();
    private readonly Dictionary<Transform, Quaternion> targetRots = new Dictionary<Transform, Quaternion>();
    private bool loggedWiring; // 只打一次接线日志，避免拿放刷屏

    private void Awake()
    {
        // 默认姿态只在本次会话捕获一次：之后无论怎么摆姿势，
        // 「放松」都能回到这份原始快照（之前每次测试都重捕获，导致放松失效）
        if (defaultRots.Count == 0) CaptureBones();
        foreach (var pair in defaultRots) targetRots[pair.Key] = pair.Value;
    }

    private void Start()
    {
        Debug.Log("[HandPose] Play 模式启动，已捕获骨骼 " + defaultRots.Count + "/" + ExpectedBoneCount, this);
    }

    // 设置某只手的姿态，并立即写入骨骼（与编辑器测试同一路径）
    public void SetPose(Hand hand, Pose pose)
    {
        if (!loggedWiring)
        {
            loggedWiring = true;
            Debug.Log("[HandPose] 运行时接线正常，首次姿态调用：" + hand + " → " + pose
                      + "（已捕获骨骼 " + defaultRots.Count + "/" + ExpectedBoneCount + "）", this);
        }
        if (defaultRots.Count == 0) return; // 骨骼一个都没捕获到（Awake/Start 日志会说明原因）

        foreach (var pair in defaultRots)
        {
            bool isRight = pair.Key.name.EndsWith(".R");
            if (hand != Hand.Both && ((hand == Hand.Right) != isRight)) continue;
            Quaternion target = pair.Value * OffsetFor(pair.Key.name, pose, isRight);
            targetRots[pair.Key] = target;
            pair.Key.localRotation = target; // 直接写入，不经过插值
        }
    }

    // 手部坐标系查询：腕骨位置 + 掌心方向（腕指向中指根）。持物锚点用它贴合真实手部。
    // 返回 false 表示对应骨骼没捕获到（调用方退回固定偏移）
    public bool TryGetHandFrame(bool right, out Vector3 wristPos, out Vector3 palmForward)
    {
        Transform wrist = right ? WristRight : WristLeft;
        Transform middle = right ? middleBaseRight : middleBaseLeft;
        if (wrist == null || middle == null)
        {
            wristPos = Vector3.zero;
            palmForward = Vector3.forward;
            return false;
        }
        wristPos = wrist.position;
        palmForward = middle.position - wrist.position;
        if (palmForward.sqrMagnitude < 1e-6f) palmForward = Vector3.forward;
        else palmForward.Normalize();
        return true;
    }

    // 掌面坐标系查询：腕位置 + 掌心方向(腕→中指根) + 掌面法线(中指×小指方向叉积)。
    // 物品锚点 = 腕 + 掌心方向×along + 法线×normal，落在卷曲手指围成的握持区里；
    // 法线朝掌侧还是背侧取决于骨骼轴定义，normalDist 支持正负号翻转来修正。
    public bool TryGetPalmFrame(bool right, out Vector3 wristPos, out Vector3 along, out Vector3 normal)
    {
        Transform wrist = right ? WristRight : WristLeft;
        Transform middle = right ? middleBaseRight : middleBaseLeft;
        Transform index = right ? indexBaseRight : indexBaseLeft;
        Transform little = right ? littleBaseRight : littleBaseLeft;
        if (wrist == null || middle == null || index == null || little == null)
        {
            wristPos = Vector3.zero;
            along = Vector3.forward;
            normal = Vector3.up;
            return false;
        }
        wristPos = wrist.position;
        Vector3 alongV = middle.position - wrist.position;
        along = alongV.sqrMagnitude > 1e-6f ? alongV.normalized : Vector3.forward;
        Vector3 acrossV = little.position - index.position;
        Vector3 across = acrossV.sqrMagnitude > 1e-6f ? acrossV.normalized : Vector3.right;
        normal = Vector3.Cross(along, across);
        if (normal.sqrMagnitude < 1e-6f) normal = Vector3.up;
        else normal.Normalize();
        return true;
    }

    // 计算某块骨骼在某姿态下的局部旋转偏移
    private Quaternion OffsetFor(string boneName, Pose pose, bool isRight)
    {
        if (pose == Pose.None) return Quaternion.identity;

        // 前臂骨：整只手臂的摆动（绕肘部），让手势有真实的“手臂动作”
        if (boneName.StartsWith("Forearm"))
        {
            Vector3 f = pose == Pose.Grip ? gripForearm
                      : pose == Pose.Carry ? carryForearm
                      : pose == Pose.Push ? pushForearm
                      : pressForearm;
            float yaw = isRight ? -f.y : f.y; // y 是左右镜像的偏航幅度
            if (isRight && mirrorRightHand) yaw = -yaw;
            return Quaternion.Euler(f.x, yaw, f.z);
        }

        if (boneName.StartsWith("Wrist"))
        {
            Vector3 w = pose == Pose.Grip ? gripWristEuler
                      : pose == Pose.Carry ? carryWristEuler
                      : pose == Pose.Push ? PushWrist
                      : PressWrist;
            if (isRight && mirrorRightHand) w = new Vector3(w.x, -w.y, -w.z);
            return Quaternion.Euler(w);
        }

        bool isThumb = boneName.StartsWith("Thumb");
        float[] fingerTable;
        float[] thumbTable;
        switch (pose)
        {
            case Pose.Grip: fingerTable = GripFinger; thumbTable = GripThumb; break;
            case Pose.Carry: fingerTable = CarryFinger; thumbTable = CarryThumb; break;
            case Pose.Push: fingerTable = PushFinger; thumbTable = PushThumb; break;
            default: fingerTable = PressFinger; thumbTable = PressThumb; break;
        }
        float[] table = isThumb ? thumbTable : fingerTable;
        float sign = isRight && mirrorRightHand ? -1f : 1f;
        return Quaternion.Euler(sign * table[JointIndex(boneName) - 1], 0f, 0f);
    }

    // "Index.02.L" → 2（第几节指骨）
    private static int JointIndex(string boneName)
    {
        if (boneName.Contains(".01.")) return 1;
        if (boneName.Contains(".02.")) return 2;
        return 3;
    }

    // ==== 骨骼捕获与诊断 ====

    private void CaptureBones()
    {
        defaultRots.Clear();
        targetRots.Clear();

        // 腕骨与指根：持物锚点贴合手部用的定位参考
        WristLeft = FindDescendant(transform, "Wrist.L");
        WristRight = FindDescendant(transform, "Wrist.R");
        middleBaseLeft = FindDescendant(transform, "Middle.01.L");
        middleBaseRight = FindDescendant(transform, "Middle.01.R");
        indexBaseLeft = FindDescendant(transform, "Index.01.L");
        indexBaseRight = FindDescendant(transform, "Index.01.R");
        littleBaseLeft = FindDescendant(transform, "Little.01.L");
        littleBaseRight = FindDescendant(transform, "Little.01.R");

        var missing = new List<string>();
        for (int side = 0; side < 2; side++)
        {
            string suffix = side == 0 ? ".L" : ".R";
            foreach (string finger in FingerNames)
                for (int j = 1; j <= 3; j++)
                    CaptureBone(finger + "." + j.ToString("00") + suffix, missing);
            for (int j = 1; j <= 3; j++)
                CaptureBone("Thumb." + j.ToString("00") + suffix, missing);
            CaptureBone("Wrist" + suffix, missing);
            CaptureBone("Forearm" + suffix, missing);
        }

        if (defaultRots.Count == ExpectedBoneCount)
        {
            Debug.Log("[HandPose] 捕获骨骼 " + defaultRots.Count + "/" + ExpectedBoneCount + "，全部就绪。", this);
        }
        else
        {
            Debug.LogWarning("[HandPose] 捕获骨骼 " + defaultRots.Count + "/" + ExpectedBoneCount
                             + (missing.Count > 0 ? "，缺失：" + string.Join(", ", missing.ToArray()) : ""), this);
            if (defaultRots.Count == 0)
            {
                // 一个都没抓到：把 rig 实际层级打出来，直接对照修命名
                var names = new List<string>();
                CollectNames(transform, names, 0);
                Debug.LogWarning("[HandPose] 未找到任何目标骨骼！实际层级如下：\n" + string.Join("\n", names.ToArray()), this);
            }
        }
    }

    // 先按精确名找；找不到再按“去掉点号、忽略大小写”找（防导入改名）
    private void CaptureBone(string boneName, List<string> missing)
    {
        Transform bone = FindDescendant(transform, boneName);
        if (bone == null) bone = FindDescendantNormalized(transform, Normalize(boneName));
        if (bone == null) { missing.Add(boneName); return; }
        defaultRots[bone] = bone.localRotation;
    }

    private static string Normalize(string s) => s.Replace(".", "").Replace(" ", "").ToLowerInvariant();

    private static Transform FindDescendant(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform child in root)
        {
            Transform result = FindDescendant(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private static Transform FindDescendantNormalized(Transform root, string normalized)
    {
        if (Normalize(root.name) == normalized) return root;
        foreach (Transform child in root)
        {
            Transform result = FindDescendantNormalized(child, normalized);
            if (result != null) return result;
        }
        return null;
    }

    // 深度优先收集层级名（最多 60 个），用于“全都没找到”时的诊断输出
    private static void CollectNames(Transform t, List<string> names, int depth)
    {
        if (names.Count >= 60) return;
        if (depth > 0) names.Add(new string(' ', Mathf.Min(depth, 5) * 2) + t.name);
        foreach (Transform child in t) CollectNames(child, names, depth + 1);
    }

    // ==== 编辑模式调试菜单 ====

#if UNITY_EDITOR
    [ContextMenu("测试：抓握（右手）")]
    private void TestGrip()
    {
        if (defaultRots.Count == 0) CaptureBones(); // 只在首次捕获，之后复用原始快照
        SetPose(Hand.Right, Pose.Grip);
        Debug.Log("[HandPose] 编辑器测试：右手抓握已应用。想复位点「还原：FBX 原始姿态」。", this);
    }

    [ContextMenu("测试：托举（双手）")]
    private void TestCarry()
    {
        if (defaultRots.Count == 0) CaptureBones();
        SetPose(Hand.Both, Pose.Carry);
        Debug.Log("[HandPose] 编辑器测试：双手托举已应用。想复位点「还原：FBX 原始姿态」。", this);
    }

    [ContextMenu("测试：放松")]
    private void TestNone()
    {
        if (defaultRots.Count == 0) CaptureBones();
        SetPose(Hand.Both, Pose.None);
    }

    [ContextMenu("测试：推门手势（右手）")]
    private void TestPush()
    {
        if (defaultRots.Count == 0) CaptureBones();
        SetPose(Hand.Right, Pose.Push);
    }

    [ContextMenu("测试：撑压手势（双手）")]
    private void TestPress()
    {
        if (defaultRots.Count == 0) CaptureBones();
        SetPose(Hand.Both, Pose.Press);
    }

    // 从 FBX 源资产读回全部骨骼的原始旋转：彻底复位（只动旋转，保留位置调整），
    // 并重新捕获默认快照。场景手部姿态被测试弄乱时点这个。
    [ContextMenu("还原：FBX 原始姿态")]
    private void RestoreFromFbx()
    {
        var bones = new List<Transform>();
        CollectAllBones(transform, bones);
        int restored = 0;
        foreach (Transform bone in bones)
        {
            Transform source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(bone) as Transform;
            if (source == null) continue;
            Undo.RecordObject(bone, "还原手部姿态");
            bone.localRotation = source.localRotation;
            restored++;
        }
        CaptureBones(); // 重新捕获干净默认
        Debug.Log("[HandPose] 已从 FBX 还原 " + restored + " 个骨骼的旋转，并重新捕获默认姿态。", this);
    }

    private static void CollectAllBones(Transform root, List<Transform> list)
    {
        foreach (Transform child in root)
        {
            list.Add(child);
            CollectAllBones(child, list);
        }
    }
#endif
}
