"""Author semantic parts and PBR maps from the project's existing extinguisher.
Run in Blender background mode. Back up the original FBX before the first run.
No downloaded assets. AO and tangent normals are geometry/shader bakes;
roughness/metalness are authored material estimates, not measured scans.
"""
import bpy, numpy as np, json, shutil, hashlib
from pathlib import Path
from mathutils import Vector

repo=Path(__file__).resolve().parents[2]
source=repo/'模型/办公室场景/SM_Extinguisher_01.fbx'
texdir=source.parent/'灭火器贴图'
work=repo/'.codex-artifacts/extinguisher-upgrade'
work.mkdir(parents=True,exist_ok=True)
backup=work/'SM_Extinguisher_01.original.fbx'
if not backup.exists(): shutil.copy2(source,backup)
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(backup))
meshes=[o for o in bpy.data.objects if o.type=='MESH']
for o in list(bpy.data.objects):
 if o.type!='MESH': bpy.data.objects.remove(o,do_unlink=True)
names={0:'Cylinder',1:'BaseBand',2:'SafetyTether',3:'Hose',4:'NozzleBody',5:'SqueezeLever',6:'CarryHandle',7:'PressureGauge',8:'BaseBoot',9:'SafetyPin',10:'ValveCollar'}
parts={int(o.data.materials[0].name.rsplit('_',1)[-1]):o for o in meshes}
allv=[o.matrix_world@v.co for o in meshes for v in o.data.vertices]
lo=Vector([min(v[i] for v in allv) for i in range(3)]); hi=Vector([max(v[i] for v in allv) for i in range(3)])
pivot=Vector(((lo.x+hi.x)/2,(lo.y+hi.y)/2,lo.z)); scale=.5/(hi.z-lo.z)
for n,o in parts.items():
 matrix=o.matrix_world.copy(); o.parent=None; o.matrix_world.identity()
 for v in o.data.vertices: v.co=(matrix@v.co-pivot)*scale
 o.name=names[n]; o.data.name=names[n]+'_Mesh'
 # Preserve source material identifiers: Unity's existing remaps remain valid.
 assert o.data.uv_layers.active is not None
 o.data.update()

# Opening center: bottom rim of the hanging discharge nozzle, not bottle top.
nozzle=parts[4]; zmin=min(v.co.z for v in nozzle.data.vertices)
rim=[v.co for v in nozzle.data.vertices if v.co.z<zmin+.003]
tip=sum(rim,Vector())/len(rim); tip.z=zmin-.0005
anchor=bpy.data.objects.new('Nozzle',None); bpy.context.collection.objects.link(anchor)
anchor.parent=nozzle; anchor.location=tip
anchor.rotation_euler=Vector((0,0,-1)).to_track_quat('Z','Y').to_euler()
anchor.empty_display_size=.012

scene=bpy.context.scene; scene.render.engine='CYCLES'; scene.cycles.samples=16
scene.render.bake.margin=8; scene.render.bake.use_clear=True
scene.render.bake.use_selected_to_active=False
scene.world=bpy.data.worlds.new('BakeWorld'); scene.world.use_nodes=True
scene.world.node_tree.nodes['Background'].inputs['Color'].default_value=(.2,.2,.2,1)
size=1024
yy,xx=np.mgrid[0:size,0:size].astype(np.float32)/size
rng=np.random.default_rng(19677)
fine=rng.random((size,size)).astype(np.float32)-.5
wear=(np.sin(xx*57+np.sin(yy*23))*np.cos(yy*43)+1)/2
def save_map(n,kind,arr):
 if arr.ndim==2: arr=np.stack((arr,arr,arr,np.ones_like(arr)),axis=-1)
 im=bpy.data.images.new(f'Part{n:02d}_{kind}',size,size,alpha=True)
 im.colorspace_settings.name='Non-Color'; im.pixels.foreach_set(arr.astype(np.float32).ravel())
 im.filepath_raw=str(texdir/f'T_Extinguisher_01_Part{n:02d}_{kind}.png'); im.file_format='PNG'; im.save()
 return im
