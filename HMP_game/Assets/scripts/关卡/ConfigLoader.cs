// 配置加载器——《关卡架构设计（骨架版）》§四：读 levels.json 与单关 json；
// 「缺字段、缺场景、JSON 语法错误在 Console 给明确报错」。
//
// 路径口径（§3.3）：JSON 放 Resources，运行时 Resources.Load<TextAsset> 直接可读。
//   Configs/levels                                   注册表
//   Configs/Levels/level_1_initial_fire              单关配置
//
// Normalize()：JsonUtility 不能依赖字段初始值，缺字段时取值不可靠，
// 所以这里统一把空/非法字段补成计划书口径的默认值，JSON 里就不必写全每个字段。
using UnityEngine;

namespace HMProtection.Core
{
    public static class ConfigLoader
    {
        public const string RegistryPath = "Configs/levels";
        public const string LevelFolder = "Configs/Levels/";

        /// <summary>读取关卡注册表。失败时 error 含可直接贴进 Console 的原因。</summary>
        public static bool TryLoadRegistry(out LevelRegistry registry, out string error)
        {
            registry = null;
            error = null;
            var asset = Resources.Load<TextAsset>(RegistryPath);
            if (asset == null)
            {
                error = "找不到关卡注册表 Resources/" + RegistryPath + ".json";
                Debug.LogError("[LevelConfig] " + error);
                return false;
            }
            try
            {
                registry = JsonUtility.FromJson<LevelRegistry>(asset.text);
            }
            catch (System.Exception e)
            {
                error = "关卡注册表 JSON 解析失败：" + e.Message;
                Debug.LogError("[LevelConfig] " + error);
                return false;
            }
            if (registry == null || registry.levels == null || registry.levels.Length == 0)
            {
                error = "关卡注册表为空（levels 数组缺失或长度为 0）";
                Debug.LogError("[LevelConfig] " + error);
                return false;
            }
            return true;
        }

        /// <summary>读取单关配置并补齐默认值。configPath 形如 Configs/Levels/level_1_initial_fire。</summary>
        public static bool TryLoadLevel(string configPath, out LevelConfigDto level, out string error)
        {
            level = null;
            error = null;
            if (string.IsNullOrEmpty(configPath))
            {
                error = "configPath 为空";
                Debug.LogError("[LevelConfig] " + error);
                return false;
            }
            var asset = Resources.Load<TextAsset>(configPath);
            if (asset == null)
            {
                error = "找不到关卡配置 Resources/" + configPath + ".json";
                Debug.LogError("[LevelConfig] " + error);
                return false;
            }
            try
            {
                level = JsonUtility.FromJson<LevelConfigDto>(asset.text);
            }
            catch (System.Exception e)
            {
                error = "关卡配置 JSON 解析失败（" + configPath + "）：" + e.Message;
                Debug.LogError("[LevelConfig] " + error);
                return false;
            }
            if (level == null)
            {
                error = "关卡配置解析结果为空：" + configPath;
                Debug.LogError("[LevelConfig] " + error);
                return false;
            }
            Normalize(level);
            return true;
        }

        /// <summary>把缺省/非法字段补成计划书口径的默认值，并在日志里点名被补的字段。</summary>
        public static void Normalize(LevelConfigDto level)
        {
            if (string.IsNullOrEmpty(level.displayName)) level.displayName = level.id;
            if (level.fireCues == null) level.fireCues = new FireCueDto[0];
            if (level.guidanceRoutes == null) level.guidanceRoutes = new GuidanceRouteDto[0];
            if (level.stages == null) level.stages = new StageDto[0];

            level.quiz ??= new QuizDto();
            QuizDto q = level.quiz;
            if (string.IsNullOrEmpty(q.questionId)) q.questionId = "office_fire_first_action";
            if (q.secondsPerQuestion <= 0f) q.secondsPerQuestion = 15f;   // 计划书 §5.2
            if (q.narrationSeconds < 0f) q.narrationSeconds = 6f;         // 计划书 §5.2
            if (q.totalQuestions <= 0) q.totalQuestions = 4;              // 计划书 §2
            if (q.scorePerQuestion <= 0f) q.scorePerQuestion = 7.5f;     // 计划书 §2
            if (q.passCorrectCount <= 0) q.passCorrectCount = 3;          // 计划书 §2

            for (int i = 0; i < level.fireCues.Length; i++)
            {
                FireCueDto cue = level.fireCues[i];
                if (cue == null)
                {
                    level.fireCues[i] = new FireCueDto { id = "cue_" + i, level = "None" };
                    continue;
                }
                if (string.IsNullOrEmpty(cue.level)) cue.level = "None";
                if (cue.intensity <= 0f) cue.intensity = 1f;
                if (cue.scale <= 0f) cue.scale = 1f;
                if (cue.smokeAmount < 0f) cue.smokeAmount = 1f;
            }

            for (int i = 0; i < level.stages.Length; i++)
            {
                StageDto s = level.stages[i];
                if (s == null)
                {
                    level.stages[i] = new StageDto { id = "stage_" + i, kind = "cutscene" };
                    continue;
                }
                if (string.IsNullOrEmpty(s.id)) s.id = "stage_" + i;
                if (string.IsNullOrEmpty(s.displayName)) s.displayName = s.id;
                if (string.IsNullOrEmpty(s.kind)) s.kind = "cutscene";
                if (s.duration < 0f) s.duration = 0f;
                if (s.fireCueAt < 0f) s.fireCueAt = -1f;
            }
        }
    }
}
