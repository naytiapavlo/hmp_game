# Lua 运行库接入与验证

## 当前实现

项目已 vendor [MoonSharp](https://www.moonsharp.org/) v2.0.0.0，来源提交 `3154416e535ab96c0d52bf12e3e472985a1532f4`，代码与 BSD-3-Clause 许可证位于 `HMP_game/Assets/Plugins/MoonSharp/`。每个启用 Lua 的 `LevelSessionHost` 创建一个 `LevelLuaRunner` 和一个 `LevelLuaVm`；VM 通过 `LuaComponentApi` 访问关卡能力，既不注册 Unity/CLR userdata，也不公开反射对象。

运行时不从 `Assets` 或 StreamingAssets 读源码。`Tools/Levels/Refresh Runtime Definitions` 将包入口写入 `LevelDefinition.luaSource`，将 `Assets/StreamingAssets/Lua/component_api.lua` 写入 `LevelDefinition.luaApiSource`。包校验会对两份缓存做一致性比对；启用脚本时还会做 MoonSharp 语法编译。缓存过期、缺失或语法错误都会被 `LevelPackageBuildGuard` 阻止构建。

## 生命周期与入口契约

`LevelBootstrapper.PrepareLevel` 在场景 bindings、scope、火焰初始状态和 session 都成功后调用 `LevelSessionHost.TryStartLua`。当 `script.enabled` 为 false 时不会创建 VM。启用时，运行时按以下次序工作：

1. 执行共享 `component_api.lua`，它必须返回 table。
2. 创建受限 host 与 JSON table，并执行关卡入口；入口必须返回 module table。
3. 调用 module 的 `new(host,json)`；必须返回 instance table。
4. 每帧由 `LevelSessionHost.LateUpdate` 调用 `update(self,delta,unscaled)`。
5. 已成功创建 instance 后，session 停止、场景释放或运行中脚本失败时至多调用一次 `stop(self)`，随后释放 API client。若启动在 instance 创建前失败，不能保证会调用 `stop`，但已创建的 API client 仍会释放。

host 只有 `host:Call(method,argsJson)`。JSON table 提供 `encode`、`decode`、`array` 和 `null`；整数、`null`、空数组和对象按 `LevelLuaVm` 的 JSON 规则处理。唯一可 require 的模块名是 `component_api`；其他 require、文件加载和动态模块解析均不可用。关卡代码不用也不能自行创建 host、json、VM 或 API client。

## 限制与安全边界

每个顶层 chunk、`new`、`update` 和 `stop` 都以协程运行，单次上限 100,000 条指令。脚本主动 yield 或超过预算会被标记为失败，不能在下一帧续跑。共享 API 与入口源码各不超过 1 MiB UTF-8；JSON 请求编码最大 64 KiB / 深度 16；响应解码最大 2 MiB / 深度 32，以容纳批量事件。所有调用必须发生在 Unity 主线程。

这是 MoonSharp 的 Lua 5.2 语言实现，不是 LuaJIT，也不宣称完整支持 Lua 5.3 或 5.4。硬沙箱减少了可见 API，但它不是针对恶意代码的强安全边界：内存压力和 Lua 原生字符串运算仍可能被滥用。不要把不受信任的用户脚本当作安全输入。

## 已启用包与事件

`third_scene_shared_modules_a` 和 `third_scene_shared_modules_b` 已设为 `script.enabled:true`。二者入口会发布 `script.level.started`，监听 `fire.state_changed` 后发布 `script.level.fire_changed`，并在 presentation timer 到期时发布 `script.level.ready`。

办公室 `level_1_initial_fire` 也设为 `script.enabled:true`，并通过 `script.config: "Config/flow.json"` 把 11 个阶段交给其包内 Lua 编排。Lua 调用 flow API 请求下一阶段；`LevelFlowRunner` 继续在 C# 中执行每段的表现、判题计分、教官逻辑和结算。它不是把办公室关卡改写为纯 Lua。

## 使用方式

1. 在关卡 `Config/level.json` 中设置 `"script": { "enabled": true, "entry": "Scripts/main.lua" }`；需要结构化脚本数据时增加 `"config": "Config/<name>.json"`。
2. 参考 A/B 包的 `Scripts/main.lua` 编写 `new`、`update`、`stop`。`update` 中调用 `self.api:update()` 才会分发该 client 收到的事件。
3. 执行 `Tools/Levels/Refresh Runtime Definitions`，再执行 `Tools/Levels/Validate Packages`。
4. 打开对应包的 Scenes 场景进入 Play；后续每次修改脚本都要刷新 Runtime 缓存。当前不提供运行中热重载。

接口、事件名和参数详见 [Lua组件API与事件总线接入说明](Lua组件API与事件总线接入说明.md)。

## 基础运行库验证状态

2026-09-23，使用 Unity 6000.6.0f1 Windows Editor，在独立验证副本运行，随后同步生成的 Runtime 资产；未关闭用户正在使用的编辑器。

| 检查 | 结果 |
| --- | --- |
| EditMode | 67/67 通过，含真实 Lua 执行、整数/中文/空数组 JSON、大响应、语法错误与各生命周期死循环预算；[XML](验证记录/Lua运行库/editmode.xml) |
| Lua 场景集成 | 通过 A/B 自动启动与逐帧更新、C#→Lua→C# 事件、计时器、控制租约释放、启动/更新/停止异常清理、切关销毁与旧令牌失效；[报告](验证记录/Lua运行库/runtime-integration.txt) |
| 关卡兼容回归 | 通过 A→B→办公室导航、JSON 初始火势、办公室旧配置和题库加载、最终 session 释放；[报告](验证记录/Lua运行库/package-regression.txt) |
| 编辑器与构建前校验 | 通过重复创建幂等、保留已打开场景、拒绝 Lua 缓存过期、场景缺失及启用脚本语法错误；[报告](验证记录/Lua运行库/authoring.txt) |

Lua 场景检查期间曾出现 UnityEditor.Search 启动索引异常（调用栈仅在 Unity 编辑器搜索模块），检查仍完成并输出 PASS。上述结果是第一关 Lua 阶段迁移前的运行库基础验证，不能替代迁移后的完整答题、计分、教官、结算和菜单入口验证；待执行项目见 [初起火灾第一关迁移与验证](初起火灾第一关迁移与验证.md)。移动端、WebGL、IL2CPP/AOT 和独立 Player 均未实测。

第一关已完成 Lua 编排迁移及真实菜单、成功/失败结算、重开和退出验证；当前结果见 [初起火灾第一关迁移与验证](初起火灾第一关迁移与验证.md)。
