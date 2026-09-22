"""Copy the authored assets preserving existing Unity GUIDs; set URP texture maps.
The Unity editor setup performs the same mapping on later redeployments.
"""
from pathlib import Path
import re, shutil, uuid, json
repo=Path(__file__).resolve().parents[2]
src=repo/'模型/办公室场景'
assets=repo/'HMP_game/Assets/Props'
shutil.copy2(src/'SM_Extinguisher_01.fbx',assets/'Models/SM_Extinguisher_01.fbx')
template=(assets/'Textures/T_Extinguisher_01_Part00_BaseColor.jpg.meta').read_text(encoding='utf8')
guids={}
for p in sorted((src/'灭火器贴图').glob('*.png')):
 dst=assets/'Textures'/p.name; shutil.copy2(p,dst); meta=Path(str(dst)+'.meta')
 if meta.exists(): guid=re.search(r'guid: (\w+)',meta.read_text()).group(1)
 else: guid=uuid.uuid5(uuid.NAMESPACE_URL,'hmp/extinguisher/'+p.name).hex
 text=re.sub(r'guid: \w+',f'guid: {guid}',template,count=1)
 text=re.sub(r'(sRGBTexture:) \d+',r'\g<1> 0',text)
 text=re.sub(r'(textureType:) \d+',r'\g<1> '+('1' if '_Normal.' in p.name else '0'),text)
 text=re.sub(r'(convertToNormalMap:) \d+',r'\g<1> 0',text)
 text=re.sub(r'(alphaIsTransparency:) \d+',r'\g<1> 0',text)
 meta.write_text(text,encoding='utf8'); guids[p.stem]=guid
for n in range(11):
 p=assets/f'Materials/MAT_Extinguisher_01_Part{n:02d}.mat'; text=p.read_text(encoding='utf8')
 text=re.sub(r'  m_ValidKeywords:.*?(?=  m_InvalidKeywords:)',
  '  m_ValidKeywords:\n  - _NORMALMAP\n  - _METALLICSPECGLOSSMAP\n  - _OCCLUSIONMAP\n',text,flags=re.S)
 for prop,kind in (('_BumpMap','Normal'),('_OcclusionMap','AO'),('_MetallicGlossMap','MetallicSmoothness')):
  guid=guids[f'T_Extinguisher_01_Part{n:02d}_{kind}']
  text=re.sub(r'(- '+prop+r':\s+ m_Texture:) \{[^}]*\}',r'\g<1> {fileID: 2800000, guid: '+guid+', type: 3}',text)
 for prop,value in (('_Metallic','1'),('_Smoothness','1'),('_BumpScale','1'),('_OcclusionStrength','0.8'),('_SmoothnessTextureChannel','0')):
  text=re.sub(r'(- '+prop+r':) [^\n]+',r'\g<1> '+value,text)
 p.write_text(text,encoding='utf8')
print('Installed model,',len(guids),'PBR textures, 11 URP materials; existing model/material GUIDs preserved.')