targets={'AO':{},'Normal':{}}
for n,o in parts.items():
 m=o.data.materials[0]; m.use_nodes=True; nodes=m.node_tree.nodes; nodes.clear(); links=m.node_tree.links
 out=nodes.new('ShaderNodeOutputMaterial'); bs=nodes.new('ShaderNodeBsdfPrincipled'); links.new(bs.outputs['BSDF'],out.inputs['Surface'])
 base=nodes.new('ShaderNodeTexImage'); base.image=bpy.data.images.load(str(texdir/f'T_Extinguisher_01_Part{n:02d}_BaseColor.jpg'))
 links.new(base.outputs['Color'],bs.inputs['Base Color'])
 rubber=n in (3,4,8); painted=n in (0,5,6); tether=n==2
 rough0=.72 if rubber else .6 if tether else .4 if painted else .29
 metal0=0 if rubber or tether or painted else .85
 rough=np.clip(rough0+.065*(wear-.5)+.035*fine,.1,.9)
 metal=np.full((size,size),metal0,dtype=np.float32)
 # Printed gauge face is dielectric; metal is only the desaturated housing.
 if n==7:
  temp=base.image.copy(); temp.scale(size,size); rgb=np.array(temp.pixels[:]).reshape(size,size,4)[:,:,:3]
  sat=rgb.max(2)-rgb.min(2); metal=np.where(sat>.13,0,.82).astype(np.float32)
  bpy.data.images.remove(temp)
 ri=save_map(n,'Roughness',rough); mi=save_map(n,'Metallic',metal)
 save_map(n,'MetallicSmoothness',np.stack((metal,np.zeros_like(metal),np.zeros_like(metal),1-rough),axis=-1))
 for im,inp in ((ri,'Roughness'),(mi,'Metallic')):
  node=nodes.new('ShaderNodeTexImage'); node.image=im; links.new(node.outputs['Color'],bs.inputs[inp])
 noise=nodes.new('ShaderNodeTexNoise'); noise.inputs['Scale'].default_value=450 if rubber else 900
 bump=nodes.new('ShaderNodeBump'); bump.inputs['Strength'].default_value=.14 if rubber else .08; bump.inputs['Distance'].default_value=.00012
 links.new(noise.outputs['Fac'],bump.inputs['Height']); links.new(bump.outputs['Normal'],bs.inputs['Normal'])
 for kind in targets:
  im=bpy.data.images.new(f'Part{n:02d}_{kind}',size,size,alpha=False); im.colorspace_settings.name='Non-Color'
  im.generated_color=(1,1,1,1) if kind=='AO' else (.5,.5,1,1)
  im.filepath_raw=str(texdir/f'T_Extinguisher_01_Part{n:02d}_{kind}.png'); im.file_format='PNG'
  node=nodes.new('ShaderNodeTexImage'); node.image=im; targets[kind][n]=node
bpy.ops.object.select_all(action='DESELECT')
for o in meshes: o.select_set(True)
bpy.context.view_layer.objects.active=parts[0]
for kind in ('AO','Normal'):
 for n,o in parts.items(): o.data.materials[0].node_tree.nodes.active=targets[kind][n]
 print('BAKE',kind,flush=True)
 bpy.ops.object.bake(type='AO' if kind=='AO' else 'NORMAL')
 for node in targets[kind].values(): node.image.save()
 print('BAKE_DONE',kind,flush=True)

# Save an editable source without preview cameras/lights, and mesh + anchor FBX.
for im in bpy.data.images:
 if im.source=='FILE': im.filepath=bpy.path.relpath(im.filepath,start=str(source.parent))
bpy.ops.wm.save_as_mainfile(filepath=str(source.with_suffix('.blend')))
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.fbx(filepath=str(source),use_selection=True,object_types={'MESH','EMPTY'},
 axis_forward='-Z',axis_up='Y',apply_unit_scale=True,apply_scale_options='FBX_SCALE_UNITS',
 bake_anim=False,add_leaf_bones=False,path_mode='RELATIVE',use_mesh_modifiers=True)
report={'height_m':.5,'faces':sum(len(o.data.polygons) for o in meshes),'triangles':sum(len(p.vertices)-2 for o in meshes for p in o.data.polygons),
 'parts':{str(n):o.name for n,o in sorted(parts.items())},'nozzle_blender_m':list(tip),'maps_per_part':['BaseColor','Normal','Roughness','Metallic','AO','MetallicSmoothness'],
 'source_sha256':hashlib.sha256(backup.read_bytes()).hexdigest(),'fbx_sha256':hashlib.sha256(source.read_bytes()).hexdigest(),
 'pbr_note':'AO and tangent normals baked in Cycles; roughness/metalness artist-authored from part materials; no high-poly source or measured PBR scan.'}
(source.parent/'灭火器资产清单.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
print('UPGRADE_DONE',json.dumps(report),flush=True)
