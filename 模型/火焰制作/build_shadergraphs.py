"""Generate editable URP Shader Graph assets with an explicit custom-function node.
Deterministic IDs keep the graph and Unity references stable between rebuilds.
"""
import hashlib,json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
OUT=ROOT/'HMP_game/Assets/VFX'
def uid(s):return hashlib.md5(('hmprotection.fire.v1.'+s).encode()).hexdigest()
def ref(s):return {'m_Id':s}
def generate(smoke=False):
    name='SG_SmokeFlipbook' if smoke else 'SG_FireFlipbook'
    objects=[];nodes=[];edges=[];properties=[]
    def obj(kind,key,**kw):
        o={'m_SGVersion':0,'m_Type':kind,'m_ObjectId':uid(name+key),**kw};objects.append(o);return o
    def slot(key,index,label,typ='Vector4',output=False,value=None):
        o=obj('UnityEditor.ShaderGraph.'+typ+'MaterialSlot',key,m_Id=index,m_DisplayName=label,m_SlotType=1 if output else 0,m_Hidden=False,m_ShaderOutputName=label,m_StageCapability=3)
        if typ.startswith('Vector'):
            n=int(typ[-1]);v=0. if n==1 else dict(zip('xyzw'[:n],[0.]*n))
            o.update(m_Value=v if value is None else value,m_DefaultValue=v,m_Labels=[])
        if typ=='Texture2D':o['m_BareResource']=False
        return o
    def node(kind,key,label,slots,**kw):
        o=obj('UnityEditor.ShaderGraph.'+kind,key,m_Group=ref(''),m_Name=label,
              m_DrawState={'m_Expanded':True,'m_Position':{'serializedVersion':'2','x':-600+len(nodes)*130,'y':0,'width':180,'height':140}},
              m_Slots=[ref(s['m_ObjectId']) for s in slots],synonyms=[],m_Precision=1,m_PreviewExpanded=False,m_DismissedVersion=0,m_PreviewMode=0,m_CustomColors={'m_SerializableColors':[]},**kw)
        nodes.append(ref(o['m_ObjectId']));return o
    def edge(a,ai,b,bi):edges.append({'m_OutputSlot':{'m_Node':ref(a['m_ObjectId']),'m_SlotId':ai},'m_InputSlot':{'m_Node':ref(b['m_ObjectId']),'m_SlotId':bi}})
    def prop(key,label,typ,value):
        o=obj('UnityEditor.ShaderGraph.Internal.'+typ+'ShaderProperty',key,m_Guid={'m_GuidSerialized':uid(key)},m_Name=label,
              m_DefaultRefNameVersion=1,m_RefNameGeneratedByDisplayName=label,m_DefaultReferenceName=key,m_OverrideReferenceName=key,
              m_GeneratePropertyBlock=True,m_UseCustomSlotLabel=False,m_CustomSlotLabel='',m_DismissedVersion=0,m_Precision=1,
              overrideHLSLDeclaration=False,hlslDeclarationOverride=0,m_Hidden=False,m_Value=value)
        if typ=='Texture2D':o.update(isMainTexture=True,useTilingAndOffset=False,useTexelSize=True,m_Modifiable=True,m_DefaultType=0)
        if typ=='Vector1':o.update(m_FloatType=0,m_RangeValues={'x':0,'y':1});o['m_SGVersion']=1
        properties.append(ref(o['m_ObjectId']))
        return node('PropertyNode',key+'node',label,[slot(key+'out',0,'Out',typ,True)],m_Property=ref(o['m_ObjectId']))
    atlas=prop('_MainTex','Baked 8 x 8 atlas','Texture2D',{'m_SerializedTexture':'','m_Guid':''})
    gain=prop('_Intensity','Emission / smoke gain','Vector1',1. if smoke else 2.)
    distort=prop('_Distortion','UV turbulence','Vector1',.012)
    soft=prop('_SoftDistance','Intersection softness (m)','Vector1',.04)
    uv=node('UVNode','uv','Particle UV + age + seed',[slot('uvout',0,'Out',output=True)],m_OutputChannel=0)
    color=node('VertexColorNode','color','Particle tint and lifetime opacity',[slot('colorout',0,'Out',output=True)])
    pos=node('PositionNode','pos','World position',[slot('posout',0,'Out','Vector3',True)],m_Space=2,m_PositionSource=0);pos['m_SGVersion']=1
    params=[('Atlas','Texture2D'),('UV','Vector4'),('Tint','Vector4'),('PositionWS','Vector3'),('Gain','Vector1'),('Distortion','Vector1'),('SoftDistance','Vector1'),('RGB','Vector3'),('Alpha','Vector1')]
    fn=node('CustomFunctionNode','function','Baked flame: frame blend, turbulence, soft intersection',
            [slot('fn'+str(i),i,n,t,i>=7) for i,(n,t) in enumerate(params)],
            m_SourceType=0,m_FunctionName='FireFlipbook',m_FunctionSource=uid('FireFlipbook.hlsl'),m_FunctionBody='')
    fn['m_SGVersion']=1
    for index,n in enumerate([atlas,uv,color,pos,gain,distort,soft]):edge(n,0,fn,index)
    blocks=[]
    for label,typ,out in [('BaseColor','Vector3',7),('Alpha','Vector1',8)]:
        n=node('BlockNode','block'+label,'SurfaceDescription.'+label,[slot('blockslot'+label,0,label,typ)],m_SerializedDescriptor='SurfaceDescription.'+label)
        blocks.append(ref(n['m_ObjectId']));edge(fn,out,n,0)
    target=obj('UnityEditor.Rendering.Universal.ShaderGraph.UniversalTarget','target',m_Datas=[],m_ActiveSubTarget=ref(uid(name+'subtarget')),m_AllowMaterialOverride=False,m_SurfaceType=1,m_ZTestMode=4,m_ZWriteControl=2,m_AlphaMode=0 if smoke else 2,m_RenderFace=0,m_AlphaClip=False,m_CastShadows=False,m_ReceiveShadows=False,m_DisableTint=False,m_AdditionalMotionVectorMode=0,m_AlembicMotionVectors=False,m_SupportsLODCrossFade=False,m_CustomEditorGUI='',m_SupportVFX=False)
    target['m_SGVersion']=1
    sub=obj('UnityEditor.Rendering.Universal.ShaderGraph.UniversalUnlitSubTarget','subtarget');sub['m_SGVersion']=2
    category=obj('UnityEditor.ShaderGraph.CategoryData','category',m_Name='',m_ChildObjectList=properties)
    graph={'m_SGVersion':3,'m_Type':'UnityEditor.ShaderGraph.GraphData','m_ObjectId':uid(name+'graph'),
           'm_Properties':properties,'m_Keywords':[],'m_Dropdowns':[],'m_CategoryData':[ref(category['m_ObjectId'])],
           'm_Nodes':nodes,'m_GroupDatas':[],'m_StickyNoteDatas':[],'m_Edges':edges,
           'm_VertexContext':{'m_Position':{'x':0,'y':0},'m_Blocks':[]},
           'm_FragmentContext':{'m_Position':{'x':1000,'y':0},'m_Blocks':blocks},
           'm_PreviewData':{'serializedMesh':{'m_SerializedMesh':'{"mesh":{"instanceID":0}}','m_Guid':''},'preventRotation':False},
           'm_Path':'HMProtection/Fire','m_GraphPrecision':1,'m_PreviewMode':2,'m_OutputNode':ref(''),'m_SubDatas':[],
           'm_ActiveTargets':[ref(target['m_ObjectId'])]}
    (OUT/(name+'.shadergraph')).write_text('\n\n'.join(json.dumps(o,indent=4) for o in [graph]+objects),encoding='utf-8')
    (OUT/(name+'.shadergraph.meta')).write_text('fileFormatVersion: 2\nguid: '+uid(name)+'\n',encoding='utf-8')
for smoke in [False,True]:generate(smoke)
(OUT/'FireFlipbook.hlsl.meta').write_text('fileFormatVersion: 2\nguid: '+uid('FireFlipbook.hlsl')+'\n',encoding='utf-8')
