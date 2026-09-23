# Lua 组件 API 与事件总线接入说明

## 范围与前置条件

Lua 是关卡组件的可选编排层，业务 API 不返回 `GameObject`、反射句柄或实体实例。运行库仍需绑定最小的 C# host 桥；本接口不是恶意脚本沙箱，绑定层不应自动开放整个 CLR。C# 通过字符串 JSON 门面 `HMProtection.Scripting.LuaComponentApi` 暴露已注册实体及其能力；实体身份使用 scope/generation 绑定的不透明 token。旧 token 在实体销毁、scope 停止或下一次关卡运行后均不可复用。

当前工程已经接入每关一个的 MoonSharp VM。它只看到字符串 JSON host 门面和 Lua table，不注册 CLR、Unity 或反射 userdata：

- C# 门面：[LuaComponentApi.cs](../../HMP_game/Assets/GameContent/Common/Scripting/LuaComponentApi.cs)
- 会话内 client 管理与事件转发：[ComponentScriptService.cs](../../HMP_game/Assets/GameContent/Common/Scripting/ComponentScriptService.cs)、[ComponentEventRelay.cs](../../HMP_game/Assets/GameContent/Common/Scripting/ComponentEventRelay.cs)
- 命名事件总线：[NamedEventBus.cs](../../HMP_game/Assets/Framework/LevelSession/NamedEventBus.cs)
- Lua 客户端模块与示例：[component_api.lua](../../HMP_game/Assets/StreamingAssets/Lua/component_api.lua)、[fire_level.lua](../../HMP_game/Assets/StreamingAssets/Lua/examples/fire_level.lua)
- VM 与 JSON 桥：[LevelLuaVm.cs](../../HMP_game/Assets/Framework/LuaRuntime/LevelLuaVm.cs)
- 运行生命周期：[LevelLuaRunner.cs](../../HMP_game/Assets/GameContent/Common/Scripting/LevelLuaRunner.cs)、[LevelSessionHost.cs](../../HMP_game/Assets/GameContent/Common/EntityAdapters/LevelSessionHost.cs)

`LevelBootstrapper.PrepareLevel` 会在实体 scope、初始状态和 `LevelSessionHost` 都就绪后调用 `TryStartLua`。只有 `level.json` 的 `script.enabled` 为 `true` 时才创建 VM；A/B 与初起火灾第一关均可启用。C# 原生组件不依赖 Lua，关闭脚本不会阻止它们运行。

## 初始化、线程与生命周期

`LevelSessionHost.Bind` 在实体 scope 已就绪后启动 `LevelSession`，随后构造 `ComponentScriptService`。运行库自动创建一个 API client，把受限的 `host:Call(method,argsJson)` 和 `json.encode/decode/array/null` 传给入口模块的 `new(host,json)`；关卡作者无需自行注入 host 或 JSON，也无需创建 C# bridge。

入口源码必须返回 module table，并定义 `new(host,json)`、`update(self,delta,unscaled)`、`stop(self)`。VM 仅允许 `require("component_api")`，不支持其他 module 或文件加载。`Update` 阶段先采样原生组件并推进 session；`LateUpdate` 再执行 Lua `update`。已成功创建 instance 的 VM 在正常释放或运行中故障后至多调用一次 `stop`，随后释放 client 的订阅、timer、控制租约及自有 route；若启动在 instance 创建前失败，则无法保证调用 `stop`，但 API client 仍会被释放。

每次 `new`、`update`、`stop` 和顶层 chunk 都有 100,000 条指令预算；主动 yield 或超额会使该 VM 失败且不再恢复。所有 API 调用与 Lua tick 都在 Unity 主线程执行。共享 API 与入口源码各限制为 1 MiB UTF-8；`json.encode` 最大 64 KiB、深度 16，`json.decode` 最大 2 MiB、深度 32。

## JSON 信封、通用类型与错误码

每个 C# 调用接收 JSON 对象字符串，返回固定信封：

```json
{"apiVersion":1,"ok":true,"code":"ok","error":"","data":{}}
```

