"""Structural and deformation QA, then export only the two skinned arm meshes."""
import bpy,bmesh,json,math
import numpy as np
from pathlib import Path
from mathutils import Matrix,Vector
ROOT=Path('C:/Users/Administrator/Desktop/hmp/hmp_game/模型/第一人称双手')
scene=bpy.context.scene
rig=bpy.data.objects['RIG_FPHands_01']
meshes=[bpy.data.objects['SK_FPArm_'+s+'_01'] for s in ('L','R')]
report={'blender_version':bpy.app.version_string,'units':'meters','bones':len(rig.data.bones),
        'deform_bones':sum(b.use_deform for b in rig.data.bones),'meshes':[],'finger_deformation':[]}
for mesh in meshes:
    bm=bmesh.new();bm.from_mesh(mesh.data)
    metrics={'name':mesh.name,'vertices':len(bm.verts),'triangles':sum(len(f.verts)-2 for f in bm.faces),
             'boundary_edges':sum(e.is_boundary for e in bm.edges),'nonmanifold_edges':sum(not e.is_manifold for e in bm.edges),
             'loose_vertices':sum(not v.link_faces for v in bm.verts),'volume_m3':bm.calc_volume(signed=True),
             'dimensions_m':list(mesh.dimensions),'uv_layers':[u.name for u in mesh.data.uv_layers]}
    bm.free()
    metrics['max_influences']=max(sum(g.weight>1e-6 for g in v.groups) for v in mesh.data.vertices)
    metrics['max_weight_sum_error']=max(abs(sum(g.weight for g in v.groups)-1) for v in mesh.data.vertices)
    metrics['missing_weight_bones']=list({mesh.vertex_groups[g.group].name for v in mesh.data.vertices for g in v.groups if mesh.vertex_groups[g.group].name not in rig.data.bones})
    metrics['uv_range']=[min(c for d in mesh.data.uv_layers[0].data for c in d.uv),max(c for d in mesh.data.uv_layers[0].data for c in d.uv)]
    assert metrics['boundary_edges']==metrics['nonmanifold_edges']==metrics['loose_vertices']==0,metrics
    assert metrics['volume_m3']>0,metrics
    assert metrics['max_influences']<=4 and metrics['max_weight_sum_error']<.00001,metrics
    assert not metrics['missing_weight_bones'],metrics
    assert metrics['uv_range'][0]>=0 and metrics['uv_range'][1]<=1,metrics
    metrics['transform_identity_error']=max(abs(mesh.matrix_world[r][c]-(1.0 if r==c else 0.0)) for r in range(4) for c in range(4))
    assert metrics['transform_identity_error']<1e-6
    report['meshes'].append(metrics)

def positions(mesh):
    obj=mesh.evaluated_get(bpy.context.evaluated_depsgraph_get())
    return np.array([v.co[:] for v in obj.data.vertices],dtype=np.float32)
base=[positions(m) for m in meshes]
for side_index,side in enumerate(('L','R')):
    for finger in ('Thumb','Index','Middle','Ring','Little'):
        bone=rig.pose.bones[finger+'.01.'+side]
        bone.rotation_euler.x=math.radians(-12)
        bpy.context.view_layer.update()
        moved=float(np.max(np.linalg.norm(positions(meshes[side_index])-base[side_index],axis=1)))
        opposite=float(np.max(np.linalg.norm(positions(meshes[1-side_index])-base[1-side_index],axis=1)))
        assert moved>.001 and opposite<1e-7,(finger,side,moved,opposite)
        report['finger_deformation'].append({'finger':finger+'.'+side,'max_motion_m':moved,'opposite_arm_motion_m':opposite})
        bone.rotation_euler.x=0
        bpy.context.view_layer.update()

