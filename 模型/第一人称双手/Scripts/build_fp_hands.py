"""Build the licensed first-person hand asset in Blender; no external add-ons.

Execute this file using Blender MCP execute_code, or Blender's text editor.
Sources and licenses are retained in the sibling folders. This script replaces
only the FPHands_Asset and Preview_Only collections created by this asset.
"""
import bpy
import bmesh
import math
import json
import numpy as np
from pathlib import Path
from mathutils import Matrix, Vector

ROOT = Path('C:/Users/Administrator/Desktop/hmp/hmp_game/模型/第一人称双手')
exec((ROOT/'Scripts'/'forearm_geometry.py').read_text(encoding='utf-8'),globals())
SCENE = bpy.context.scene
SCENE.name = 'SCN_FPHands_01'
if bpy.context.object and bpy.context.object.mode != 'OBJECT':
    bpy.ops.object.mode_set(mode='OBJECT')

for collection_name in ('FPHands_Asset', 'Preview_Only'):
    c = bpy.data.collections.get(collection_name)
    if c:
        for o in list(c.objects):
            bpy.data.objects.remove(o, do_unlink=True)
        bpy.data.collections.remove(c)

# Recover only intermediate imports left by an interrupted prior run.
for o in list(SCENE.objects):
    if o.get('_fp_hands_staging',False):
        bpy.data.objects.remove(o,do_unlink=True)

for blocks in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials, bpy.data.images):
    for block in list(blocks):
        if block.users == 0 and any(s in block.name for s in ('FPHand', 'FPArm', 'FirstPerson')):
            blocks.remove(block)

ASSET = bpy.data.collections.new('FPHands_Asset')
PREVIEW = bpy.data.collections.new('Preview_Only')
SCENE.collection.children.link(ASSET)
SCENE.collection.children.link(PREVIEW)
SCENE.unit_settings.system = 'METRIC'
SCENE.unit_settings.scale_length = 1.0
SCENE.render.fps = 30

def relocate(o, collection):
    for c in list(o.users_collection):
        c.objects.unlink(o)
    collection.objects.link(o)

def texture(kind, source):
    name = 'T_FPHands_Skin_' + kind
    im = bpy.data.images.load(str(ROOT / 'Sources' / source), check_existing=False)
    im.name = name
    if kind != 'BaseColor':
        im.colorspace_settings.name = 'Non-Color'
    assert tuple(im.size) == (2048, 2048)
    # Force decoding before changing a lazily loaded image's path.
    assert len(im.pixels) == 2048 * 2048 * 4
    im.pack()
    im.file_format = 'PNG'
    im.filepath_raw = str(ROOT / 'Textures' / (name + '.png'))
    im.save()
    return im

images = {k: texture(k, f) for k, f in {
    'BaseColor': 'Hand_BaseColor.jpg', 'Normal': 'Hand_Normal.jpg',
    'Roughness': 'Hand_Roughness.jpg', 'AO': 'Hand_AO.jpg'
}.items()}

# URP Lit expects metallic in R and smoothness in A. Preserve linear values.
rough = np.empty(2048 * 2048 * 4, dtype=np.float32)
images['Roughness'].pixels.foreach_get(rough)
packed = np.zeros((2048 * 2048, 4), dtype=np.float32)
packed[:, 3] = 1.0 - rough.reshape(-1, 4)[:, 0]
ms = bpy.data.images.new('T_FPHands_Skin_MetallicSmoothness', width=2048, height=2048, alpha=True)
ms.colorspace_settings.name = 'Non-Color'
ms.pixels.foreach_set(packed.ravel())
ms.filepath_raw = str(ROOT / 'Textures' / 'T_FPHands_Skin_MetallicSmoothness.png')
ms.file_format = 'PNG'
ms.save()
images['MetallicSmoothness'] = ms