失败时仍有相同字段，`ok:false`、`data:{}`。请求 UTF-8 上限为 64 KiB，只接受一个 JSON 对象，最大深度为 16，重复键及非对象均为 `invalid_json` / `invalid_argument`。

| 类型 | 形状与约束 |
|---|---|
| `entity` | `{entity,id,scopeId}`；`entity` 是仅供后续 API 传回的不透明字符串。|
| 实体参数 | 含 `entity` 字段，值必须是当前运行中有效的 token；否则 `stale_entity`。|
| 文本 | 必填文本通常为 1–512 个字符且不可全空白；事件主题另外限制为 128，`method` / `scriptId` 限制为 128。|
| `payload` | 可选 JSON 对象；省略时 `{}`，不能传数组、标量或 JSON 文本。|
| `accepted` | 命令已被组件接收，不表示门动画、拾取、落座、交互演出或任务目标已经完成。|

`code` 大小写敏感，不得自行归一化。门面自身产生的小写 code 包括 `wrong_thread`、`session_stopped`、`invalid_argument`、`invalid_json`、`internal_error`、`unknown_method`、`missing_binding`、`missing_service`、`missing_config`、`invalid_config`、`flow_owned`、`flow_not_owned`、`stale_entity`、`operation_rejected`、`control_blocked`、`duplicate_request`、`quota_exceeded`、`unknown_timer`、`unknown_lease`、`unknown_subscription`、`reserved_topic`、`route_busy`、`unknown_route`。实体注册表/能力校验失败则直接使用 `EntityErrorCode.ToString()`，当前为 PascalCase，例如 `EntityNotFound`、`MissingCapability`、`MissingReference`、`InvalidHandle`；不要把它们写成小写错误码。

Lua 模块还可能在本地返回 `client.disposed`、`client.invalid_method`、`client.encode_failed`、`client.invalid_subscription`、`client.reentrant_update`、`transport.invalid_result`、`transport.invalid_json`、`transport.call_failed`、`api.version_mismatch`、`events.*`。这些来自 `component_api.lua`，不是 C# 门面的返回 code。

## 端点

表中“返回”是成功信封的 `data`。除标为可选的字段外均必填；`{}` 表示成功但没有额外数据。

### 会话与实体

| C# method / Lua 封装 | 参数 | 返回 |
|---|---|---|
| `session.info` / `session_info()` | `{}` | `{scopeId,levelId,scriptId,clientId,simulationTime,presentationTime}`。|
| `entity.resolve` / `resolve(id)` | `{id:string}` | 单个 `entity` 表。|
| `role.resolve` / `resolve_role(role)` | `{role:string}` | 单个 `entity` 表；scope 没有角色绑定表时为 `missing_binding`。|
| `entity.find` / `find(tag)` | `{tag:string}` | `{entities:[entity,...]}`，没有匹配时为空数组。|
| `entity.active.get` / `entity_active(entity)` | `{entity}` | `{activeSelf:boolean,activeInHierarchy:boolean}`。|
| `entity.active.set` / `set_entity_active(entity,active)` | `{entity,active:boolean}` | `{}`。|
| `entity.anchor` / `anchor(entity,slot)` | `{entity,slot?:string}`，默认 `origin` | `{position:{x,y,z},rotation:{x,y,z,w}}`。|

### 关卡脚本配置与流程编排

这些端点通过 `api:call("<method>", args)` 使用。它们只在带 `LevelBootstrapper` 的已打包关卡中可用；第一关以此读取 `script.config` 指向的 `Config/flow.json`，再编排阶段。阶段的具体表现与业务服务仍由 C# `LevelFlowRunner` 执行。

