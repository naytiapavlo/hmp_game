# 关卡配置（JSON）

对应《关卡架构设计（骨架版）》§三。运行时由 `HMProtection.Core.ConfigLoader` 用
`Resources.Load<TextAsset>` 读取并 `JsonUtility` 反序列化，加载后跑一遍 `Normalize()`
把缺省字段补成计划书口径的默认值——**所以 JSON 里不必写全每个字段**。

```
Assets/Resources/Configs/
├─ levels.json                               关卡注册表（列表 / 顺序 / 解锁 / 卡片文案）
└─ Levels/level_1_initial_fire.json          第一关：初起火灾应对（办公室场景）
    Levels/level_2_extinguisher_fire.json    第二关：灭火器扑救（第三场景）
```

## 加一个新关卡（三步，零逻辑代码）

1. 做关卡场景，放一个 `LevelBootstrapper`（含 `锚点_出生点` 子物体；Editor 菜单 `Tools/Level1/Install Flow Runner`
   是第一关的装配器模板，第二关见 `Tools/Level2/接入第二关`）；
2. 写 `Resources/Configs/Levels/level_xxx.json`；
3. `levels.json` 的 `levels` 数组加一条 → **选关窗口自动出现新卡片**（`SceneSelectUI` 读注册表生成，2026-09-22 起已实现）。

**记得把场景加进 `EditorBuildSettings`（Scenes In Build）**——运行时不按路径找场景，只按 `sceneName` 在构建列表里反查；
没加进去时选关窗口会打印 `[SceneSelect] 关卡场景不在构建列表里：…`，不会静默黑屏。

## levels.json 字段（§3.1）

| 字段 | 说明 |
|---|---|
| `id` | 关卡唯一标识（内部引用 / 解锁依赖 / 存档记录用） |
| `displayName` | 关卡显示名（中文，用于日志与内部引用） |
| `sceneName` | 目标场景名；`LevelBootstrapper` 直开场景时按它反查，也是构建列表里的查找键 |
| `configPath` | 该关 JSON 在 Resources 下的路径（不含扩展名） |
| `status` | `available` / `locked` / `comingSoon`（选关卡片三态，非 `available` 一律不可选） |
| `unlockAfter` | 锁定态的解锁条件：另一关的 `id`；空 = 不依赖 |
| `order` | 卡片排序 |
| `transitionVideo` | 进关 CG 视频名（`Assets/CG` 下的 mp4，空 = 无 CG 直接进） |
| `cardTitle` | 选关卡片的标题（主菜单视觉是英文，卡片文案走这里；空 = 用 `displayName`） |
| `cardDescription` | 选关卡片的副标题（空 = 用 `displayName`） |
| `cardImage` | 选关卡片的切图，**Resources 下的 Sprite 路径**（如 `SceneSelection/OfficePreview`）；空 = 不显示预览图 |

## 单关配置字段（§3.2）

### `stages[]` — 状态机阶段序列（由 `LevelFlowRunner` 执行）

| 字段 | 说明 |
|---|---|
| `id` / `displayName` | 阶段标识 / 显示名 |
| `kind` | `cutscene` / `quiz` / `freeRoam` / `result` / `video` / `settlement`。决定引擎用哪段逻辑执行；其余 kind 一律按 `duration` 定时推进 |
| `duration` | 阶段时长（秒）。`quiz` / `freeRoam` 段的 `duration` 即作答时限 / 取物倒计时 |
| `timed` | 是否参与倒计时。**计划书硬约束：只有答题与操作段计时**，其余设 `false` |
| `fireCue` | 阶段开始时下发的火势 cue id |
| `fireCueAt` / `secondaryFireCue` | `fireCueAt > 0` 时，在阶段内第 N 秒切到第二条 cue（失火动画「冒烟 → 起火」用这个） |
| `playerControl` | 阶段内是否允许玩家操控 |
| `frameFire` | 阶段开始时把玩家相机转向起火点（验收：一进 Play 就能看见火焰） |
| `freezeFire` | 阶段内是否冻结火焰粒子。计划书 §2 说「动画期间冻结」，§6.3 只列暂停/解说/等待继续；失火动画的火焰本身就是动画内容，故默认 `false` |
| `correctFireCue` / `wrongFireCue` | 仅 `result` 段：正确分支 / 错误分支的火势 cue |
| `correctEndAt` / `correctEndFireCue` | 仅 `result` 段：第 N 秒把正确分支切到「余烟后熄灭」 |

`quiz` 段结束时会自动进入「解说 →（可选）等待继续」，期间**火焰与计时一起冻结**；
`freeRoam` 段监听 `GuidanceSystem.OnDestinationReached`，抵达或超时都推进，不阻断流程。

### `fireCues[]` — 火势提示

| 字段 | 说明 |
|---|---|
| `id` | 被 `stages[].fireCue` 引用的 id |
| `level` | `FireLevel` 枚举名：`None` / `SmokeOnly` / `Small` / `Medium` / `Large` |
| `intensity` / `scale` / `smokeAmount` | 0–1 表现参数（`FireVfx` 的三个对外参数） |
| `useVfxParams` | 是否下发上面三个参数。`false` = 只下发分级（架构文档 §八：FireEffectController 只收「火势几级」） |

### `quiz` — 题库口径

`questionId` 对应 `Assets/StreamingAssets/Quiz/scene_choices.json` 里的题；
`correctOptionId` 对应其中某个 `option.id`（当前 `office_fire_first_action` 的正确答案是断电处置 `unplug_strip`）。
计分口径按计划书 §2：4 题 × 7.5 分 = 30 分，至少 3 题正确。

`waitForContinue`：解说后是否等待点击「继续」。解说 UI 尚未交付（计划书 §5.2），
因此默认 `false` ＝ 解说计满自动推进；UI 到位后改成 `true` 即可。

### `guidanceRoutes[]` — 指引路线（骨架期只留字段）

渲染引擎见架构文档 §八 阶段三；当前自由移动段的路线由 `OfficeFireChoiceFlow` 作答后
直接调 `GuidanceSystem.ShowRoute` 显示。
