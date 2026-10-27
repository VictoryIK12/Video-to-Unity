"""Real FFmpeg fixtures + the unmodified production C# converter; no Unity process.
Run: python validate.py [--quick] [--main-fixtures PATH_TO_BASIC].
Requires Python 3, FFmpeg/FFprobe and .NET 9 SDK.
All generated data stays in this owned folder's ignored Build/ directory.
"""
import json
import hashlib
import math
import pathlib
import shutil
import struct
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent
BUILD = ROOT / 'Build'
BUILD.mkdir(exist_ok=True)
RUN = BUILD / ('run-' + __import__('uuid').uuid4().hex[:12])
RUN.mkdir()
FFMPEG = shutil.which('ffmpeg')
FFPROBE = shutil.which('ffprobe')
DOTNET = shutil.which('dotnet')
assert FFMPEG and FFPROBE and DOTNET

def run(args, **kwargs):
    result = subprocess.run([str(a) for a in args], capture_output=True, **kwargs)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}): {args}\n{result.stderr.decode('utf-8', errors='replace')[-20000:]}")
    return result.stdout

def ff(args, **kwargs):
    return run([FFMPEG, '-hide_banner', '-v', 'error', '-nostdin', '-n'] + args, **kwargs)

def probe(path):
    return json.loads(run([FFPROBE, '-v', 'error', '-show_streams', '-show_frames', '-of', 'json', path]))

def decode(path, alpha=False, packed=False, color=False):
    args = []
    if path.suffix == '.webm': args += ['-c:v', 'libvpx']
    args += ['-i', path]
    if alpha:
        vf = ('crop=iw/2:ih:iw/2:0:exact=1,scale=in_range=limited:out_range=full,format=gray'
              if packed else 'alphaextract,scale=in_range=full:out_range=full,format=gray')
        pix = 'gray'
    else:
        vf = 'format=rgb24' if not packed else 'format=rgb24,crop=iw/2:ih:0:0:exact=1'
        pix = 'rgb24'
    return ff(args + ['-vf', vf, '-pix_fmt', pix, '-fps_mode', 'passthrough', '-f', 'rawvideo', 'pipe:1'])