mat = bpy.data.materials.new('M_FPHands_Skin_01')
mat.use_nodes = True
n = mat.node_tree.nodes
n.clear()
links = mat.node_tree.links
out = n.new('ShaderNodeOutputMaterial')
out.location = (640, 100)
bs = n.new('ShaderNodeBsdfPrincipled')
bs.location = (300, 100)
bs.inputs['Metallic'].default_value = 0.0
bs.inputs['Specular IOR Level'].default_value = 0.26
bs.inputs['Subsurface Weight'].default_value = 0.04
bs.inputs['Subsurface Radius'].default_value = (1.0, 0.42, 0.22)
bs.inputs['Subsurface Scale'].default_value = 0.005
links.new(bs.outputs['BSDF'], out.inputs['Surface'])
for i, k in enumerate(('BaseColor', 'Roughness', 'Normal', 'AO')):
    t = n.new('ShaderNodeTexImage')
    t.image = images[k]
    t.name = 'TEX_' + k
    t.label = k + ' / 2048'
    t.location = (-600, 450 - i * 280)
    if k == 'BaseColor':
        links.new(t.outputs['Color'], bs.inputs['Base Color'])
    elif k == 'Roughness':
        links.new(t.outputs['Color'], bs.inputs['Roughness'])
    elif k == 'Normal':
        normal = n.new('ShaderNodeNormalMap')
        normal.location = (-100, -120)
        normal.inputs['Strength'].default_value = 0.65
        links.new(t.outputs['Color'], normal.inputs['Color'])
        links.new(normal.outputs['Normal'], bs.inputs['Normal'])
    else:
        t.label = 'AO / supplied separately for Unity Occlusion'
mat['Source'] = 'Hafnia Hands; see asset license register'

rig_data = bpy.data.armatures.new('RIG_FPHands_01')
rig = bpy.data.objects.new('RIG_FPHands_01', rig_data)
ASSET.objects.link(rig)
rig.show_in_front = True
rig.display_type = 'WIRE'
rig['Description'] = 'Two independent wrists and articulated fingers. Meters, +Y forward, +Z up.'
rig['Camera_mount'] = 'Parent to a Unity camera at local position and rotation zero, scale one.'
rig['Skin_license'] = 'MIT'
rig['Mesh_license'] = 'BSD-3-Clause'
rig['upstream_commit'] = 'c9753a9912046d93d4733865ab7784a17c3460c4'