# Moderate grasp pose to visually inspect the phalanges and wrist connection.
for side in ('L','R'):
    for finger in ('Index','Middle','Ring','Little'):
        for j,angle in ((1,-25),(2,-40),(3,-22)):
            rig.pose.bones[f'{finger}.{j:02d}.{side}'].rotation_euler.x=math.radians(angle)
    rig.pose.bones['Thumb.01.'+side].rotation_euler.x=math.radians(-12)
    rig.pose.bones['Thumb.02.'+side].rotation_euler.x=math.radians(-15)
bpy.context.view_layer.update()
scene.camera=bpy.data.objects['CAM_FirstPerson_60deg']
scene.render.filepath=str(ROOT/'Previews'/'FPHands_GripCheck.png')
bpy.ops.render.render(write_still=True)
for bone in rig.pose.bones:bone.matrix_basis=Matrix.Identity(4)
bpy.context.view_layer.update()
scene.frame_set(1)
scene.render.filepath=str(ROOT/'Previews'/'FPHands_FirstPerson.png')
report['total_triangles']=sum(m['triangles'] for m in report['meshes'])
report['forearm_bone_lengths_m']={s:rig.data.bones['Forearm.'+s].length for s in ('L','R')}
report['hand_wrist_to_middle_tip_m']={s:(rig.data.bones['Middle.03.'+s].tail_local-rig.data.bones['Wrist.'+s].head_local).length for s in ('L','R')}
report['texture_sizes']={n.image.name:list(n.image.size) for n in meshes[0].active_material.node_tree.nodes if n.type=='TEX_IMAGE'}
report['export']={'format':'FBX','mesh_count':2,'armature_count':1,
    'axis_forward':'-Z','axis_up':'Y','temporary_basis_rotation_z_degrees':180,
    'unity_forward':'+Z','unity_left_x':'negative','meters_per_unit':1,
    'leaf_bones':False,'animation_clips':False,'textures':'external relative paths'}
report['passed']=True
(ROOT/'QA').mkdir(exist_ok=True)
(ROOT/'QA'/'blender_validation.json').write_text(json.dumps(report,indent=2),encoding='utf-8')

for node in meshes[0].active_material.node_tree.nodes:
    if node.type=='TEX_IMAGE':
        node.image.filepath='//Textures/T_FPHands_01_'+node.name.removeprefix('TEX_')+'.png'
        if not node.image.packed_file:node.image.pack()
bpy.ops.object.select_all(action='DESELECT')
for o in [rig]+meshes:o.select_set(True)
bpy.context.view_layer.objects.active=rig
fbx=ROOT/'SK_FPHands_01.fbx'
# Use absolute image paths until the blend has a base directory.
for node in meshes[0].active_material.node_tree.nodes:
    if node.type=='TEX_IMAGE':node.image.filepath=str(ROOT/'Textures'/('T_FPHands_01_'+node.name.removeprefix('TEX_')+'.png'))
exec((ROOT/'Scripts'/'export_unity_fbx.py').read_text(encoding='utf-8'),globals())
export_unity_fbx(ROOT,rig,meshes)
for node in meshes[0].active_material.node_tree.nodes:
    if node.type=='TEX_IMAGE':node.image.filepath='//Textures/T_FPHands_01_'+node.name.removeprefix('TEX_')+'.png'
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active
            space.overlay.show_overlays=False
            space.shading.type='MATERIAL'
            space.region_3d.view_perspective='CAMERA'
            space.region_3d.view_camera_zoom=0
scene.camera=bpy.data.objects['CAM_FirstPerson_60deg']
text=bpy.data.texts.get('FPHands_README') or bpy.data.texts.new('FPHands_README')
text.clear();text.write('First-person hands with continuous forearms.\nOnly FPHands_Asset is exported.\n37 bones, 18616 triangles, shared 2048 PBR atlas.\nMeters; +Y forward and +Z up in Blender.\nSee README.md and asset source register beside this file.\n')
bpy.ops.wm.save_as_mainfile(filepath=str(ROOT/'SK_FPHands_01.blend'),compress=True)
print(json.dumps(report,ensure_ascii=False))