| 方法 | 参数 | 返回与规则 |
|---|---|---|
| `level.config` | `{}` | 返回 `LevelDefinition.scriptConfig` 中的 JSON 对象。关卡未声明 `script.config` 时为 `missing_config`。 |
| `flow.prepare` | `{}` | 读取并校验脚本配置，准备 C# 阶段服务，返回 `flow.status`。要求 Lua 已启用且 `legacy.enabled:false`。同一 session 只能有一个 client 成功准备；已有拥有者时返回 `flow_owned`。 |
| `flow.stage` | `{index:int}` | 请求运行指定的零基阶段序号，范围为 `0..stages.Length-1`；返回 `flow.status`。调用 client 必须拥有流程，否则为 `flow_not_owned`。 |
| `flow.status` | `{}` | 返回 `{running,busy,stageIndex,completedStage,stageId,error,asked,correct,passed,paused}`。`busy` 或 `paused` 为 true 时不应推进下一阶段。 |
| `flow.finish` | `{}` | 仅当最后一个阶段已经完成且流程不忙时成功，完成 C# 的流程收尾并返回 `flow.status`。 |

流程所有权按 API client 隔离，不按 Lua 文件名共享。拥有 client 的 `api:dispose()`、Lua `stop`、VM 故障、session 停止或场景释放都会取消该 client 当前的流程并释放所有权；其他 client 随后可再次 `flow.prepare`。

### 火焰、交互和通用能力

| C# method / Lua 封装 | 参数 | 返回 |
|---|---|---|
| `fire.get` / `fire(entity)` | `{entity}` | `{state,hasEffect}`；state 仅为 `None`、`SmokeOnly`、`Small`、`Medium`、`Large`。|
| `fire.set` / `set_fire(entity,state)` | `{entity,state}` | `{}`；state 大小写必须完全匹配。|
| `fire.freeze` / `freeze_fire(entity,frozen)` | `{entity,frozen:boolean}` | `{}`。|
| `fire.visual` / `set_fire_visual(entity,intensity,scale,smoke)` | `{entity,intensity:0..1,scale:0..100,smoke:0..1}` | `{}`。|
| `interaction.prompt` / `interaction_prompt(entity)` | `{entity}` | `{available:boolean,prompt:string,reason:string}`；不可用也返回成功信封。|
| `interaction.invoke` / `interact(entity,requestId)` | `{entity,requestId:string}` | `{accepted:true,requestId}`；每 client 的 requestId 只能使用一次，历史上限 1024。|
| `door.get` / `door(entity)` | `{entity}` | `{open:boolean,animating:boolean}`。|
| `door.set` / `set_door(entity,open)` | `{entity,open:boolean}` | `{accepted:true}`。|
| `pickup.get` / `pickup(entity)` | `{entity}` | `{held:boolean}`。|
| `pickup.drop` / `drop(entity,toss)` | `{entity,toss?:boolean}`，默认 `false` | `{accepted:true}`。|
| `seat.get` / `seat(entity)` | `{entity}` | `{occupied:boolean,transitioning:boolean}`。|
| `seat.leave` / `leave_seat(entity)` | `{entity}` | `{accepted:true}`。|
| `visibility.get` / `visibility(entity)` | `{entity}` | `{visible:boolean}`。|
| `visibility.set` / `set_visibility(entity,visible)` | `{entity,visible:boolean}` | `{}`。|

`interaction.invoke`、`door.set`、`pickup.drop`、`seat.leave` 都可能因玩家角色、能力、控制租约或组件状态被拒绝。`accepted:true` 只代表请求已提交给 controller；不要据此认定动画或交互结果已结束。

### 计时、控制、事件和引导

