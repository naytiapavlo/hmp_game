"""Original continuous forearm extension for the licensed source hand meshes."""
def extend_forearm(mesh, side, transform, wrist_raw):
    bm = bmesh.new()
    bm.from_mesh(mesh.data)
    blend_layer = bm.verts.layers.float.new('arm_blend')
    deform = bm.verts.layers.deform.verify()
    arm_uv = bm.loops.layers.uv.new('UV_Forearm')
    cut_z = -6.7
    def skin_blend(z):
        f=max(0,min(1,(-4.4-z)/6.0))
        return f*f*(3-2*f)
    for v in bm.verts:v[blend_layer]=skin_blend(v.co.z)
    bmesh.ops.bisect_plane(bm,
        geom=list(bm.verts) + list(bm.edges) + list(bm.faces),
        plane_co=(0, 0, cut_z), plane_no=(0, 0, 1),
        clear_inner=True, clear_outer=False, dist=.00001)
    edges = [e for e in bm.edges if e.is_boundary]
    assert len(edges) >= 12, 'Expected a single open wrist ring'
    verts = {v for e in edges for v in e.verts}
    start = min(verts, key=lambda v: v.co.y)
    ring = [start]
    previous = None
    current = start
    while True:
        candidates = [e.other_vert(current) for e in current.link_edges if e.is_boundary and e.other_vert(current) != previous]
        nxt = next((v for v in candidates if v not in ring), None)
        if nxt is None:
            break
        ring.append(nxt)
        previous, current = current, nxt
    assert len(ring) == len(verts), 'Multiple boundary loops'
    cx = (min(v.co.x for v in ring) + max(v.co.x for v in ring)) * .5
    cy = (min(v.co.y for v in ring) + max(v.co.y for v in ring)) * .5
    rx = (max(v.co.x for v in ring) - min(v.co.x for v in ring)) * .5
    ry = (max(v.co.y for v in ring) - min(v.co.y for v in ring)) * .5
    angles = [math.atan2((v.co.y-cy)/ry, (v.co.x-cx)/rx) for v in ring]
    wrist_offsets = [v.co - Vector((cx,cy,cut_z)) for v in ring]
    uv_layer = bm.loops.layers.uv['UV_Source']
    edge_uvs = []
    for i,v in enumerate(ring):
        other=ring[(i+1)%len(ring)]
        edge=next(e for e in v.link_edges if other in e.verts)
        face=edge.link_faces[0]
        edge_uvs.append((next(l[uv_layer].uv.copy() for l in face.loops if l.vert==v),
                         next(l[uv_layer].uv.copy() for l in face.loops if l.vert==other)))
    forearm_group = mesh.vertex_groups.new(name='Forearm.'+side)
    wrist_index = mesh.vertex_groups['Wrist.'+side].index
    # Sampled cross sections: narrow wrist, extensor belly, tapered elbow.
    profiles = [(.008,rx*1.01,ry*1.02),(.020,rx*1.045,ry*1.09),
                (.038,3.05,2.32),(.060,3.35,2.58),(.086,3.72,2.87),
                (.115,4.08,3.16),(.145,4.38,3.42),(.177,4.43,3.51),
                (.205,4.28,3.45),(.232,4.00,3.27),(.255,3.72,3.08),
                (.262,3.68,3.04)]
    previous_ring=ring
    cumulative=[0.0]
    for i,v in enumerate(ring):cumulative.append(cumulative[-1]+(ring[(i+1)%len(ring)].co-v.co).length)
    fractions=[x/cumulative[-1] for x in cumulative]
    previous_distance=0.0
    sign=-1 if side=='L' else 1
    for distance, radius_x, radius_y in profiles:
        new_ring=[]
        morph=min(1,distance/.065)
        morph=morph*morph*(3-2*morph)
        shift_x=sign*.30*(distance/.262)**1.5
        shift_y=.18*math.sin(math.pi*distance/.262)
        for i,angle in enumerate(angles):
            # Preserve the noncircular wrist, gradually build the muscle contour.
            old=wrist_offsets[i]
            radial=max(0, -math.cos(angle)*(-sign))
            extensor=max(0,math.sin(angle))
            belly=math.exp(-((distance-.145)/.074)**2)
            a=angle+sign*math.radians(6)*morph
            x=radius_x*math.cos(a)*(1+.055*radial*belly)
            y=radius_y*math.sin(a)*(1+.035*extensor*belly)
            x=(1-morph)*old.x*(radius_x/rx)+morph*x
            y=(1-morph)*old.y*(radius_y/ry)+morph*y
            v=bm.verts.new((cx+shift_x+x,cy+shift_y+y,cut_z-distance*100))
            v[blend_layer]=skin_blend(v.co.z)
            wrist_weight=max(0,1-distance/.04)*.72
            v[deform][wrist_index]=wrist_weight
            v[deform][forearm_group.index]=1-wrist_weight
            new_ring.append(v)
        for i in range(len(ring)):
            j=(i+1)%len(ring)
            face=bm.faces.new((previous_ring[i],previous_ring[j],new_ring[j],new_ring[i]))
            face.material_index=1
            uva,uvb=edge_uvs[i]
            for l,uv in zip(face.loops,(uva,uvb,uvb,uva)):
                l[uv_layer].uv=uv
            for l,uv in zip(face.loops,((fractions[i],previous_distance/.262),
                    (fractions[i+1],previous_distance/.262),(fractions[i+1],distance/.262),(fractions[i],distance/.262))):
                l[arm_uv].uv=uv
        previous_ring=new_ring
        previous_distance=distance
    cap=bm.faces.new(tuple(reversed(previous_ring)))
    cap.material_index=2
    for l in cap.loops:
        l[uv_layer].uv=(.25,.10)
        l[arm_uv].uv=((l.vert.co.x-cx)/9+.5,(l.vert.co.y-cy)/8+.5)
    # Blend skinning continuously at the wrist junction.
    for v in ring:
        for k in list(v[deform].keys()):del v[deform][k]
        v[deform][wrist_index]=.85
        v[deform][forearm_group.index]=.15
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.to_mesh(mesh.data)
    bm.free()
    elbow=transform@Vector((cx+sign*.30,cy,cut_z-26.2))
    return elbow, [list(uv) for pair in edge_uvs for uv in pair]


