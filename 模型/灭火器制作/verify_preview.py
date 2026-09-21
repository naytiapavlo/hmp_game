import bpy, json, numpy as np
from pathlib import Path
from mathutils import Vector
repo=Path(__file__).resolve().parents[2]; src=repo/'模型/办公室场景'; out=repo/'.codex-artifacts/extinguisher-upgrade'
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(src/'SM_Extinguisher_01.fbx'))
meshes=[o for o in bpy.data.objects if o.type=='MESH']
assert len(meshes)==11
assert not any(o.type in ('CAMERA','LIGHT') for o in bpy.data.objects)
assert bpy.data.objects.get('SafetyPin') and bpy.data.objects.get('Nozzle')
vs=[o.matrix_world@v.co for o in meshes for v in o.data.vertices]
height=max(v.z for v in vs)-min(v.z for v in vs)
assert abs(height-.5)<.0001, height
tri=sum(len(p.vertices)-2 for o in meshes for p in o.data.polygons)
assert tri<=50000
assert all(o.data.uv_layers.active for o in meshes)
stats=[]
for p in sorted((src/'灭火器贴图').glob('*.png')):
 im=bpy.data.images.load(str(p)); im.colorspace_settings.name='Non-Color'
 arr=np.array(im.pixels[:]).reshape(-1,4)
 assert tuple(im.size)==(1024,1024)
 assert np.isfinite(arr).all()
 stats.append({'file':p.name,'min':arr.min(0).tolist(),'max':arr.max(0).tolist()})
 if '_Normal.' in p.name: assert arr[:,2].mean()>.55
 if '_AO.' in p.name: assert arr[:,0].max()>.5 and arr[:,0].min()<.99
(out/'validation.json').write_text(json.dumps({'fbx_roundtrip':True,'height_m':height,'triangles':tri,'maps':stats},indent=2))

bpy.ops.wm.open_mainfile(filepath=str(src/'SM_Extinguisher_01.blend'),use_scripts=False)
for m in bpy.data.materials:
 if not m.use_nodes: continue
 nodes=m.node_tree.nodes; links=m.node_tree.links; bs=nodes.get('Principled BSDF')
 if not bs: continue
 for node in list(nodes):
  if node.type=='TEX_IMAGE' and node.image and node.image.name.endswith('_Normal'):
   normal=nodes.new('ShaderNodeNormalMap'); links.new(node.outputs['Color'],normal.inputs['Color']); links.new(normal.outputs['Normal'],bs.inputs['Normal'])
for im in bpy.data.images:
 if im.source=='FILE': im.filepath=bpy.path.relpath(im.filepath,start=str(src))
bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=str(src/'SM_Extinguisher_01.blend'))
scene=bpy.context.scene; scene.render.engine='CYCLES'; scene.cycles.samples=48
scene.render.resolution_x=800;scene.render.resolution_y=900;scene.render.resolution_percentage=100
scene.world.node_tree.nodes['Background'].inputs['Color'].default_value=(.11,.13,.16,1)
scene.world.node_tree.nodes['Background'].inputs['Strength'].default_value=.4
for pos,power,size in [((.5,-.6,1),50,.6),((-.5,.3,.8),35,.5),((.3,.8,.5),25,.4)]:
 bpy.ops.object.light_add(type='AREA',location=pos); l=bpy.context.object;l.data.energy=power;l.data.shape='DISK';l.data.size=size
 l.rotation_euler=(Vector((0,0,.25))-l.location).to_track_quat('-Z','Y').to_euler()
bpy.ops.object.camera_add();cam=bpy.context.object;scene.camera=cam;cam.data.type='ORTHO';cam.data.ortho_scale=.61
center=Vector((0,0,.25));cam.location=center+Vector((.8,-1,.45));cam.rotation_euler=(center-cam.location).to_track_quat('-Z','Y').to_euler()
scene.render.filepath=str(out/'extinguisher-pbr.png');bpy.ops.render.render(write_still=True)
cam.data.ortho_scale=.21;center=Vector((0,.025,.425));cam.location=center+Vector((.9,-1,.35));cam.rotation_euler=(center-cam.location).to_track_quat('-Z','Y').to_euler()
scene.render.filepath=str(out/'extinguisher-detail.png');bpy.ops.render.render(write_still=True)
print('VALIDATION_OK',height,tri,flush=True)
