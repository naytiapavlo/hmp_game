"""Render transparent flame/smoke passes from a baked Mantaflow tier."""
import argparse, json, sys, time
from pathlib import Path
import bpy
from mathutils import Vector

p=argparse.ArgumentParser()
p.add_argument('--bake',required=True)
p.add_argument('--preview',action='store_true')
p.add_argument('--size',type=int,default=256)
p.add_argument('--samples',type=int,default=24)
p.add_argument('--engine',choices=['CYCLES','BLENDER_EEVEE'],default='BLENDER_EEVEE')
a=p.parse_args(sys.argv[sys.argv.index('--')+1:])
root=Path(a.bake).resolve()
bpy.ops.wm.open_mainfile(filepath=str(root/'baked.blend'),use_scripts=False)
info=json.loads((root/'bake.json').read_text())
scene=bpy.context.scene
domain=bpy.data.objects['FIRE | Gas Simulation Domain (boundary only)']
width=info['width']; height=info['height']
scene.render.engine=a.engine
scene.eevee.taa_render_samples=8
scene.eevee.volumetric_tile_size='2'
scene.eevee.volumetric_samples=32
scene.eevee.use_volumetric_shadows=True
scene.eevee.use_volume_custom_range=True
scene.eevee.volumetric_start=.001
scene.eevee.volumetric_end=height*6
prefs=bpy.context.preferences.addons['cycles'].preferences
prefs.compute_device_type='OPTIX';prefs.get_devices()
for d in prefs.devices:d.use=d.type=='OPTIX'
scene.cycles.device='GPU'
scene.cycles.samples=a.samples
scene.cycles.use_denoising=True
scene.cycles.volume_bounces=1
scene.cycles.volume_step_rate=.75
scene.render.resolution_x=scene.render.resolution_y=a.size
scene.render.resolution_percentage=100
scene.render.film_transparent=True
scene.render.image_settings.file_format='PNG'
scene.render.image_settings.color_mode='RGBA'
scene.render.image_settings.color_depth='8'
scene.view_settings.view_transform='AgX'
scene.view_settings.look='AgX - Medium High Contrast'
scene.view_settings.exposure=0
world=bpy.data.worlds.new('Fire neutral studio');world.use_nodes=True
world.node_tree.nodes.get('Background').inputs['Color'].default_value=(.3,.35,.4,1)
world.node_tree.nodes.get('Background').inputs['Strength'].default_value=.35
scene.world=world
cam_data=bpy.data.cameras.new('Atlas orthographic')
cam=bpy.data.objects.new('Atlas Camera',cam_data);scene.collection.objects.link(cam)
cam_data.type='ORTHO';scene.camera=cam
for name,pos,power,scale in [('Key',(-width*2,-width*3,height),400,2),('Rim',(width*2,width,height*.7),250,1)]:
    ld=bpy.data.lights.new(name,'AREA');ld.energy=power*width*width;ld.shape='DISK';ld.size=width*scale
    ob=bpy.data.objects.new(name,ld);scene.collection.objects.link(ob);ob.location=pos
    ob.rotation_euler=(Vector((0,0,height*.4))-ob.location).to_track_quat('-Z','Y').to_euler()

def material(layer):
    mat=bpy.data.materials.new('Export '+layer);mat.use_nodes=True
    nt=mat.node_tree;nt.nodes.clear()
    out=nt.nodes.new('ShaderNodeOutputMaterial')
    vol=nt.nodes.new('ShaderNodeVolumePrincipled')
    if 'Weight' in vol.inputs: vol.inputs['Weight'].default_value=1
    vol.inputs['Density Attribute'].default_value=''
    vol.inputs['Color Attribute'].default_value=''
    vol.inputs['Temperature Attribute'].default_value=''
    vol.inputs['Blackbody Intensity'].default_value=0
    vol.inputs['Anisotropy'].default_value=.1
    nt.links.new(vol.outputs['Volume'],out.inputs['Volume'])
    attr=nt.nodes.new('ShaderNodeAttribute');attr.attribute_name='flame' if layer=='flame' else 'density'
    density=nt.nodes.new('ShaderNodeMath');density.operation='MULTIPLY'
    density.inputs[1].default_value=(5.0 if layer=='flame' else 2.5)/max(width,.2)
    nt.links.new(attr.outputs['Fac'],density.inputs[0]);nt.links.new(density.outputs[0],vol.inputs['Density'])
    if layer=='flame':
        ramp=nt.nodes.new('ShaderNodeValToRGB')
        ramp.color_ramp.elements.remove(ramp.color_ramp.elements[1])
        colors=[(0,(.6,.025,.001,1)),(.12,(1,.12,.005,1)),(.4,(1,.48,.03,1)),(.8,(1,.92,.55,1)),(1,(1,1,.85,1))]
        for i,(pos,col) in enumerate(colors):
            e=ramp.color_ramp.elements[0] if i==0 else ramp.color_ramp.elements.new(pos)
            e.position=pos;e.color=col
        nt.links.new(attr.outputs['Fac'],ramp.inputs[0]);nt.links.new(ramp.outputs[0],vol.inputs['Emission Color'])
        strength=nt.nodes.new('ShaderNodeMath');strength.operation='MULTIPLY';strength.inputs[1].default_value=3/max(width,.2)
        nt.links.new(attr.outputs['Fac'],strength.inputs[0]);nt.links.new(strength.outputs[0],vol.inputs['Emission Strength'])
        vol.inputs['Color'].default_value=(1,.25,.01,1)
    else:
        color=.8 if info['tier']=='SmokeOnly' else .07
        vol.inputs['Color'].default_value=(color,color,color,1)
    domain.data.materials.clear();domain.data.materials.append(mat)

layers=['smoke'] if info['tier']=='SmokeOnly' else ['flame','smoke']
start=time.time()
for layer in layers:
    material(layer)
    span=height*1.12
    bottom=-.10*info['preset']-.015*height
    target=Vector((0,0,bottom+span/2))
    cam.location=target+Vector((0,-height*4,0))
    cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
    cam_data.ortho_scale=span
    directory=root/layer;directory.mkdir(exist_ok=True)
    for frame in ([48] if a.preview else range(25,89)):
        scene.frame_set(frame)
        scene.render.filepath=str(directory/f'{frame:04d}.png')
        bpy.ops.render.render(write_still=True)
        print('RENDER_FRAME',info['tier'],layer,frame,flush=True)
(root/('preview.json' if a.preview else 'render.json')).write_text(json.dumps({
    'size':a.size,'samples':a.samples,'layers':layers,'frame_start':25,'frame_count':64,
    'seconds':time.time()-start,'engine':a.engine},indent=2))