| C# method / Lua 封装 | 参数 | 返回 |
|---|---|---|
| `timer.start` / `start_timer(seconds,domain,payload)` | `{seconds:0..86400,domain?:"simulation"|"presentation",payload?:object}`，默认 `simulation` | `{timer:string}`；每 client 最多 128 个活动计时器。|
| `timer.cancel` / `cancel_timer(timer)` | `{timer:string}` | `{}`；已到期、取消或其他 client 的 token 为 `unknown_timer`。|
| `control.acquire` / `acquire_controls(masks)` | `{masks:["Simulation"|"Movement"|"Look"|"Interaction"|"ToolUse",...]}`，1–5 项 | `{lease:string}`；每 client 最多 128 个租约。|
| `control.release` / `release_controls(lease)` | `{lease:string}` | `{}`；仅创建 client 可释放。|
| `events.subscribe` / `on(topic,callback)` | `{topic:string}` | `{subscription:string}`；每 client 最多 64 个订阅。|
| `events.poll` / `update()` 内部调用 | `{subscription:string,max?:1..64}`，默认 64 | `{events:[event...],dropped:int}`。|
| `events.unsubscribe` / `off(subscription)` | `{subscription:string}` | `{}`。|
| `events.emit` / `emit(topic,payload)` | `{topic:"script." 开头且后续非空,payload?:object}` | `{}`；原生事实主题保留，其他 topic 为 `reserved_topic`。|
| `guidance.start` / `start_guidance(entity,slot,label)` | `{entity,slot?:string,label?:string}`，slot 默认 `origin`，label 默认空字符串 | `{operation:string}`；场景需有 `EntityGuidanceService`，同时只能有一个活动 route。|
| `guidance.cancel` / `cancel_guidance()` | `{}` | `{}`；仅可取消该 client 当前活动 route。|
| `client.dispose` / `dispose()` | `{}` | `{}`；取消订阅、timer、租约和自有 guidance route。|

`simulation` timer 会在 `Simulation` 控制租约阻塞时暂停；`presentation` timer 仍使用 presentation 时钟。计时器到期的 `timer.elapsed` 仅投递给创建它的 client。

## 事件主题与 payload

事件表固定为 `{topic:string,sequence:number,payload:object}`。`sequence` 是 `NamedEventBus` 的 session 内递增序号。无通配订阅；每次只能订阅一个完全匹配主题。

| 主题 | payload |
|---|---|
| `entity.registered`、`entity.unregistered` | `{entity,id}`。|
| `entity.active_changed` | `{entity,id,activeSelf,activeInHierarchy}`。|
| `fire.state_changed`、`fire.level_lowered` | `{entity,id,state}`。|
| `fire.reignited`、`fire.extinguished` | `{entity,id}`；来自 `FireStateModel` 原生事件。|
| `door.state_changed` | `{entity,id,open,animating}`。|
| `pickup.state_changed` | `{entity,id,held}`。|
| `seat.state_changed` | `{entity,id,occupied,transitioning}`。|
| `visibility.changed` | `{entity,id,visible}`。|
| `interaction.accepted`、`interaction.rejected` | `{entity,id,requestId,reason,clientId}`。|
| `timer.elapsed` | `{timer,clientId,payload}`；投递时按 clientId 隔离。|
| `guidance.finished` | `{operation,outcome,clientId}`。|
| `script.*` | 脚本 `emit` 的原样 `payload`，必须是对象。|

订阅**没有初始状态快照，也不重放已派发的历史**。总线按派发时的订阅列表投递，因此新订阅可能收到订阅前已经入队、尚未派发的事件。`ComponentEventRelay` 初次观察实体时只建立比较基线，之后才发布变化。Lua 初始化应先经 `fire.get`、`door.get`、`pickup.get`、`seat.get`、`visibility.get`、`entity.active.get` 等 getter 取得当前值；门、拾取、座位、可见性和激活事件来自 `Scripts.Tick()` 采样；同一次采样之间的多次变化可能合并，只能代表采样时状态，不能当作完整动作日志。火焰的 `state_changed`、`level_lowered`、`reignited`、`extinguished` 是原生 `FireStateModel` 的离散事实事件，适合记录发生过程，不能代替初始化和状态纠偏 polling。

## 队列、背压与回调规则

`NamedEventBus` 的 session 队列容量为 1024。每次 session tick 最多 `Dispatch(256)` 条，并且只派发进入本次 dispatch 时已排队的那一批；回调中 publish 的事件留到下一次 dispatch。嵌套 dispatch 不工作。队列满时新 publish 被拒绝；C# 门面把该拒绝返回为 `operation_rejected`，原生组件 relay 会忽略发布失败。

