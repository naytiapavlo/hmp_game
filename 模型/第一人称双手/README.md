# 第一人称双手与小臂

写实裸手，包含左右手掌、十根手指，以及连续连接到肘部附近的小臂。默认摆在第一人称相机画面下方，中央留出操作视野。基于公开的 Hafnia Hands 手部资产改造，新增小臂并重建骨架；完整来源及许可见 [资产来源登记](资产来源登记.md)。

## 交付文件

| 文件 | 用途 |
| --- | --- |
| `SK_FPHands_01.blend` | Blender 5.2 源文件，贴图已打包，含第一人称和全貌预览相机 |
| `SK_FPHands_01.fbx` | 两个蒙皮网格、一个骨架，配套外部 `Textures/` 目录 |
| `FPHands_URP.unitypackage` | 已设置贴图导入参数、URP Lit 材质和 Prefab，可直接导入 Unity |
| `Previews/FPHands_FirstPerson.png` | Blender 第一人称预览 |
| `Previews/FPHands_Detail.png` | 双手和整段小臂全貌 |
| `Previews/FPHands_GripCheck.png` | 手指弯曲检查图 |
| `Previews/FPHands_UnityURP.png` | Unity URP 实际导入渲染 |
| `QA/` | Blender 结构、权重检查和 Unity 导入验证结果 |
| `Sources/`、`Licenses/` | 上游原文件、固定版本证据和许可原文 |

## 规格

| 项目 | 数值 |
| --- | --- |
| 单位 | 1 单位 = 1 米 |
| 网格 | `SK_FPArm_L_01`、`SK_FPArm_R_01` |
| 顶点 | 共 9,312；导入引擎后可能因 UV 或法线分裂增加 |
| 三角面 | 共 18,616；左 9,312，右 9,304 |
| 骨架 | `RIG_FPHands_01`，37 根骨骼，其中 36 根参与变形 |
| 权重 | 每顶点最多 4 根骨骼，已归一化 |
| 尺寸 | 腕部到中指尖约 18.3 cm，小臂骨长约 25.7 cm |
| 材质与 UV | 1 个共享皮肤材质，1 套 UV，无越界坐标 |
| 贴图 | 5 张 2048 × 2048 PNG |

`Root` 控制整体；`Forearm.L/R` 和 `Wrist.L/R` 控制小臂与手腕；`Thumb/Index/Middle/Ring/Little.01–03.L/R` 控制指节。保留了小指掌骨。骨架适合后续制作抓握、操作器材等动画；本次交付默认姿态和蒙皮，不含成套动作动画。

## Unity 使用

1. 在使用 URP 的工程中导入 `FPHands_URP.unitypackage`。
2. 将 `Assets/FPHands/FPHands_01.prefab` 放到第一人称相机下面，局部 Position 和 Rotation 都设为 `(0, 0, 0)`，Scale 设为 `(1, 1, 1)`。
3. 预览使用垂直 FOV 60°、Near Clip 0.01 m。默认 0.3 m 的近裁剪面会切到靠近相机的小臂；根据游戏场景调整。相机的世界高度由现有角色控制器负责。
4. 骨架采用 Generic，关闭 Import Animation；需要直接访问手指骨骼时保留未优化的层级。Prefab 已使用四骨骼蒙皮和配好的 URP Lit 材质。

FBX 已做坐标基底转换，导入 Unity 后前方为 `+Z`、左手位于负 X；挂载时不需要额外旋转 180°。Blender 源文件继续使用 `+Y` 向前、`+Z` 向上的制作坐标。

如果只导入 FBX，需一并复制 `Textures/` 并手工建立材质：

| 贴图后缀 | 设置 |
| --- | --- |
| `BaseColor` | Base Map，sRGB 开启，Tint 白色 |
| `Normal` | Normal Map 类型，强度 1 |
| `AO` | Occlusion，sRGB 关闭；预览强度 0.65 |
| `MetallicSmoothness` | Metallic Map，sRGB 关闭，Smoothness 1；R=0，A=1−Roughness |
| `Roughness` | 独立粗糙度备份，sRGB 关闭；URP 已使用上方打包版本 |

`Preview_Only` 中的两台相机和灯光仅用于 Blender 预览，没有导出到 FBX 或 Prefab。资源包不包含项目的场景、角色控制器或渲染管线设置。

## 检查与重建

已通过 Blender 5.2 的闭合流形、法线方向、UV 范围、权重和十根手指独立变形检查。在独立 Unity 6000.6.0f1 / URP 17.6.0 工程内验证了左右方向、米制尺寸、18,616 三角面、37 根骨骼、手指蒙皮和真实材质渲染。

修改现有资产直接打开 `.blend`。完整重建时，在单独的空 Blender 文件里执行 `Scripts/build_fp_hands.py`，再执行 `Scripts/validate_export.py`；前者会清空当前场景。若移动目录，先修改两个入口脚本的 `ROOT`。其余 Python 文件是几何、烘焙和 FBX 导出的辅助模块。`Scripts/HandsQA.cs` 保存本次 Unity 回测与打包代码，供独立验证工程使用。

再次分发时保留 `Licenses/` 两份许可原文及来源说明。Unity 资源包已包含这些文本。成品文件的大小和 SHA-256 见 `QA/delivery_manifest.json`。
