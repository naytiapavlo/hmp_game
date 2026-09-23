// 关卡 DTO——《关卡架构设计（骨架版）》§三「JSON 数据结构（JsonUtility DTO）」与 §四 文件清单。
//
//   Assets/Resources/Configs/levels.json                 关卡注册表（列表/顺序/解锁）
//   Assets/Resources/Configs/Levels/level_xxx.json       每关配置（出生点/阶段/题库/指引/计分）
//
// 约束：JsonUtility 只认 public 字段与 [Serializable]，不认属性/字典；
// 且**不能依赖字段初始值**（缺字段时取值不可靠）。因此 ConfigLoader 会在加载后跑
// 一遍 Normalize()，把空字段补成默认值——JSON 里就不必把每个字段都写全。
using System;

namespace HMProtection.Core
{
    /// <summary>注册表条目（§3.1）。</summary>
    [Serializable]
    public sealed class LevelEntry
    {
        public string id;
        public string displayName;
        /// <summary>目标场景名（SceneManager 按名加载）</summary>
        public string sceneName;
        /// <summary>该关 JSON 在 Resources 下的路径（不含扩展名）</summary>
        public string configPath;
        /// <summary>available / locked / comingSoon（选关卡片三态）</summary>
        public string status;
        /// <summary>锁定态的解锁条件：另一关的 id；空 = 不依赖</summary>
        public string unlockAfter;
        /// <summary>卡片排序</summary>
        public int order;
        /// <summary>进关 CG 转场视频名（Assets/CG 下的 mp4，空 = 无 CG）</summary>
        public string transitionVideo;
        /// <summary>选关卡片的标题（空 = 用 displayName）。主菜单视觉是英文，卡片文案走这里，不必改代码</summary>
        public string cardTitle;
        /// <summary>选关卡片的副标题（空 = 用 displayName）</summary>
        public string cardDescription;
        /// <summary>选关卡片的切图，Resources 下的 Sprite 路径（如 "SceneSelection/OfficePreview"，空 = 不显示预览图）</summary>
        public string cardImage;
    }

    [Serializable]
    public sealed class LevelRegistry
    {
        public LevelEntry[] levels;
    }

    /// <summary>火势提示（§3.2 fireCues）：阶段 → 火势等级 + 可选 0-1 表现参数。</summary>
    [Serializable]
    public sealed class FireCueDto
    {
        public string id;
        /// <summary>FireLevel 枚举名：None / SmokeOnly / Small / Medium / Large</summary>
        public string level;
        /// <summary>火势强度 0-1（FireVfx.SetIntensity）</summary>
        public float intensity;
        /// <summary>整体缩放（FireVfx.SetScale）</summary>
        public float scale;
        /// <summary>烟量 0-1（FireVfx.SetSmokeAmount）</summary>
        public float smokeAmount;
        /// <summary>是否把上面三个 0-1 参数下发给 FireVfx；false = 只下发分级（文档 §八：只收「火势几级」）</summary>
        public bool useVfxParams;
    }

    /// <summary>阶段（§3.2 stages）。kind 决定 LevelFlowRunner 用哪段逻辑执行。</summary>
    [Serializable]
    public sealed class StageDto
    {
        public string id;
        public string displayName;
        /// <summary>cutscene / quiz / freeRoam / result / video / settlement</summary>
        public string kind;
        /// <summary>quiz 阶段的题目 id；缺省时使用关卡 quiz.questionId。</summary>
        public string questionId;
        /// <summary>独立练习开始前回到出生点，避免沿用上一题的终点。</summary>
        public bool resetPlayerToSpawn;
        /// <summary>阶段时长（秒）</summary>
        public float duration;
        /// <summary>是否参与倒计时（§2：只有答题与操作段计时）</summary>
        public bool timed;
        /// <summary>阶段开始时下发的火势 cue id</summary>
        public string fireCue;
        /// <summary>&gt;0 时在阶段内第 fireCueAt 秒切到 secondaryFireCue（失火动画「冒烟 → 起火」用）</summary>
        public float fireCueAt;
        public string secondaryFireCue;
        /// <summary>阶段内是否允许玩家操控</summary>
        public bool playerControl;
        /// <summary>阶段开始时把玩家相机转向起火点（验收：一进 Play 就能看到火焰）</summary>
        public bool frameFire;
        /// <summary>阶段内是否冻结火焰粒子（文档 §2 提到动画期间冻结，§6.3 只列暂停/解说/等待继续）</summary>
        public bool freezeFire;
        /// <summary>result 阶段专用：正确分支火势 cue</summary>
        public string correctFireCue;
        /// <summary>result 阶段专用：错误分支火势 cue</summary>
        public string wrongFireCue;
        /// <summary>result 阶段正确分支余烟停留秒数，之后切 correctEndFireCue</summary>
        public string correctEndFireCue;
        public float correctEndAt;
    }

    /// <summary>题库（§3.2 quiz）。骨架期只做第一关那一题，字段口径按计划书。</summary>
    [Serializable]
    public sealed class QuizDto
    {
        /// <summary>交给 OfficeFireChoiceFlow 的题 id（对应 StreamingAssets/Quiz/scene_choices.json）</summary>
        public string questionId;
        /// <summary>该题正确答案的 option id</summary>
        public string correctOptionId;
        /// <summary>每题作答时限（计划书 §5.2：15 秒）</summary>
        public float secondsPerQuestion;
        /// <summary>作答后解说时长（§5.2：6 秒）</summary>
        public float narrationSeconds;
        /// <summary>解说后是否等待点击「继续」。解说 UI 未交付时设 false ＝ 计满自动推进</summary>
        public bool waitForContinue;
        /// <summary>等待「继续」的兜底超时（秒），0 = 无限等待</summary>
        public float continueFallbackSeconds;
        /// <summary>本关题目总数（结算口径：4 题 × 7.5 = 30 分）</summary>
        public int totalQuestions;
        public float scorePerQuestion;
        /// <summary>通过所需最少正确题数（计划书 §2：至少 3 题）</summary>
        public int passCorrectCount;
        /// <summary>结果动画是否按「最后一题」判定（当前只实现一题时才有可演示的两条分支）</summary>
        public bool resultUsesLastAnswer;
    }

    /// <summary>指引路线（§3.2 guidanceRoutes）。骨架期只留字段，渲染引擎见 §八 阶段三。</summary>
    [Serializable]
    public sealed class GuidanceRouteDto
    {
        public string id;
        public string label;
        /// <summary>路线终点锚点名字路径</summary>
        public string destinationPath;
        /// <summary>是否在自由移动阶段显示（第三关逃生路线为考核内容，设 false 防剧透）</summary>
        public bool visibleDuringPlay;
    }

    /// <summary>单关配置（§3.2）。</summary>
    [Serializable]
    public sealed class LevelConfigDto
    {
        public string id;
        public string displayName;
        public string description;
        /// <summary>出生点名字路径，如 LevelBootstrapper/锚点_出生点</summary>
        public string spawnPoint;
        public StageDto[] stages;
        public QuizDto quiz;
        public GuidanceRouteDto[] guidanceRoutes;
        public FireCueDto[] fireCues;
    }
}