def add_forearm_skin_shader(mat, source_images, samples):
    n=mat.node_tree.nodes;links=mat.node_tree.links
    bs=next(x for x in n if x.type=='BSDF_PRINCIPLED')
    uv=n.new('ShaderNodeUVMap');uv.uv_map='UV_Source';uv.location=(-1600,600)
    for x in n:
        if x.type=='TEX_IMAGE':links.new(uv.outputs['UV'],x.inputs['Vector'])
    base=n['TEX_BaseColor'].outputs['Color']
    roughness=n['TEX_Roughness'].outputs['Color']
    source_normal=next(x for x in n if x.type=='NORMAL_MAP').outputs['Normal']
    pixels=np.empty(2048*2048*4,dtype=np.float32)
    source_images['BaseColor'].pixels.foreach_get(pixels)
    pixels=pixels.reshape(2048,2048,4)
    color=np.mean([pixels[int(v*2047),int(u*2047),:3] for u,v in samples],axis=0)
    # Byte image pixels are sRGB; shader color sockets use scene-linear values.
    linear=np.where(color<.04045,color/12.92,((color+.055)/1.055)**2.4)
    coord=n.new('ShaderNodeTexCoord');coord.location=(-1800,-400)
    broad=n.new('ShaderNodeTexNoise');broad.inputs['Scale'].default_value=35; broad.inputs['Detail'].default_value=3
    links.new(coord.outputs['Object'],broad.inputs['Vector'])
    ramp=n.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position=.15
    ramp.color_ramp.elements[0].color=tuple(linear*np.array([.89,.87,.86]))+(1,)
    ramp.color_ramp.elements[1].position=.85
    ramp.color_ramp.elements[1].color=tuple(linear*np.array([1.06,1.05,1.04]))+(1,)
    links.new(broad.outputs['Fac'],ramp.inputs[0])
    fine=n.new('ShaderNodeTexNoise');fine.inputs['Scale'].default_value=1300;fine.inputs['Detail'].default_value=2
    links.new(coord.outputs['Object'],fine.inputs['Vector'])
    freckles=n.new('ShaderNodeValToRGB')
    freckles.color_ramp.elements[0].position=.65;freckles.color_ramp.elements[0].color=(1,1,1,1)
    freckles.color_ramp.elements[1].position=.77;freckles.color_ramp.elements[1].color=(.70,.68,.65,1)
    links.new(fine.outputs['Fac'],freckles.inputs[0])
    variation=n.new('ShaderNodeMixRGB');variation.blend_type='MULTIPLY';variation.inputs[0].default_value=.45
    links.new(ramp.outputs['Color'],variation.inputs[1]);links.new(freckles.outputs['Color'],variation.inputs[2])
    attr=n.new('ShaderNodeAttribute');attr.attribute_name='arm_blend'
    col=n.new('ShaderNodeMixRGB');col.name='BAKE_BaseColor'
    links.new(attr.outputs['Fac'],col.inputs[0]);links.new(base,col.inputs[1]);links.new(variation.outputs[0],col.inputs[2])
    links.new(col.outputs[0],bs.inputs['Base Color'])
    rough=n.new('ShaderNodeMixRGB');rough.name='BAKE_Roughness';rough.inputs[2].default_value=(.57,.57,.57,1)
    links.new(attr.outputs['Fac'],rough.inputs[0]);links.new(roughness,rough.inputs[1]);links.new(rough.outputs[0],bs.inputs['Roughness'])
    geom=n.new('ShaderNodeNewGeometry')
    norm=n.new('ShaderNodeMixRGB')
    links.new(attr.outputs['Fac'],norm.inputs[0]);links.new(source_normal,norm.inputs[1]);links.new(geom.outputs['Normal'],norm.inputs[2])
    height=n.new('ShaderNodeMath');height.operation='MULTIPLY'
    links.new(fine.outputs['Fac'],height.inputs[0]);links.new(attr.outputs['Fac'],height.inputs[1])
    bump=n.new('ShaderNodeBump');bump.inputs['Distance'].default_value=.00012;bump.inputs['Strength'].default_value=.22
    links.new(height.outputs[0],bump.inputs['Height']);links.new(norm.outputs[0],bump.inputs['Normal'])
    links.new(bump.outputs['Normal'],bs.inputs['Normal'])
    mat['Forearm_tone_sRGB']=[float(v) for v in color]
