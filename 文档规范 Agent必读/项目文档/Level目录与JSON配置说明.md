# Level 目录与 JSON 配置说明

## 已落地的关卡包

统一入口为 `HMP_game/Assets/Levels/<levelId>/`。当前建立三个包：

| levelId | 内容 | 来源 |
|---|---|---|
| `level_1_initial_fire` | 办公室初起火灾，保留原阶段流程与题库 | 办公室实体试点场景的独立副本 |
| `third_scene_shared_modules_a` | 通用模块训练 A，火源初始 Small / Medium | 通用模块试点场景的独立副本 |
| `third_scene_shared_modules_b` | 通用模块训练 B，火源初始 Large / SmokeOnly | 通用模块试点场景的独立副本 |

每包有独立场景 GUID 和 Definition，不再让 A/B 共用同一个场景文件。原 `Assets/Scenes` 场景、旧注册表和菜单入口保留用于兼容和回归；本次没有自动切换正式菜单入口，也没有把恢复快照、菜单或素材测试场景误当作新增教学关。

```text
Assets/Levels/<levelId>/
  Scenes/<levelId>.unity
  Config/
    level.json                      # 配置源文件
    flow.json                       # 可选，第一关 Lua 阶段序列配置
    scene_choices.json              # 包内题库 TextAsset
    shared-assets.json              # 创建时记录的包外资产依赖，不是运行配置
    Resources/Levels/<levelId>/
      legacy.json                   # 可选的历史兼容资源，不是第一关当前入口
  Scripts/main.lua                  # 关卡脚本入口
  Art/
    Models/ Materials/ Textures/ Prefabs/ Audio/
    README.md
  Runtime/<levelId>.asset            # Unity 引用与 Lua 源码缓存
```

`Art` 用于关卡专属资产。目前场景继续引用已有办公室、消防道具、火焰特效等共享美术，Art 子目录预留，没有复制共享库或生成替代美术。公共资源仍放在原来的 `OfficeClassic`、`Props`、`VFX`、UI 等位置；公共代码仍在 Framework、GameModules、GameContent/Common。关卡包因此不是可脱离项目单独运行的资源包，迁移到其他工程时必须携带共享依赖。

## level.json v1

文件固定放在 `Config/level.json`；所有普通资产路径相对于关卡包根目录，**不是相对于 Config 目录**。

```json
{
  "schemaVersion": 1,
  "levelId": "third_scene_shared_modules_a",
  "displayName": "通用模块训练 A",
  "scene": "Scenes/third_scene_shared_modules_a.unity",
  "script": { "enabled": true, "entry": "Scripts/main.lua" },
  "artRoots": ["Art"],
  "legacy": { "enabled": false, "configResource": "" },
  "initialFires": [
    { "entityId": "fire.third.a", "state": "Small" },
    { "entityId": "fire.third.b", "state": "Medium" }
  ]
}
```

| 字段 | 规则与用途 |
|---|---|
| schemaVersion | 必须为整数 1；未来不兼容改动升级版本 |
| levelId | `[a-z0-9_-]+`，必须与根文件夹名完全相同 |
| displayName | 非空显示名称 |
| scene | 本包 `Scenes/*.unity`；必须存在且加入 Build Settings |
| script.enabled | 是否在该关 scope 和 session 就绪后启动本包 Lua。启用后在 scope 和 session 就绪时启动本包 Lua。 |
| script.entry | 本包 `Scripts/*.lua`；关闭 Lua 时也必须存在。启用时需返回带 `new(host,json)`、`update(self,delta,unscaled)`、`stop(self)` 的 module table。 |
| script.config | 可选的本包 `Config/*.json`；编辑器刷新时作为 TextAsset 写入 `LevelDefinition.scriptConfig`。第一关使用 `Config/flow.json` 声明阶段序列。旧配置省略该字段时，运行时资产不得绑定脚本配置。 |
| artRoots | 本包 Art 或其子目录；允许多个不重复目录 |
| legacy.enabled | 是否启动旧的兼容阶段驱动；第一关当前为 false |
| legacy.configResource | legacy.enabled 为 true 时使用的 Resources.Load 相对名，不带扩展名 |
| initialFires | 初始火源状态；entityId 必须在场景实体系统中存在且有火焰能力，不可重复 |

火势值大小写固定为 `None`、`SmokeOnly`、`Small`、`Medium`、`Large`。不能传枚举数字。

JSON 所有列出的字段均必填，拒绝未知字段、重复属性、错误类型、尾随第二个 JSON 值；配置 UTF-8 上限 1 MiB，嵌套深度上限 32。路径用 `/`，拒绝绝对路径、`..`、`.`、反斜杠、冒号、空路径段、Windows/URI 保留字符和末尾空格/点；中文文件名可以使用。

[JSON Schema](level.schema.json) 用于编辑器提示，真实运行校验以 `LevelPackageConfig.TryParse` 为准。Schema 不能表达按 entityId 唯一等全部跨字段规则，资源是否存在也必须由 Unity 验证。

## 配置如何进入运行时

`LevelDefinition.packageConfig` 直接引用 JSON TextAsset，`packageRoot` 记录包目录，`questionCatalog` 引用包内题库，`scriptConfig` 可选引用 `script.config` 声明的 JSON，`luaSource` 缓存入口脚本，`luaApiSource` 缓存共享 `component_api.lua`。每次 Validate/PrepareLevel 都重新解析 JSON，覆盖派生的场景、ID、火势和流程开关，不依赖生成时的旧字段值。

