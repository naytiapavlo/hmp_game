"""Pack rendered RGBA passes into 8x8 atlases with shared framing and edge gutters."""
import argparse, hashlib, json
from pathlib import Path
import numpy as np
from PIL import Image, ImageFilter, ImageDraw
p=argparse.ArgumentParser();p.add_argument('--bakes',required=True);p.add_argument('--output',required=True)
a=p.parse_args();root=Path(a.bakes);out=Path(a.output);out.mkdir(parents=True,exist_ok=True)
report={}
for tier in ['Small','Medium','Large','SmokeOnly']:
    for layer in (['smoke'] if tier=='SmokeOnly' else ['flame','smoke']):
        paths=[root/tier/layer/f'{n:04}.png' for n in range(25,89)]
        if not all(f.exists() for f in paths):
            print('WAITING',tier,layer);continue
        frames=[Image.open(f).convert('RGBA') for f in paths]
        boxes=[im.getchannel('A').point(lambda x:255 if x>8 else 0).getbbox() for im in frames]
        assert all(boxes),f'Empty render: {tier}/{layer}'
        x0=min(b[0] for b in boxes);y0=min(b[1] for b in boxes);x1=max(b[2] for b in boxes);y1=max(b[3] for b in boxes)
        width=x1-x0;height=y1-y0
        atlas=Image.new('RGBA',(2048,2048));thumbs=[]
        for n,im in enumerate(frames):
            im=im.crop((x0,y0,x1,y1));rgba=np.array(im,dtype=np.float32)/255
            yy,xx=np.mgrid[0:height,0:width];u=(xx+.5)/width;v=(yy+.5)/height
            def smooth(x):x=np.clip(x,0,1);return x*x*(3-2*x)
            # Gas grids end at a box boundary. Fade that boundary before sprite export.
            if layer=='smoke':
                fade=smooth(u/.18)*smooth((1-u)/.18)*smooth(v/.32)*smooth((1-v)/.10)
                rgba[:,:,3]*=fade
            else:
                rgba[:,:,3]*=smooth(u/.07)*smooth((1-u)/.07)*smooth(v/.06)*smooth((1-v)/.025)
            im=Image.fromarray(np.uint8(np.clip(rgba*255,0,255)),'RGBA')
            target=(max(1,round(width*236/max(width,height))),max(1,round(height*236/max(width,height))))
            im=im.resize(target,Image.Resampling.LANCZOS)
            tile=Image.new('RGBA',(256,256));tile.paste(im,((256-target[0])//2,256-10-target[1]))
            atlas.paste(tile,((n%8)*256,(n//8)*256))
            if n in (0,8,16,24,32,40,48,56):thumbs.append(tile)
        name=f'T_{"Fire" if layer=="flame" else "Smoke"}_{tier}_8x8'
        atlas.save(out/(name+'.png'))
        meta=out/(name+'.png.meta')
        if not meta.exists():meta.write_text('fileFormatVersion: 2\nguid: '+hashlib.md5(name.encode()).hexdigest()+'\n')
        preview=Image.new('RGB',(1024,512),(38,40,43))
        for i,tile in enumerate(thumbs):preview.paste(tile,((i%4)*256,(i//4)*256),tile.getchannel('A'))
        preview.save(root/tier/(layer+'-contact.jpg'),quality=93)
        report[name]={'frames':64,'tiles':[8,8],'size':[2048,2048],'crop':[x0,y0,x1,y1],
                      'nonempty':True,'unique_frames':len({hashlib.sha256(f.tobytes()).hexdigest() for f in frames})}
(out/'FlipbookManifest.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
