"""Bake the supplied Mantaflow rig into a portable, renderable working copy.
Run with Blender --background --factory-startup --disable-autoexec --python this.py -- ...
The original 火焰.blend is never overwritten. Cache and frames are build artifacts.
"""
import argparse, json, math, sys, time
from pathlib import Path
import bpy
from mathutils import Vector

args = argparse.ArgumentParser()
args.add_argument('--tier', choices=['Small','Medium','Large','SmokeOnly'], default='Small')
args.add_argument('--output', required=True)
args.add_argument('--resolution', type=int, default=72)
args.add_argument('--frames', type=int, default=96)
a = args.parse_args(sys.argv[sys.argv.index('--')+1:])
root = Path(a.output).resolve() / a.tier
root.mkdir(parents=True, exist_ok=True)
source = Path(__file__).resolve().parent.parent / '火焰.blend'
bpy.ops.wm.open_mainfile(filepath=str(source), use_scripts=False)
scene = bpy.context.scene
domain = bpy.data.objects['FIRE | Gas Simulation Domain (boundary only)']
flow = bpy.data.objects['FIRE | Physical fuel bed']
ctrl = bpy.data.objects['FIRE | Master Controller']
f = {'Small':.12, 'Medium':1., 'Large':2.5, 'SmokeOnly':.12}[a.tier]
for obj in list(bpy.data.objects):
    if obj not in (domain, flow, ctrl): bpy.data.objects.remove(obj, do_unlink=True)
ctrl.animation_data_clear()
ctrl.location = (0,0,0)
ctrl.scale = (1,1,1)
ctrl['Fire Size'] = f
for obj in (domain, flow):
    obj.animation_data_clear()
    obj.hide_set(False)
domain.hide_render = False
flow.hide_render = True
height = .25 + 3.25 * f**.75
width = .10 + 1.60 * f
domain.scale = (width/1.7, width/1.7, height/3.5)
domain.location = (0,0,height/2-.1*f)
flow.scale = (f, f, (.02+.09*f)/.11)
flow.location = (0,0,.02+.065*f)
# New modifier clears stale baked flags without freeing/deleting another machine's cache.
for m in list(domain.modifiers): domain.modifiers.remove(m)
mod = domain.modifiers.new('Portable Mantaflow Domain','FLUID')
mod.fluid_type = 'DOMAIN'
ds = mod.domain_settings
ds.domain_type = 'GAS'
ds.resolution_max = a.resolution
ds.cache_directory = str(root/'cache')
ds.cache_type = 'ALL'
ds.cache_data_format = 'OPENVDB'
ds.cache_frame_start = 1
ds.cache_frame_end = a.frames
ds.use_noise = True
ds.noise_scale = 2
ds.noise_strength = 1.
ds.noise_pos_scale = 2.
ds.noise_time_anim = .1
ds.use_adaptive_domain = False
ds.vorticity = 1.2
ds.flame_vorticity = 1.5
ds.burning_rate = .65
ds.flame_smoke = 1.
ds.flame_ignition = 1.5
ds.flame_max_temp = 3.
ds.use_dissolve_smoke = True
ds.dissolve_speed = 45
ds.use_dissolve_smoke_log = True
for face in ('front','back','left','right','top','bottom'):
    setattr(ds,'use_collision_border_'+face,False)
fs = next(m.flow_settings for m in flow.modifiers if m.type=='FLUID')
fs.flow_type = 'SMOKE' if a.tier=='SmokeOnly' else 'BOTH'
fs.flow_behavior = 'INFLOW'
fs.fuel_amount = .78 * math.sqrt(f)
fs.density = f**.35
fs.temperature = .5+.5*math.sqrt(f)
fs.use_initial_velocity = True
fs.velocity_normal = .35*math.sqrt(f)
fs.surface_distance = 1.5
scene.frame_start=1
scene.frame_end=a.frames
scene.render.fps=24
scene.frame_set(1)
bpy.context.view_layer.update()
bpy.ops.object.select_all(action='DESELECT')
domain.select_set(True)
bpy.context.view_layer.objects.active=domain
bpy.ops.wm.save_as_mainfile(filepath=str(root/'baked.blend'))
start=time.time()
print('BAKE_START',a.tier, list(domain.dimensions),flush=True)
bpy.ops.fluid.bake_all()
scene.frame_set(48)
bpy.context.view_layer.update()
stats={'tier':a.tier,'source':str(source),'preset':f,'resolution':a.resolution,
       'frames':a.frames,'seconds':time.time()-start,'cache_files':len(list((root/'cache').rglob('*.vdb'))),
       'dimensions':list(domain.dimensions),'height':height,'width':width}
(root/'bake.json').write_text(json.dumps(stats,indent=2),encoding='utf-8')
bpy.ops.wm.save_as_mainfile(filepath=str(root/'baked.blend'))
print('BAKE_DONE',json.dumps(stats),flush=True)
