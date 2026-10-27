"""Rebuild tiny diagnostic media from RGBA, then run the production converter.
Requires Python 3, FFmpeg/FFprobe and .NET SDK 9+. Never overwrites samples.
"""
import json
import pathlib
import shutil
import subprocess
import uuid
import argparse

ROOT = pathlib.Path(__file__).resolve().parents[1]
PACKAGE = ROOT / "Packages/com.victoryik.video-to-unity"
options = argparse.ArgumentParser()
options.add_argument('--output', type=pathlib.Path, default=PACKAGE / 'Samples~/Basic')
sample_root = options.parse_args().output
DEST = sample_root / "Media"
SOURCE_DEST = sample_root / "Source~"
WORK = ROOT / ".validation" / ("fixtures-" + uuid.uuid4().hex[:10])

def run(args):
    proc = subprocess.run([str(x) for x in args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.returncode:
        raise RuntimeError(proc.stdout + proc.stderr)
    return proc.stdout.strip()

def main():
    names = ["source-alpha.mov", "native-alpha.webm", "packed-alpha.mp4", "black-background.mp4"]
    for name in names:
        destination = (SOURCE_DEST if name == names[0] else DEST) / name
        if destination.exists():
            raise FileExistsError("Refusing to overwrite " + str(destination))
    WORK.mkdir(parents=True)
    rgba, black = bytearray(), bytearray()
    for frame in range(24):
        for y in range(64):
            for x in range(128):
                color = [(0, 0, 0, 0), (0, 0, 0, 255), (255, 0, 0, 128), (0, 255, 0, 255)][x // 32]
                if y >= 56 and (frame * 5) % 120 <= x < (frame * 5) % 120 + 8:
                    color = (0, 0, 255, 255)
                rgba.extend(color)
                black.extend([round(c * color[3] / 255) for c in color[:3]])
    (WORK / "source.rgba").write_bytes(rgba)
    (WORK / "black.rgb").write_bytes(black)
    ffmpeg, ffprobe = shutil.which("ffmpeg"), shutil.which("ffprobe")
    if not ffmpeg or not ffprobe:
        raise RuntimeError("FFmpeg and FFprobe must be on PATH")
    common = [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "rawvideo"]
    run(common + ["-pixel_format", "rgba", "-video_size", "128x64", "-framerate", "12", "-i", WORK / "source.rgba",
                  "-vf", "scale=in_range=full:out_range=limited:out_color_matrix=bt709,format=yuva444p10le,setparams=range=limited:color_primaries=bt709:color_trc=iec61966-2-1:colorspace=bt709",
                  "-c:v", "prores_ks", "-profile:v", "4", "-pix_fmt", "yuva444p10le", "-alpha_bits", "16",
                  "-color_primaries", "bt709", "-colorspace", "bt709", "-color_trc", "iec61966-2-1", WORK / names[0]])
    run(common + ["-pixel_format", "rgb24", "-video_size", "128x64", "-framerate", "12", "-i", WORK / "black.rgb",
                  "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "12", "-movflags", "+faststart", WORK / names[3]])
    # Build the unchanged converter and its standalone host, with explicit source inclusion.
    import xml.sax.saxutils
    escape = xml.sax.saxutils.escape
    (WORK / "Host.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
        '</PropertyGroup><ItemGroup><Compile Include="' + escape(str(PACKAGE / "Editor/Conversion/VideoConverter.cs"))
        + '"/><Compile Include="' + escape(str(PACKAGE / "Editor/Conversion/Validation~/Harness.cs"))
        + '"/></ItemGroup></Project>', encoding="utf-8")
    run(["dotnet", "build", WORK / "Host.csproj", "--nologo", "--verbosity", "quiet", "-o", WORK / "bin"])
    temp = WORK / "jobs"
    temp.mkdir()
    for mode, name in [("packed", names[2]), ("webm", names[1])]:
        print(run(["dotnet", WORK / "bin/Host.dll", "convert", WORK / names[0], temp, ffmpeg, ffprobe, mode, WORK / name]))
    DEST.mkdir(parents=True, exist_ok=True)
    SOURCE_DEST.mkdir(parents=True, exist_ok=True)
    for name in names:
        destination = (SOURCE_DEST if name == names[0] else DEST) / name
        with destination.open("xb") as target:
            target.write((WORK / name).read_bytes())
    print("Created four 128x64, 24-frame, 12-fps diagnostic files (packed width: 256).")

if __name__ == "__main__":
    main()
