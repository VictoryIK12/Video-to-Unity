"""Opaque synthetic input clips for background extraction; no user media included."""
from pathlib import Path
import argparse
import subprocess
import shutil

root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--output',type=Path,default=root/'Packages/com.victoryik.video-to-unity/Samples~/BackgroundRemoval')
target=parser.parse_args().output
target.mkdir(parents=True,exist_ok=True)
ffmpeg=shutil.which('ffmpeg')
assert ffmpeg
w,h,count=160,96,24
for name,background in [('flat-black.mp4',(0,0,0)),('flat-green.mp4',(0,180,40))]:
    output=target/name
    if output.exists(): raise SystemExit(f'Refusing to replace {output}')
    raw=bytearray()
    for frame in range(count):
        for y in range(h):
            for x in range(w):
                rgb=background
                if 50<=x<110 and 27<=y<69:
                    rgb=(0,0,0) if 53<=x<107 and 30<=y<66 else (245,245,245)
                if 8+frame*3<=x<18+frame*3 and 8<=y<19: rgb=(245,30,10)
                radius=((x-130)**2+(y-18)**2)**.5
                if radius<13:
                    strength=(1-radius/13)*.65
                    rgb=tuple(round(background[c]*(1-strength)+(35,100,245)[c]*strength) for c in range(3))
                raw.extend((*rgb,255))
    command=[ffmpeg,'-hide_banner','-v','error','-nostdin','-n','-f','rawvideo','-pix_fmt','rgba','-s','160x96','-framerate','12','-i','pipe:0',
        '-vf','scale=in_range=full:out_range=limited:out_color_matrix=bt709,format=yuv420p,setparams=range=limited:color_primaries=bt709:color_trc=iec61966-2-1:colorspace=bt709',
        '-c:v','libx264','-crf','4','-preset','medium','-x264-params','colorprim=bt709:transfer=iec61966-2-1:colormatrix=bt709',
        '-color_primaries','bt709','-color_trc','iec61966-2-1','-colorspace','bt709','-movflags','+faststart+write_colr',str(output)]
    subprocess.run(command,input=raw,check=True)
    print(name,output.stat().st_size,'bytes; 160x96, 24 opaque frames, 12fps')
