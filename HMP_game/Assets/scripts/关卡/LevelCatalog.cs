// 关卡目录——《关卡架构设计（骨架版）》§四：解析注册表，按 id 查询、按 order 排序、
// 结合通关记录计算锁定态（骨架期按数据呈现，不做真实存档）。
//
// 用途：
//   - SceneSelectUI 改造后从中生成选关卡片（§五，属协作者模块，本轮未动）；
//   - LevelBootstrapper 直开场景时用它按 sceneName 反查默认关卡（§二「直接 Play 关卡场景」）。
using System.Collections.Generic;
using UnityEngine;

namespace HMProtection.Core
{
    public sealed class LevelCatalog
    {
        private readonly List<LevelEntry> levels = new List<LevelEntry>();

        /// <summary>已按 order 升序排好。</summary>
        public IReadOnlyList<LevelEntry> Levels => levels;

        public int Count => levels.Count;

        /// <summary>可选关卡（status == available）。</summary>
        public List<LevelEntry> Available()
        {
            var list = new List<LevelEntry>();
            for (int i = 0; i < levels.Count; i++)
                if (levels[i].status == "available") list.Add(levels[i]);
            return list;
        }

        public LevelEntry Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < levels.Count; i++)
                if (levels[i].id == id) return levels[i];
            return null;
        }

        /// <summary>按场景名反查关卡——直开场景（开发调试）走这条。</summary>
        public LevelEntry FindByScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return null;
            for (int i = 0; i < levels.Count; i++)
                if (levels[i].sceneName == sceneName) return levels[i];
            return null;
        }

        /// <summary>锁定态：unlockAfter 为空即可用；否则需该关已通关。</summary>
        public bool IsUnlocked(LevelEntry entry, ICollection<string> clearedLevelIds)
        {
            if (entry == null) return false;
            if (entry.status != "locked") return true;
            if (string.IsNullOrEmpty(entry.unlockAfter)) return true;
            return clearedLevelIds != null && clearedLevelIds.Contains(entry.unlockAfter);
        }

        /// <summary>读注册表并建目录。失败时 error 已由 ConfigLoader 打进 Console。</summary>
        public static bool TryLoad(out LevelCatalog catalog, out string error)
        {
            catalog = null;
            if (!ConfigLoader.TryLoadRegistry(out LevelRegistry registry, out error)) return false;

            catalog = new LevelCatalog();
            for (int i = 0; i < registry.levels.Length; i++)
            {
                LevelEntry e = registry.levels[i];
                if (e == null) continue;
                if (string.IsNullOrEmpty(e.id))
                {
                    Debug.LogWarning("[LevelCatalog] 跳过 id 为空的注册表条目（index " + i + "）");
                    continue;
                }
                if (string.IsNullOrEmpty(e.status)) e.status = "available";
                if (string.IsNullOrEmpty(e.configPath)) e.configPath = ConfigLoader.LevelFolder + e.id;
                catalog.levels.Add(e);
            }
            catalog.levels.Sort((a, b) => a.order.CompareTo(b.order));
            if (catalog.levels.Count == 0)
            {
                error = "注册表里没有有效关卡条目";
                Debug.LogError("[LevelCatalog] " + error);
                catalog = null;
                return false;
            }
            return true;
        }
    }
}
