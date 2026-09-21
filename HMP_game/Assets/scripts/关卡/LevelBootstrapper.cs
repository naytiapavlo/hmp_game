// 关卡场景适配器——《关卡架构设计（骨架版）》§四：
//   「场景适配器：每关场景一个；持有默认关卡 id（直开场景兜底）；
//     锚点子物体按 JSON 名字路径解析；PrepareLevel(config) 定位玩家、
//     暴露场景侧能力（俯视相机/起火点/天花板显隐桥接等后续阶段接入）」
//
// §二：「直接 Play 关卡场景（开发调试常用）也走 LevelBootstrapper 的默认关卡配置，
//       行为与从主菜单进入一致。」
//
// 职责边界：本组件只做「场景 ↔ 配置」的对接与玩家定位，不含流程逻辑；
// 状态机引擎（阶段序列 / 计时 / 火势）在 LevelFlowRunner 上。
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HMProtection.Core
{
    [DisallowMultipleComponent]
    public sealed class LevelBootstrapper : MonoBehaviour
    {
        [Tooltip("默认关卡 id：直开场景时用它。留空则按当前场景名在注册表里反查。")]
        [SerializeField] private string defaultLevelId = "level_1_initial_fire";

        [Tooltip("出生点锚点的名字路径（相对本物体），由 Assets/Editor/LevelSetup.cs 生成")]
        [SerializeField] private string spawnAnchorPath = "锚点_出生点";

        [Tooltip("是否在 Start 时自动开局。关闭后需外部（如 LevelManager）显式调 PrepareLevel()")]
        [SerializeField] private bool autoStart = true;

        [Tooltip("是否把玩家定位到出生点锚点")]
        [SerializeField] private bool teleportPlayerToSpawn = true;

        /// <summary>本场景对应的关卡注册表条目；解析失败为 null。</summary>
        public LevelEntry Entry { get; private set; }

        /// <summary>本关配置；解析失败为 null。</summary>
        public LevelConfigDto Config { get; private set; }

        /// <summary>配置是否已就绪。</summary>
        public bool Ready { get; private set; }

        /// <summary>最近一次失败原因（可直接展示）。</summary>
        public string LastError { get; private set; }

        /// <summary>解析完成、玩家已定位后触发（引擎据此开局）。</summary>
        public event Action<LevelBootstrapper> LevelPrepared;

        private LevelFlowRunner runner;
        private body player;

        private void Awake()
        {
            runner = GetComponent<LevelFlowRunner>();
            if (runner == null) runner = gameObject.AddComponent<LevelFlowRunner>();
        }

        private void Start()
        {
            if (autoStart) PrepareLevel();
        }

        /// <summary>解析配置 + 定位玩家 + 把配置交给引擎。返回是否成功。</summary>
        public bool PrepareLevel()
        {
            LastError = null;

            if (!LevelCatalog.TryLoad(out LevelCatalog catalog, out string error))
            {
                LastError = error;
                return false;
            }

            Entry = catalog.Find(defaultLevelId);
            if (Entry == null) Entry = catalog.FindByScene(SceneManager.GetActiveScene().name);
            if (Entry == null)
            {
                LastError = "注册表里找不到关卡：defaultLevelId=\"" + defaultLevelId
                            + "\"，场景名=\"" + SceneManager.GetActiveScene().name + "\"";
                Debug.LogError("[LevelBoot] " + LastError, this);
                return false;
            }

            if (!ConfigLoader.TryLoadLevel(Entry.configPath, out LevelConfigDto config, out error))
            {
                LastError = error;
                return false;
            }
            Config = config;

            ResolvePlayer();
            if (teleportPlayerToSpawn) TeleportPlayerToSpawn();

            Ready = true;
            Debug.Log("[LevelBoot] 关卡就绪：" + Entry.id + "（" + config.displayName + "）"
                      + "　阶段 " + config.stages.Length + " 段　火势 cue " + config.fireCues.Length + " 条", this);

            LevelPrepared?.Invoke(this);
            if (runner != null) runner.Begin(Entry, config);
            else Debug.LogWarning("[LevelBoot] 没有 LevelFlowRunner，配置已就绪但不会跑流程", this);
            return true;
        }

        /// <summary>按名字路径找场景物体，支持 "Root/Child/GrandChild" 与相对本物体的 "锚点_出生点"。</summary>
        public Transform ResolvePath(string namePath)
        {
            if (string.IsNullOrEmpty(namePath)) return null;
            string[] parts = namePath.Split('/');
            int start = 0;
            Transform current = null;

            // 第一段若命中某个场景根物体，就从根开始；否则从本物体开始
            Scene scene = gameObject.scene;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name != parts[0]) continue;
                current = root.transform;
                start = 1;
                break;
            }
            if (current == null) current = transform;

            for (int i = start; i < parts.Length; i++)
            {
                current = current.Find(parts[i]);
                if (current == null) return null;
            }
            return current;
        }

        /// <summary>出生点世界坐标；锚点缺失时返回 false。</summary>
        public bool TryGetSpawnPosition(out Vector3 position)
        {
            position = Vector3.zero;
            Transform anchor = ResolvePath(spawnAnchorPath);
            if (anchor == null) return false;
            position = anchor.position;
            return true;
        }

        private void ResolvePlayer()
        {
            if (player == null) player = FindAnyObjectByType<body>();
        }

        /// <summary>把玩家挪到出生点。CharacterController 会跟直接写 transform 打架，因此先禁用再启用。</summary>
        public void TeleportPlayerToSpawn()
        {
            ResolvePlayer();
            if (player == null)
            {
                Debug.LogWarning("[LevelBoot] 场景里没有 body（第一人称玩家），跳过出生点定位", this);
                return;
            }
            if (!TryGetSpawnPosition(out Vector3 spawn))
            {
                Debug.LogWarning("[LevelBoot] 找不到出生点锚点 \"" + spawnAnchorPath + "\"，跳过定位", this);
                return;
            }

            var controller = player.GetComponent<CharacterController>();
            bool hadController = controller != null && controller.enabled;
            if (hadController) controller.enabled = false;
            player.transform.position = spawn;
            if (hadController) controller.enabled = true;

            Debug.Log("[LevelBoot] 玩家定位到出生点 " + spawn.ToString("F2"), this);
        }
    }
}
