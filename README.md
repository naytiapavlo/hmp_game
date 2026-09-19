# 初期火灾预防与应急处置 3D 消防实训系统

基于 Unity 6（URP）开发的第一人称 3D 消防实训项目，本仓库包含 Unity 工程、项目文档规范与美术源模型。

## 仓库结构

| 目录 | 说明 |
|---|---|
| `HMP_game/` | Unity 工程主目录 |
| `文档规范 Agent必读/` | 项目计划书、建模规范、美术版权要求、Agent 工作规范 |
| `模型/` | 美术源模型（Blender 源文件，经 Git LFS 管理） |

## 环境要求

- **Unity 6000.6.0f1**（通用渲染管线 URP + Input System），通过 Unity Hub 打开 `HMP_game/` 目录
- **Git LFS**：首次克隆前请确认已安装并初始化
  ```bash
  git lfs install
  ```
- `*.csproj`、`*.slnx` 等由 Unity 自动生成，不入库，也无需手动管理

## 快速开始

```bash
git clone https://github.com/naytiapavlo/hmp_game.git
cd hmp_game
# 用 Unity Hub 打开 HMP_game/（版本须为 6000.6.0f1）
```

## 大文件说明（Git LFS）

`*.blend`、`*.fbx`、`*.psd` 已在 `.gitattributes` 中登记为 LFS 管理。今后新增大体积美术资源时请勿直接提交二进制文件，确认扩展名已被 LFS 规则覆盖，避免仓库膨胀。

## 相关文档

- [项目计划书（简版）](./文档规范%20Agent必读/初期火灾预防与应急处置3D消防实训系统项目计划书（简版）.md)
- [消防实训3D项目建模规范（简版）](./文档规范%20Agent必读/消防实训3D项目建模规范（简版）.md)
- [项目美术资源版权要求](./文档规范%20Agent必读/项目美术资源版权要求.md)
- [Agent 工作规范](./文档规范%20Agent必读/消防实训项目%20Agent%20工作规范.md)
- [建模规范（模型侧必读）](./模型/消防实训3D项目建模规范Agent必读！.md)