bone_specs = []
meshes = []
skin_samples = []
basis = Matrix(((-.01, 0, 0, 0), (0, 0, .01, 0), (0, .01, 0, 0), (0, 0, 0, 1)))
for side, sign in [('L', -1), ('R', 1)]:
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(ROOT / 'Sources' / f'{side.lower()}_hand_skeletal_lowres.fbx'), use_anim=False, use_custom_normals=False)
    imported = set(bpy.data.objects) - before
    for staging in imported:staging['_fp_hands_staging']=True
    mesh = next(o for o in imported if o.type == 'MESH')
    src_rig = next(o for o in imported if o.type == 'ARMATURE')
    prefix = 'hands:b_' + side.lower() + '_'
    old_bones = src_rig.data.bones
    wrist = old_bones[prefix + 'hand'].head_local.copy()
    turn = Matrix.Rotation(math.radians(sign * 20), 4, 'Z') @ Matrix.Rotation(math.radians(35), 4, 'X')
    orientation = turn @ basis
    wrist_target = Vector((sign * .18, .48, -.18))
    M = Matrix.Translation(wrist_target - orientation @ wrist) @ orientation
    dorsal = (orientation.to_3x3() @ Vector((0, 1, 0))).normalized()
    names = {'hand': 'Wrist.' + side, 'pinky0': 'LittleMetacarpal.' + side}
    fingers = {'thumb': 'Thumb', 'index': 'Index', 'middle': 'Middle', 'ring': 'Ring', 'pinky': 'Little'}
    for f, nice in fingers.items():
        for j in range(1, 4):
            names[f + str(j)] = f'{nice}.{j:02d}.{side}'
    for src_name, name in names.items():
        b = old_bones[prefix + src_name]
        head = M @ b.head_local
        if src_name == 'hand':
            tail = M @ ((old_bones[prefix + 'index1'].head_local + old_bones[prefix + 'ring1'].head_local) * .5)
            parent = 'Forearm.'+side
        elif src_name == 'pinky0':
            tail = M @ old_bones[prefix + 'pinky1'].head_local
            parent = names['hand']
        else:
            finger, j = src_name[:-1], int(src_name[-1])
            next_name = finger + str(j+1) if j < 3 else finger + '_ignore'
            tail = M @ old_bones[prefix + next_name].head_local
            parent = names[finger + str(j-1)] if j > 1 else (names['pinky0'] if finger == 'pinky' else names['hand'])
        bone_specs.append((name, head, tail, parent, dorsal))
    weights = []
    for v in mesh.data.vertices:
        merged = {}
        for assignment in v.groups:
            source_name = mesh.vertex_groups[assignment.group].name
            short = source_name.removeprefix(prefix)
            target_name = names.get(short)
            if not target_name and '_ignore' in short:
                target_name = names.get(short.replace('_ignore', '3'))
            target_name = target_name or names['hand']
            merged[target_name] = merged.get(target_name, 0) + assignment.weight
        total = sum(merged.values())
        if total <= 1e-8:
            merged = {names['hand']: 1}
            total = 1
        weights.append({k: v / total for k, v in merged.items() if v > 1e-6})
    mesh.parent = None
    mesh.matrix_world = Matrix.Identity(4)
    mesh.modifiers.clear()
    mesh.name = f'SK_FPArm_{side}_01'
    mesh.data.name = f'GEO_FPArm_{side}_01'
    mesh.vertex_groups.clear()
    groups = {name: mesh.vertex_groups.new(name=name) for name in names.values()}
    for index, assignments in enumerate(weights):
        for name, weight in assignments.items():
            groups[name].add([index], weight, 'REPLACE')
    mesh.data.materials.clear()
    for _ in range(3):mesh.data.materials.append(mat)
    mesh.data.uv_layers.active.name='UV_Source'
    elbow,samples=extend_forearm(mesh,side,M,wrist)
    skin_samples.extend(samples)
    bone_specs.append(('Forearm.'+side,elbow,M@wrist,'Root',dorsal))
    mesh.data.transform(M)
    relocate(mesh, ASSET)
    # Recover useful quads without crossing UV seams, then refine the silhouette.
    bm = bmesh.new()
    bm.from_mesh(mesh.data)
    bmesh.ops.join_triangles(bm, faces=list(bm.faces),
        angle_face_threshold=math.radians(40), angle_shape_threshold=math.radians(60),
        cmp_uvs=True, cmp_seam=True, cmp_sharp=True, cmp_materials=True)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh.data)
    bm.free()
    bpy.ops.object.select_all(action='DESELECT')
    mesh.select_set(True)
    bpy.context.view_layer.objects.active = mesh
    sub = mesh.modifiers.new('Silhouette_Refinement', 'SUBSURF')
    sub.levels = 1
    sub.render_levels = 1
    bpy.ops.object.modifier_apply(modifier=sub.name)
    # Four normalized influences keep the exported asset compatible with Unity.
    bpy.ops.object.vertex_group_limit_total(limit=4)
    bpy.ops.object.vertex_group_normalize_all(lock_active=False)
    for poly in mesh.data.polygons:
        poly.use_smooth = True
    mesh.parent = rig
    mesh.matrix_parent_inverse = Matrix.Identity(4)
    deform = mesh.modifiers.new('Finger_Deformation', 'ARMATURE')
    deform.object = rig
    # Linear skinning matches the standard Unity runtime path.
    deform.use_deform_preserve_volume = False
    mesh['Source_triangles'] = 1678
    mesh['Description'] = 'Continuous hand and 26 cm forearm; no body or equipment.'
    meshes.append(mesh)
    for o in imported:
        if o != mesh:
            bpy.data.objects.remove(o, do_unlink=True)

