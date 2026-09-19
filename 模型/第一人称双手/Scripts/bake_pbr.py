"""Bake hand and forearm shading to a single 2K atlas, retaining UV islands."""
for side_index,mesh in enumerate(meshes):
    assert sum(p.material_index==1 for p in mesh.data.polygons)>100, 'Forearm UV region missing'
    source_uv=mesh.data.uv_layers['UV_Source']
    arm_uv=mesh.data.uv_layers['UV_Forearm']
    target_uv=mesh.data.uv_layers.new(name='UVMap')
    x_offset=.006+side_index*.5
    for p in mesh.data.polygons:
        for li in p.loop_indices:
            if p.material_index==0:
                u,v=source_uv.data[li].uv
                target_uv.data[li].uv=(x_offset+.488*u,.544+.450*v)
            elif p.material_index==1:
                u,v=arm_uv.data[li].uv
                target_uv.data[li].uv=(x_offset+.488*u,.096+.430*v)
            else:
                u,v=arm_uv.data[li].uv
                target_uv.data[li].uv=(x_offset+.206+.070*u,.010+.070*v)
    mesh.data.uv_layers.active=target_uv
    target_uv.active_render=True

bpy.ops.object.select_all(action='DESELECT')
for mesh in meshes:mesh.select_set(True)
bpy.context.view_layer.objects.active=meshes[0]
SCENE.cycles.samples=16
SCENE.render.bake.margin=8
SCENE.render.bake.use_clear=True
nodes=mat.node_tree.nodes
links=mat.node_tree.links
out=next(x for x in nodes if x.type=='OUTPUT_MATERIAL')
bs=next(x for x in nodes if x.type=='BSDF_PRINCIPLED')
emit=nodes.new('ShaderNodeEmission')
emit.inputs['Strength'].default_value=1
bake_node=nodes.new('ShaderNodeTexImage')
bake_node.name='BAKE_TARGET'
nodes.active=bake_node
final_images={}
for kind in ('BaseColor','Roughness','Normal','AO'):
    im=bpy.data.images.new('T_FPHands_01_'+kind, width=2048, height=2048, alpha=False)
    if kind!='BaseColor':im.colorspace_settings.name='Non-Color'
    if kind=='Normal':im.generated_color=(.5,.5,1,1)
    bake_node.image=im
    if kind in ('BaseColor','Roughness'):
        links.new(nodes['BAKE_'+kind].outputs[0],emit.inputs['Color'])
        links.new(emit.outputs[0],out.inputs['Surface'])
        bpy.ops.object.bake(type='EMIT')
    else:
        links.new(bs.outputs[0],out.inputs['Surface'])
        bpy.ops.object.bake(type='NORMAL' if kind=='Normal' else 'AO')
    im.filepath_raw=str(ROOT/'Textures'/('T_FPHands_01_'+kind+'.png'))
    im.file_format='PNG'
    im.save()
    im.pack()
    final_images[kind]=im
    print('BAKED',kind,flush=True)

rgba=np.zeros((2048*2048,4),dtype=np.float32)
rr=np.empty(2048*2048*4,dtype=np.float32)
final_images['Roughness'].pixels.foreach_get(rr)
rgba[:,3]=1-np.clip(rr.reshape(-1,4)[:,0],0,1)
im=bpy.data.images.new('T_FPHands_01_MetallicSmoothness',width=2048,height=2048,alpha=True)
im.colorspace_settings.name='Non-Color'
im.pixels.foreach_set(rgba.ravel())
im.filepath_raw=str(ROOT/'Textures'/'T_FPHands_01_MetallicSmoothness.png')
im.file_format='PNG';im.save();im.pack()
final_images['MetallicSmoothness']=im

# Use file-backed images after baking so packed images retain portable paths.
# Blender can clear filepath when a generated image is packed repeatedly.
for kind,generated in list(final_images.items()):
    generated.name='BAKED_TMP_'+kind
    image_path=ROOT/'Textures'/('T_FPHands_01_'+kind+'.png')
    file_image=bpy.data.images.load(str(image_path),check_existing=False)
    file_image.colorspace_settings.name='sRGB' if kind=='BaseColor' else 'Non-Color'
    _=file_image.size[:];_=file_image.pixels[0]
    file_image.pack()
    file_image.name='T_FPHands_01_'+kind
    file_image.filepath=str(image_path)
    final_images[kind]=file_image

nodes.clear()
mat.name='M_FPHands_01'
out=nodes.new('ShaderNodeOutputMaterial');out.location=(600,160)
bs=nodes.new('ShaderNodeBsdfPrincipled');bs.location=(300,160)
bs.inputs['Metallic'].default_value=0
bs.inputs['Specular IOR Level'].default_value=.26
bs.inputs['Subsurface Weight'].default_value=.04
bs.inputs['Subsurface Radius'].default_value=(1,.42,.22)
bs.inputs['Subsurface Scale'].default_value=.005
links.new(bs.outputs[0],out.inputs['Surface'])
for i,kind in enumerate(('BaseColor','Roughness','Normal','AO','MetallicSmoothness')):
    tex=nodes.new('ShaderNodeTexImage');tex.image=final_images[kind]
    tex.name='TEX_'+kind;tex.label=kind+' / 2048';tex.location=(-420,650-i*270)
    if kind=='BaseColor':links.new(tex.outputs['Color'],bs.inputs['Base Color'])
    elif kind=='Roughness':links.new(tex.outputs['Color'],bs.inputs['Roughness'])
    elif kind=='Normal':
        normal=nodes.new('ShaderNodeNormalMap');normal.location=(30,-10)
        normal.inputs['Strength'].default_value=1
        links.new(tex.outputs['Color'],normal.inputs['Color']);links.new(normal.outputs[0],bs.inputs['Normal'])
    elif kind=='AO':tex.label='Unity Occlusion / linear'
    else:tex.label='Unity URP: R metallic, A smoothness / linear'

for mesh in meshes:
    for uv in list(mesh.data.uv_layers):
        if uv.name!='UVMap':mesh.data.uv_layers.remove(uv)
    for poly in mesh.data.polygons:poly.material_index=0
    mesh.data.materials.clear();mesh.data.materials.append(mat)
    mesh.data.uv_layers.active_index=0
    mesh.data.uv_layers[0].active_render=True
    attr=mesh.data.attributes.get('arm_blend')
    if attr:mesh.data.attributes.remove(attr)
SCENE.cycles.samples=64
images=final_images