这样 Player 可以使用 Unity 序列化引用，无需在运行时用 File.ReadAllText 读取 `Assets/...`。JSON 路径用于编辑器资源校验和场景导航；它们不是发布后的磁盘路径。Lua 的 `.lua` 源文件由编辑器写入 Definition 字符串，Player 无需直接读取 Assets 文件夹。

第一关已关闭 `legacy.enabled`，阶段序列改由本包 `Config/flow.json` 供 Lua 读取。`LevelFlowRunner` 仍作为 C# 的逐阶段表现、答题计分、教官与结算服务，不再把包内 Resources 的旧阶段 JSON 当作运行入口。旧 Resources 文件可保留作历史兼容资源，但不参与第一关当前流程。

Lua 共享库为 `StreamingAssets/Lua/component_api.lua`，编辑器刷新时同入口脚本一起写入 Definition 缓存。运行时由 MoonSharp 创建 VM 并自动传入 host/json，作者不应手工注入 CLR bridge。A/B 的 `main.lua` 已启用：它们发出 `script.level.started`、响应火焰变化发出 `script.level.fire_changed`，并以 presentation timer 发出 `script.level.ready`。第一关同样启用 Lua，用 `level.config`、`flow.prepare`、`flow.stage`、`flow.status` 与 `flow.finish` 编排 11 个阶段。

## 编辑与验证流程

- `Tools/Levels/Create Packages`：首次生成三个样本包；已有包只验证，不覆盖场景、JSON、Lua 或美术。
- `Tools/Levels/Refresh Runtime Definitions`：修改 JSON、脚本配置、入口 Lua 或共享 API 后更新 Runtime 资产中的派生字段、脚本配置引用和两份 Lua 缓存；源文件不被覆盖。
- `Tools/Levels/Validate Packages`：扫描 Levels 下全部包，验证配置、路径、资源、两份 Lua 缓存、场景绑定、缺失脚本和 Build Settings。启用脚本会额外做 MoonSharp 语法编译，不执行脚本。
- `Tools/Levels/Check Package Authoring`：验证重复生成、缺失资源和过期缓存的处理；先刷新 Runtime 派生资产，随后临时修改并恢复配置。建议在验证副本执行。
- `Tools/Levels/Check Level Packages`：实际运行 A → B → 办公室切换检查。请先保存场景。

新增关卡时建立相同目录，使用 Unity 复制现有关卡场景和 Definition 以获得新 GUID，修改 JSON levelId/scene 和 Runtime 文件名，重绑 Definition 的 packageRoot/packageConfig/questionCatalog 与场景 Bootstrapper.definition，然后 Refresh Runtime Definitions、Validate Packages。场景的实体 ID、角色和能力仍由场景绑定组件维护，JSON 不依靠 GameObject 名称猜测目标。

可直接打开包内 `.unity` 运行，也可持有 Runtime Definition 调用：

```csharp
if (AppNavigationService.Instance.TryLoadDefinition(definition, out var operation, out var error))
    operation.AllowActivation();
```

构建前 `LevelPackageBuildGuard` 自动执行包校验，发现配置错误、Lua 缓存过期或缺失资产时阻止构建。主菜单 `SceneSelectUI.officeDefinition` 已绑定第一关包 Definition；菜单、UI、CG 和完整流程仍需随本次迁移单独验收。

## 既有基础验证记录

2026-09-23，Unity 6000.6.0f1 独立验证副本中完成：

| 验证 | 结果 |
|---|---|
| EditMode | 61/61 通过（含 16 项关卡配置检查）；[XML](验证记录/Level目录/edit-tests.xml) |
| 三包静态验证 | 全部通过，场景/JSON/Lua/资源引用/Build Settings 一致；[报告](验证记录/Level目录/packages.txt) |
| 实际关卡切换 | 历史基础记录：A → B → 办公室的导航、初始火势、配置重新读取、旧 session/token 失效与停止释放；第一关迁移后的完整流程需重新执行专门验证。 [报告](验证记录/Level目录/integration.txt) |
| 编辑器工具 | 历史记录：重复创建文件 SHA-256 不变、保留已打开场景、拒绝过期 Lua 缓存/缺失场景/当时未接入 VM 却启用 Lua；[报告](验证记录/Level目录/authoring.txt) |
| Lua 入口 | 三包 main.lua 的 new/update/stop 在临时 Lua 5.5 VM + mock host 下通过，stop 释放 client；[报告](验证记录/Level目录/lua-entry.txt) |

负向检查修改临时验证副本并在 finally 恢复；测试完成后再同步新包和新增 Build Settings 条目到主项目。没有运行用户的编辑器来切场景，也没有覆盖原生产美术、旧场景或旧 JSON。

可复现入口：`LevelPackageTools.CreateAllBatch`（完整命名空间 `HMProtection.LevelPackages`）、`LevelPackageIntegrationChecks.BeginBatch`、`LevelPackageAuthoringChecks.RunBatch`。三者报告位于 Unity `Library/`，归档版本见上表。

本轮历史验证未生成独立 Player，也未执行移动端/WebGL/IL2CPP 验证；其中 mock-host Lua 结论不代表现已接入的 MoonSharp 运行时。第一关本次迁移的待执行项目见 [初起火灾第一关迁移与验证](初起火灾第一关迁移与验证.md)。

第一关已完成 Lua 编排迁移及真实菜单、成功/失败结算、重开和退出验证；当前结果见 [初起火灾第一关迁移与验证](初起火灾第一关迁移与验证.md)。