bpy.ops.object.select_all(action='DESELECT')
rig.select_set(True)
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode='EDIT')
root_bone = rig_data.edit_bones.new('Root')
root_bone.head = (0, 0, 0)
root_bone.tail = (0, .05, 0)
root_bone.use_deform = False
for name, head, tail, parent, dorsal in bone_specs:
    b = rig_data.edit_bones.new(name)
    b.head = head
    b.tail = tail
    b.align_roll(dorsal)
for name, head, tail, parent, dorsal in bone_specs:
    b = rig_data.edit_bones[name]
    b.parent = rig_data.edit_bones[parent]
    b.use_connect = (b.head - b.parent.tail).length < .00001
bpy.ops.object.mode_set(mode='OBJECT')
for pb in rig.pose.bones:
    pb.rotation_mode = 'XYZ'
add_forearm_skin_shader(mat,images,skin_samples)

world = bpy.data.worlds.new('World_FPHands_Studio')
world.use_nodes = True
background = next(n for n in world.node_tree.nodes if n.type == 'BACKGROUND')
background.inputs['Color'].default_value = (.075, .09, .115, 1)
background.inputs['Strength'].default_value = .3
SCENE.world = world
def area(name, loc, watts, size, color):
    d = bpy.data.lights.new(name, 'AREA')
    d.energy = watts
    d.shape = 'DISK'
    d.size = size
    d.color = color
    o = bpy.data.objects.new(name, d)
    PREVIEW.objects.link(o)
    o.location = loc
    o.rotation_euler = (Vector((0, .4, -.13)) - o.location).to_track_quat('-Z', 'Y').to_euler()
area('Preview_Key', (-.45, .05, .6), 12, .65, (1, .93, .87))
area('Preview_Fill', (.45, .15, .20), 5, .55, (.80, .88, 1))
area('Preview_Rim', (0, .8, .4), 14, .40, (1, .95, .9))

def camera(name, loc, target=None):
    d = bpy.data.cameras.new(name)
    o = bpy.data.objects.new(name, d)
    PREVIEW.objects.link(o)
    o.location = loc
    if target:
        o.rotation_euler = (Vector(target) - o.location).to_track_quat('-Z', 'Y').to_euler()
    d.clip_start = .01
    d.clip_end = 100
    return o
fps = camera('CAM_FirstPerson_60deg', (0, 0, 0))
fps.rotation_euler = (math.pi / 2, 0, 0)
fps.data.sensor_fit = 'VERTICAL'
fps.data.sensor_height = 24
fps.data.lens = 24 / (2 * math.tan(math.radians(30)))
fps['Vertical_FOV_degrees'] = 60
inspect = camera('CAM_Hand_Detail', (0, .04, .65), (0, .39, -.20))
inspect.data.type = 'ORTHO'
inspect.data.ortho_scale = 1.02
SCENE.camera = fps
SCENE.render.engine = 'CYCLES'
SCENE.cycles.samples = 64
SCENE.cycles.use_denoising = True
SCENE.render.threads_mode = 'FIXED'
SCENE.render.threads = 8
SCENE.render.resolution_x = 1600
SCENE.render.resolution_y = 900
SCENE.render.resolution_percentage = 100
SCENE.render.image_settings.file_format = 'PNG'
SCENE.render.image_settings.color_mode = 'RGBA'
SCENE.render.film_transparent = False
SCENE.view_settings.view_transform = 'AgX'
SCENE.view_settings.exposure = 0
SCENE.view_settings.look = 'AgX - Medium High Contrast'
bpy.context.view_layer.update()
exec((ROOT/'Scripts'/'bake_pbr.py').read_text(encoding='utf-8'),globals())
for cam, file in [(fps, 'FPHands_FirstPerson.png'), (inspect, 'FPHands_Detail.png')]:
    SCENE.camera = cam
    SCENE.render.filepath = str(ROOT / 'Previews' / file)
    bpy.ops.render.render(write_still=True)
SCENE.camera = fps
print(json.dumps({'triangles': {o.name: sum(len(p.vertices)-2 for p in o.data.polygons) for o in meshes},
                  'vertices': {o.name:len(o.data.vertices) for o in meshes}, 'bones':len(rig_data.bones)}))