从总线送入 Lua 前，每个 subscription inbox 最多 256 条；同一 client 所有 inbox 合计 payload UTF-8 字节最多 1 MiB。超出任一限制时，**新到事件被丢弃**，对应 inbox 的 `dropped` 加一；`events.poll` 返回并清零该 inbox 的 `dropped`。单条总线 payload 上限为 64 KiB。Lua `client:update()` 的每项订阅每帧最多 poll 64 条，并把溢出汇总为 `update()` 成功结果的 `data.dropped`。

单个订阅内维持 FIFO；Lua 按订阅分别轮询，跨订阅回调没有全局顺序。组件状态命令（例如 fire.freeze、visibility.set）不会在 client.dispose 时自动撤销；需恢复时显式设置。自动释放的只有订阅、timer、控制租约和自有 route。

Lua callback 异常会写入 `update().data.callbackErrors`，不阻塞同批其他 callback。回调内 `off` 或 `dispose` 后，本批尚未触发的对应 callback 不再调用。禁止从 callback 重入 `update()`，会得到 `client.reentrant_update`。

## 关卡入口

不要再编写 C# host 注入、文件加载或自建 VM 的示例代码。实际可运行的入口是 A/B 包的 [main.lua](../../HMP_game/Assets/Levels/third_scene_shared_modules_a/Scripts/main.lua)：它使用唯一允许的 `require("component_api")`，由运行时自动收到 `host` 和 `json`，并实现 `new`、`update`、`stop`。通用火焰 API 用法可参考 [fire_level.lua](../../HMP_game/Assets/StreamingAssets/Lua/examples/fire_level.lua)，但它不是自动加载入口。

模块应在 `stop` 中调用 `api:dispose()`；不要把释放函数命名为 `dispose` 来代替 module 的 `stop`，否则 VM 会拒绝启动。事件回调中自行过滤实体 token，且 `update` 应检查 `callbackErrors` 和 `dropped`。

## 历史验证记录

2026-09-23，在独立的 Unity 6000.6.0f1 验证工程中完成，未操作用户正在打开的编辑器：

| 检查 | 结果与记录 |
|---|---|
| EditMode | 45/45 通过；包括命名总线 FIFO、延迟派发、重入、配额和派发中取消。[XML](验证记录/Lua接口/edit-tests.xml) |
| PlayMode | 4/4 通过。[XML](验证记录/Lua接口/play-tests.xml) |
| Lua API 场景接口 | PASS：JSON 调用、火焰/门事件、交互去重、自定义事件、timer/lease 归属、取消、scope 重建旧 token 拒绝。[记录](验证记录/Lua接口/api-integration.txt) |
| 通用模块回归 | PASS：双火焰、门/拾取/座位、火焰物理、暂停、十次 scope 重建及场景导航。[记录](验证记录/Lua接口/shared-regression.txt) |
| 办公室完整回归 | PASS：三个正确步骤、错误交互、超时、教学步骤、十次真实重启与停止释放。[记录](验证记录/Lua接口/office-regression.txt) |
| Lua 包装契约 | PASS：临时 Python 3.11 + lupa 2.8（Lua 5.5），mock host 验证 JSON、事件回调、异常隔离、取消订阅、重入拒绝、溢出报告与释放。[记录](验证记录/Lua接口/lua-wrapper.txt) |

这组记录是运行库接入前的 API/包装契约历史结果。Unity 菜单入口为 `Tools/Scripting/Check Lua Component API`。命令行可在独立验证副本运行 `Unity.exe -batchmode -projectPath . -executeMethod LuaApiIntegrationChecks.BeginBatch -logFile <日志路径>`；报告输出 `Library/LuaApiIntegrationChecks.txt`。该检查实际使用 C# 字符串接口和 Unity 场景，不执行 Lua VM。

项目显式依赖已有版本 `com.unity.nuget.newtonsoft-json` 3.2.2。临时 lupa 仅用于历史包装测试，没有作为 Unity 依赖安装。MoonSharp 接入后的验证状态、平台限制与安全边界见 [Lua运行库接入与验证](Lua运行库接入与验证.md)。
