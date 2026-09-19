"""Export a Unity-facing copy; retain Blender's +Y-forward authoring scene."""
import math
import bpy
from mathutils import Matrix
def export_unity_fbx(root, rig, meshes):
    # Unity's FBX conversion places Blender +Y behind the camera and flips X.
    # Bake a 180-degree Z rotation into temporary mesh and skeleton datablocks.
    # This keeps the Unity prefab root at identity without changing the .blend.
    objects=[rig]+meshes
    original=[o.data for o in objects]
    temporary=[]
    turn=Matrix.Rotation(math.pi,4,'Z')
    try:
        for o in objects:
            data=o.data.copy()
            data.transform(turn)
            o.data=data
            o.update_tag()
            temporary.append(data)
        bpy.context.view_layer.update()
        bpy.ops.object.select_all(action='DESELECT')
        for o in objects:o.select_set(True)
        bpy.context.view_layer.objects.active=rig
        bpy.ops.export_scene.fbx(filepath=str(root/'SK_FPHands_01.fbx'),use_selection=True,
            object_types={'MESH','ARMATURE'},apply_unit_scale=True,
            apply_scale_options='FBX_SCALE_UNITS',global_scale=1,
            axis_forward='-Z',axis_up='Y',use_mesh_modifiers=True,mesh_smooth_type='FACE',
            add_leaf_bones=False,use_armature_deform_only=True,bake_anim=False,
            path_mode='RELATIVE',embed_textures=False,use_custom_props=False)
    finally:
        for o,data in zip(objects,original):o.data=data;o.update_tag()
        for data in temporary:
            if isinstance(data,bpy.types.Armature):bpy.data.armatures.remove(data)
            else:bpy.data.meshes.remove(data)
        bpy.context.view_layer.update()