def pixels(w, h, frames, opaque=False):
    data = bytearray()
    for f in range(frames):
        for y in range(h):
            for x in range(w):
                # Asymmetric quadrants/corners, full-width gray steps, moving hole and one-pixel marks.
                rgb = ((220, 35, 40), (25, 210, 60), (40, 65, 235), (210, 170, 25))[(y >= h//2)*2 + (x >= w//2)]
                a = (x * 255 // (w-1) + f * 7) % 256
                if x < 12 and y < 12: a = 0
                if x >= w-12 and y < 12: a = 64
                if x < 12 and y >= h-12: a = 128
                if x >= w-12 and y >= h-12: a = 255
                if x == 18+f and y == 17: a = 255
                if x == w-18-f and y == h-17: a = 0
                data.extend((*rgb, 255 if opaque else a))
    return data

def fixture(name, codec, w=96, h=64, frames=12, rate='24000/1001', opaque=False, filters=None, rgba=None):
    path = RUN / name
    args = ['-f', 'rawvideo', '-pix_fmt', 'rgba', '-s', f'{w}x{h}', '-r', rate, '-i', 'pipe:0', '-an']
    if filters: args += ['-vf', filters]
    if codec == 'prores': args += ['-c:v', 'prores_ks', '-profile:v', '4', '-alpha_bits', '16', '-pix_fmt', 'yuva444p10le']
    elif codec == 'vp8': args += ['-c:v', 'libvpx', '-pix_fmt', 'yuva420p', '-auto-alt-ref', '0', '-crf', '4', '-b:v', '0']
    else: args += ['-c:v', 'libx264', '-pix_fmt', 'yuv420p']
    args += ['-fps_mode', 'passthrough', '-threads', '2', path]
    ff(args, input=pixels(w,h,frames,opaque) if rgba is None else rgba)
    return path

project = BUILD / 'Harness.csproj'
project.write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><LangVersion>9.0</LangVersion></PropertyGroup><ItemGroup><Compile Include="../../VideoConverter.cs"/><Compile Include="../Harness.cs"/></ItemGroup></Project>''')
print(run([DOTNET, 'build', project, '-c', 'Release', '-o', BUILD / 'host', '--nologo']).decode(), flush=True)
HOST = [DOTNET, BUILD / 'host' / 'Harness.dll']
results = []
outputs = {}

def matrix(fmt):
    return 'bt709' if fmt == 'packed' else 'bt470bg'

def canonical_rgb(src, fmt):
    args = ['-c:v', 'libvpx'] if src.suffix == '.webm' else []
    pix = 'yuv420p' if fmt == 'packed' else 'yuva420p'
    vf = f'scale=out_color_matrix={matrix(fmt)}:out_range=limited,format={pix},scale=in_color_matrix={matrix(fmt)}:in_range=limited:out_range=full,format=rgb24'
    return ff(args + ['-i', src, '-vf', vf, '-fps_mode', 'passthrough', '-f', 'rawvideo', 'pipe:1'])

def check_color_headers(src, dest, fmt):
    source_stream = probe(src)['streams']
    source_stream = next(s for s in source_stream if s['codec_type'] == 'video')
    transfer = source_stream.get('color_transfer', 'bt709')
    if transfer == 'unknown': transfer = 'bt709'
    expected = {'color_space': matrix(fmt), 'color_range': 'tv', 'color_primaries': 'bt709', 'color_transfer': transfer}
    data = probe(dest)
    for item in [data['streams'][0]] + data['frames']:
        assert {k:item.get(k) for k in expected} == expected, (dest, expected, item)
    if fmt == 'packed':
        # Independently inspect MP4 colr, then probe the raw H.264 SPS without container tags.
        blob = dest.read_bytes(); at = blob.index(b'colr') + 4
        assert blob[at:at+4] == b'nclx'
        values = struct.unpack('>HHHB', blob[at+4:at+11])
        assert values == (1, {'bt709':1, 'iec61966-2-1':13}[transfer], 1, 0), values
        raw = dest.with_suffix('.h264')
        ff(['-i', dest, '-map', '0:v:0', '-frames:v', '1', '-c:v', 'copy', '-bsf:v', 'h264_mp4toannexb', '-f', 'h264', raw])
        header = probe(raw)['streams'][0]
        assert {k:header.get(k) for k in expected} == expected, header
    return expected

def convert(source, fmt, mode='run', stage='', expected_failure=None):
    scratch = RUN / ('scratch-' + __import__('uuid').uuid4().hex[:8]); scratch.mkdir()
    destination = RUN / (source.stem + '-' + fmt + '-' + __import__('uuid').uuid4().hex[:6] + ('.mp4' if fmt == 'packed' else '.webm'))
    args = HOST + [mode, source, scratch, FFMPEG, FFPROBE, fmt, stage if mode != 'run' else destination]
    result = subprocess.run([str(a) for a in args], capture_output=True, timeout=120)
    output = (result.stdout + result.stderr).decode('utf-8', errors='replace')
    if expected_failure:
        assert result.returncode != 0 and expected_failure in output, output
        assert not destination.exists()
    else:
        assert result.returncode == 0, output
    assert not list(scratch.iterdir()), f"Leaked scratch files: {list(scratch.iterdir())}"
    print(('EXPECTED REJECTION: ' if expected_failure else '') + output.splitlines()[0], flush=True)
    results.append({'case': source.name + ':' + fmt + ':' + mode + ':' + stage, 'expected_failure': expected_failure, 'output': output[:2000]})
    if mode == 'run' and not expected_failure:
        results[-1]['verified_color_metadata'] = check_color_headers(source, destination, fmt)
        expected_rgb = canonical_rgb(source, fmt)
        actual_rgb = decode(destination, packed=fmt=='packed')
        assert len(expected_rgb) == len(actual_rgb)
        errors = [abs(a-b) for a,b in zip(expected_rgb,actual_rgb)]
        assert max(errors) <= 16 and sum(errors)/len(errors) <= 2, (max(errors), sum(errors)/len(errors))
        results[-1]['rgb_max_error'] = max(errors)
        results[-1]['rgb_mean_error'] = sum(errors)/len(errors)
        results[-1]['source_sha256'] = hashlib.sha256(source.read_bytes()).hexdigest()
        results[-1]['output_sha256'] = hashlib.sha256(destination.read_bytes()).hexdigest()
    return destination

source = fixture('색상 & 알파 (원본) ; $ 시험.mov', 'prores')
webm = fixture('직접 생성한 VP8 alpha.webm', 'vp8')
for src in [source, webm]:
    original_alpha = decode(src, alpha=True)
    assert min(original_alpha) == 0 and max(original_alpha) == 255
    for fmt in ['packed', 'webm']:
        dest = convert(src, fmt)
        outputs[(src, fmt)] = dest
        actual_alpha = decode(dest, alpha=True, packed=fmt=='packed')
        assert len(actual_alpha) == len(original_alpha)
        errors = [abs(a-b) for a,b in zip(original_alpha,actual_alpha)]
        assert max(errors) <= 16 and sum(errors)/len(errors) <= 2
        pp = probe(dest)
        assert len(pp['frames']) == 12
        assert len(pp['streams']) == 1 and pp['streams'][0]['codec_type'] == 'video'
        assert pp['streams'][0]['width'] == (192 if fmt=='packed' else 96)
        # Distinct corners across all decoded frames, from decoded SOURCE rather than ideal RGBA.
        for f in range(12):
            for x,y in [(2,2),(93,2),(2,61),(93,61),(18+f,17),(78-f,47)]:
                i=f*96*64+y*96+x
                assert abs(original_alpha[i]-actual_alpha[i]) <= 16
        if fmt == 'packed':
            blob = dest.read_bytes()
            assert blob.index(b'moov') < blob.index(b'mdat'), 'faststart missing'
        results[-1]['alpha_max_error'] = max(errors)
        results[-1]['alpha_mean_error'] = sum(errors)/len(errors)
        results[-1]['decoded_frames'] = len(pp['frames'])
        results[-1]['source_sha256'] = hashlib.sha256(src.read_bytes()).hexdigest()
        results[-1]['output_sha256'] = hashlib.sha256(dest.read_bytes()).hexdigest()
        print(f'Independent alpha/corner/structure check: max={max(errors)}, mean={sum(errors)/len(errors):.5f}', flush=True)

opaque = fixture('opaque-with-alpha-channel.mov', 'prores', opaque=True)
convert(opaque, 'packed', expected_failure='opaque')
plain = fixture('opaque-no-alpha.mp4', 'h264')
convert(plain, 'packed', expected_failure='failed')
odd = fixture('odd-geometry.mov', 'prores', w=95, h=63)
convert(odd, 'packed', expected_failure='even source')
convert(source, 'packed', mode='cancel')
vfr = fixture('variable-framerate.mov', 'prores', rate='30', filters='setpts=PTS+floor(N/3)*2')
for fmt in ['packed', 'webm']:
    convert(vfr, fmt)
single = fixture('one-frame.mov', 'prores', frames=1, rate='30000/1001')
for fmt in ['packed', 'webm']:
    convert(single, fmt)

offset = RUN / 'nonzero-video-start.mov'
ff(['-itsoffset', '1.25', '-i', source, '-c', 'copy', offset])
convert(offset, 'packed')
audio = RUN / 'audio-first-video-second.mov'
ff(['-i', source, '-f', 'lavfi', '-i', 'sine=frequency=440:duration=1', '-map', '1:a:0', '-map', '0:v:0', '-c:v', 'copy', '-c:a', 'pcm_s16le', '-shortest', audio])
audio_dest = convert(audio, 'packed')
assert len(probe(audio_dest)['streams']) == 1
convert(source, 'packed', mode='parallel')

fake = RUN / 'metadata-only-alpha.webm'
ff(['-i', plain, '-c:v', 'libvpx', '-pix_fmt', 'yuv420p', '-metadata:s:v:0', 'alpha_mode=1', fake])
assert {k.lower(): v for k,v in probe(fake)['streams'][0].get('tags', {}).items()}.get('alpha_mode') == '1'
convert(fake, 'packed', expected_failure='failed')
rotated = RUN / 'rotated.mov'
ff(['-display_rotation', '90', '-i', source, '-c', 'copy', rotated])
assert probe(rotated)['streams'][0]['side_data_list'][0]['rotation'] == 90
convert(rotated, 'packed', expected_failure='Rotation/display-transform')
sar = fixture('non-square-pixels.mov', 'prores', filters='setsar=2')
convert(sar, 'packed', expected_failure='Non-square')
hdr = RUN / 'hdr-metadata.mov'
ff(['-i', source, '-vf', 'setparams=color_trc=smpte2084:color_primaries=bt2020', '-c:v', 'prores_ks', '-profile:v', '4', '-pix_fmt', 'yuva444p10le', '-color_trc', 'smpte2084', '-color_primaries', 'bt2020', hdr])
assert probe(hdr)['streams'][0]['color_transfer'] == 'smpte2084'
convert(hdr, 'packed', expected_failure='Export SDR')
other_ext = RUN / 'real-alpha.anyextension'
other_ext.write_bytes(source.read_bytes())
convert(other_ext, 'packed')

# Prove matching extrema and plausible geometry are insufficient: mirror every matte pixel,
# damage one non-corner pixel in a middle frame, and mirror only the color half.
reference_alpha = RUN / 'reference.alpha'
reference_alpha.write_bytes(decode(source, alpha=True))
reference_color = RUN / 'reference.rgb'
reference_color.write_bytes(canonical_rgb(source, 'packed'))
good = outputs[(source, 'packed')]
for kind in ['mirror-matte', 'single-pixel-middle-frame', 'mirror-color']:
    left = 'hflip' if kind == 'mirror-color' else 'null'
    right = 'hflip' if kind == 'mirror-matte' else "drawbox=x=53:y=29:w=1:h=1:color=white:t=fill:enable='eq(n,6)'" if kind == 'single-pixel-middle-frame' else 'null'
    corrupt = RUN / (kind + '.mp4')
    graph = f'[0:v]split[l][r];[l]crop=96:64:0:0,{left}[c];[r]crop=96:64:96:0,{right}[a];[c][a]hstack[out]'
    ff(['-i', good, '-filter_complex', graph, '-map', '[out]', '-c:v', 'libx264', '-crf', '0', '-pix_fmt', 'yuv420p', '-color_range', 'tv', '-colorspace', 'bt709', corrupt])
    mode = 'color' if kind == 'mirror-color' else 'alpha'
    ref = reference_color if mode == 'color' else reference_alpha
    log = run(HOST + ['verify', corrupt, RUN, FFMPEG, FFPROBE, mode, ref, '96', '64']).decode()
    print(log.strip(), flush=True)
    results.append({'case': kind, 'output': log})

# A caller that tries to copy onto an existing file must fail without modifying the source.
before = hashlib.sha256(source.read_bytes()).hexdigest()
scratch = RUN / 'no-overwrite'; scratch.mkdir()
failed_copy = subprocess.run([str(a) for a in HOST + ['run', source, scratch, FFMPEG, FFPROBE, 'packed', source]], capture_output=True)
assert failed_copy.returncode != 0
assert hashlib.sha256(source.read_bytes()).hexdigest() == before and not list(scratch.iterdir())
results.append({'case': 'existing-source-not-overwritten', 'output': 'PASS'})
print('PASS existing source preserved on copy collision', flush=True)

# Saturated RGB and midtone regressions: independently inspect decoded SOURCE patch colors,
# not only a reference constructed using the same YUV conversion as the encoder.
swatches = [(255,0,0,128),(0,255,0,255),(0,0,255,64),(128,128,128,255),
            (64,128,192,128),(255,0,255,255),(0,255,255,128),(220,160,40,255)]
swatch_bytes = bytes(c for f in range(12) for y in range(64) for x in range(128) for c in swatches[x//16])
missing_tags = fixture('709-matrix-missing-primaries-transfer.mov', 'prores', w=128, rgba=swatch_bytes,
    filters='scale=out_color_matrix=bt709:out_range=limited,format=yuva444p10le,setparams=colorspace=bt709:color_primaries=unknown:color_trc=unknown')
source_meta = probe(missing_tags)['streams'][0]
assert source_meta['color_space'] == 'bt709' and source_meta.get('color_primaries', 'unknown') == 'unknown' and source_meta.get('color_transfer', 'unknown') == 'unknown'
srgb = fixture('tagged-srgb709.mov', 'prores', w=128, rgba=swatch_bytes,
    filters='scale=out_color_matrix=bt709:out_range=limited,format=yuva444p10le,setparams=colorspace=bt709:color_primaries=bt709:color_trc=iec61966-2-1')
assert probe(srgb)['streams'][0]['color_transfer'] == 'iec61966-2-1'

def swatch_check(src, dest, fmt, points):
    # Decode source independently with its decoder metadata and no forced matrix.
    before, after = decode(src), decode(dest, packed=fmt=='packed')
    info = next(s for s in probe(src)['streams'] if s['codec_type']=='video')
    width, height = info['width'], info['height']
    frames = len(before)//(width*height*3)
    maximum = 0; patches = []
    for frame in range(frames):
        for x,y in points:
            at = ((frame*height+y)*width+x)*3
            a,b = list(before[at:at+3]),list(after[at:at+3])
            maximum = max(maximum, *[abs(c-d) for c,d in zip(a,b)])
            if frame == 0: patches.append({'xy':[x,y],'source_rgb':a,'output_rgb':b})
    assert maximum <= 4, (src,fmt,maximum,patches)
    results[-1]['source_rgb_patch_max_error'] = maximum
    results[-1]['first_frame_rgb_patches'] = patches
    print('PASS decoded-source RGB patches: max error',maximum,flush=True)

for src in [missing_tags, srgb]:
    for fmt in ['packed','webm']:
        dest = convert(src,fmt); outputs[(src,fmt)] = dest
        swatch_check(src,dest,fmt,[(x*16+8,32) for x in range(8)])
for fmt in ['packed','webm']:
    dest = convert(outputs[(missing_tags,'webm')],fmt)
    swatch_check(outputs[(missing_tags,'webm')],dest,fmt,[(x*16+8,32) for x in range(8)])

# A wrong-matrix VP8 has correct-looking BT.601 tags yet deliberately contains BT.709 YUV.
# The unforced decoded RGB check MUST reject it, even though YUV bytes look plausible.
wrong_matrix = RUN / '709-values-falsely-tagged-601.webm'
ff(['-i', missing_tags, '-vf', 'scale=out_color_matrix=bt709:out_range=limited,format=yuva420p,setparams=colorspace=bt470bg:range=limited:color_primaries=bt709:color_trc=bt709',
    '-c:v','libvpx','-crf','4','-b:v','0','-auto-alt-ref','0', '-colorspace','bt470bg','-color_primaries','bt709','-color_trc','bt709',wrong_matrix])
reference_601 = RUN/'reference-601.rgb'; reference_601.write_bytes(canonical_rgb(missing_tags,'webm'))
log = run(HOST+['verify',wrong_matrix,RUN,FFMPEG,FFPROBE,'color-webm',reference_601,'128','64']).decode()
assert 'Color differs from source' in log
results.append({'case':'709-YUV-mistagged-as601-rejected-by-decoded-RGB','output':log})
print(log.strip(),flush=True)

# Remove SPS primaries/transfer. Then spoof only MP4 colr to prove container metadata alone
# cannot pass the production independent bitstream check.
no_tags = RUN/'h264-missing-sps-colour.mp4'
ff(['-i',outputs[(missing_tags,'packed')],'-c:v','copy','-bsf:v','h264_metadata=colour_primaries=2:transfer_characteristics=2',
    '-color_primaries','unknown','-color_trc','unknown','-movflags','+write_colr',no_tags])
scratch=RUN/'metadata-reject';scratch.mkdir()
log=run(HOST+['verify-metadata',missing_tags,scratch,FFMPEG,FFPROBE,'packed',no_tags]).decode()
results.append({'case':'missing-H264-primaries-transfer-rejected','output':log});print(log.strip(),flush=True)
spoofed=RUN/'valid-colr-missing-sps.mp4'
blob=bytearray(no_tags.read_bytes());at=blob.index(b'colr')+4
assert blob[at:at+4]==b'nclx';blob[at+4:at+11]=struct.pack('>HHHB',1,1,1,0);spoofed.write_bytes(blob)
assert probe(spoofed)['streams'][0]['color_primaries']=='bt709'
scratch=RUN/'bitstream-reject';scratch.mkdir()
log=run(HOST+['verify-bitstream',missing_tags,scratch,FFMPEG,FFPROBE,'packed',spoofed]).decode()
results.append({'case':'valid-container-missing-H264-SPS-tags-rejected','output':log});print(log.strip(),flush=True)

if '--main-fixtures' in sys.argv:
    basic=pathlib.Path(sys.argv[sys.argv.index('--main-fixtures')+1])
    # Read-only source snapshot: do not edit main's generated media or generator.
    original=basic/'Source~/source-alpha.mov'; snapshot=RUN/'main-diagnostic-source.mov'
    with snapshot.open('xb') as out: out.write(original.read_bytes())
    for fmt in ['packed','webm']:
        dest=convert(snapshot,fmt)
        swatch_check(snapshot,dest,fmt,[(16,32),(48,32),(80,32),(112,32)])
if '--quick' not in sys.argv:
    long_source = fixture('cancel-test.mov', 'prores', w=320, h=180, frames=120, rate='30')
    for stage in ['Checking every', 'Decoding real', 'Decoding full', 'Encoding', 'Verifying output', 'Comparing the complete', 'Comparing every']:
        convert(long_source, 'packed', mode='cancel', stage=stage)
    convert(long_source, 'webm', mode='dispose', stage='Encoding')

(RUN / 'results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'PASS {len(results)} cases; evidence: {RUN}', flush=True)
