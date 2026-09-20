"""把 16 个起火点道具 FBX 重新导出为「内嵌贴图」版本。

背景
----
仓库里原有的单件 FBX 由旧工程管线 `IgnitionProps/Source/finalize_props.py` 导出，用的是
`path_mode='RELATIVE'`。由于 FBX 写在 `Exports/Individual/`（比 `Textures/` 低两层），
文件里存的是 `..\\..\\Textures\\T_xxx.png`。FBX 被单独拷进本仓库后该相对路径失效，
导入 Blender 时材质在、贴图全丢（物体渲染成灰白）。

本脚本打开源工程 `IgnitionProps.blend`，按与原管线完全相同的选择/轴向/缩放/平滑设置重新导出，
只改两点：

  1. `path_mode='COPY'` + `embed_textures=True`：贴图直接写进 FBX，文件自包含；
  2. 导出前把资产根 `root.location` 归零。`IgnitionProps.blend` 是画册陈列版——
     finalize_props.py 是「先导出、后把道具摆到陈列网格」，所以直接导出这个已保存的
     blend 会把陈列位移烘进资产，破坏「资产根位于底部原点」的交付规则。

注意：内嵌的只有常态（Normal）贴图，即导出时材质真正引用的那 3 张。
Fault / Burnt 状态贴图是 Unity 侧按 `State_Presets.json` 换材质使用的，仍需单独导入
（已随 `模型/办公室场景/Textures/` 一并入库）。

脚本只读源工程，不会保存 .blend。

用法
----
    blender --background --factory-startup --python embed_textures_fbx.py

改路径就改下面两个常量。导出结果需自行校验后再覆盖仓库里的 FBX，
校验方式：把新 FBX 单独放进一个没有 Textures 同级的目录，用
`bpy.ops.import_scene.fbx(..., use_image_search=False)` 导入，能出贴图即为内嵌成功，
再比对三角面、材质槽与包围盒。
"""
import bpy
import json
from pathlib import Path

# 源工程（旧工程管线目录）
SRC_BLEND = Path(r"C:\Users\Administrator\Desktop\hmp\game\模型\起火点\IgnitionProps\IgnitionProps.blend")
# 导出目标目录
OUT_DIR = Path(r"C:\Users\Administrator\Desktop\hmp\hmp_game\模型\办公室场景")

bpy.ops.wm.open_mainfile(filepath=str(SRC_BLEND))
scene = bpy.context.scene

# 道具根：名为 SM_* 且自定义属性 asset_id 等于自身名字的空物体
roots = [o for o in scene.objects if o.type == 'EMPTY' and o.get('asset_id') == o.name]
roots.sort(key=lambda o: o.name)
print("@@@ROOTS %d" % len(roots), flush=True)

report = []
for root in roots:
    meshes = [o for o in root.children_recursive if o.type == 'MESH']
    catalogue_location = list(root.location)

    # 交付规则：资产根位于底部原点，Scale = 1、Rotation = 0
    root.location = (0.0, 0.0, 0.0)
    bpy.context.view_layer.update()

    bpy.ops.object.select_all(action='DESELECT')
    root.select_set(True)
    for child in root.children_recursive:
        if child.type == 'EMPTY':
            child.select_set(True)
    for o in meshes:
        o.hide_set(False)
        o.select_set(True)
    bpy.context.view_layer.objects.active = root

    out = OUT_DIR / (root.name + ".fbx")
    bpy.ops.export_scene.fbx(
        filepath=str(out),
        use_selection=True,
        object_types={'EMPTY', 'MESH'},
        use_mesh_modifiers=False,
        mesh_smooth_type='OFF',
        use_tspace=True,
        add_leaf_bones=False,
        bake_anim=False,
        axis_forward='-Z',
        axis_up='Y',
        apply_unit_scale=True,
        apply_scale_options='FBX_SCALE_UNITS',
        use_custom_props=True,
        path_mode='COPY',
        embed_textures=True,
    )

    tris = 0
    for o in meshes:
        o.data.calc_loop_triangles()
        tris += len(o.data.loop_triangles)
    report.append({
        "asset": root.name,
        "meshes": len(meshes),
        "triangles": tris,
        "bytes": out.stat().st_size,
        "catalogue_location_reset": [round(v, 4) for v in catalogue_location],
    })
    print("@@@EXPORTED %s %d bytes" % (root.name, out.stat().st_size), flush=True)

print("@@@SUMMARY " + json.dumps({"count": len(report),
                                  "total_bytes": sum(r["bytes"] for r in report),
                                  "assets": report}, ensure_ascii=False), flush=True)
