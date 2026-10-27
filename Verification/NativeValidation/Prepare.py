"""Prepare only an explicitly marked, repository-local disposable Unity test project."""
from pathlib import Path
import argparse
import json
import re
import shutil

root = Path(__file__).resolve().parents[2]
package = root / 'Packages/com.victoryik.video-to-unity'
parser = argparse.ArgumentParser()
parser.add_argument('--name', required=True)
parser.add_argument('--generation', required=True, choices=['2022', '6'])
parser.add_argument('--initialize', action='store_true', help='Mark this new disposable verification project.')
parser.add_argument('--installed', action='store_true', help='Dependencies are already installed; refresh test copies now.')
args = parser.parse_args()
if not re.fullmatch(r'[A-Za-z0-9_-]+', args.name):
    raise SystemExit('Project name must be a simple directory name, without separators.')
project = (root / '.validation' / args.name).resolve()
project.relative_to((root / '.validation').resolve())
marker = project / '.video-to-unity-validation'
if not (project / 'ProjectSettings/ProjectVersion.txt').is_file():
    raise SystemExit('First create the isolated project using Unity -createProject.')
if not marker.exists():
    if not args.initialize:
        raise SystemExit('Missing dedicated-validation marker; no changes made.')
    marker.write_text('Disposable Video to Unity verification project.\n', encoding='utf-8')
versions = ('1.0.0', '1.1.33') if args.generation == '2022' else ('2.0.0', '1.6.0')
bootstrap = (Path(__file__).parent / 'V3PackageBootstrap.cs.txt').read_text(encoding='utf-8')
bootstrap = bootstrap.replace('PACKAGE_PATH', package.as_posix()).replace('UGUI_VERSION', versions[0]).replace('TEST_VERSION', versions[1])
editor = project / 'Assets/Editor'
editor.mkdir(parents=True, exist_ok=True)
(editor / 'V3PackageBootstrap.cs').write_text(bootstrap, encoding='utf-8')
for kind, source_name, test_name, folder in ([] if not args.installed else [
    ('Editor', 'VideoToUnity.Tests', 'VideoToUnity.Validation.Native', 'VideoToUnityNativeTests'),
    ('PlayMode', 'VideoToUnity.PlayMode.Tests', 'VideoToUnity.Validation.PlayMode', 'VideoToUnityPlayModeTests'),
]):
    destination = project / 'Assets' / folder
    destination.mkdir(parents=True, exist_ok=True)
    for source in (package / 'Tests' / kind).glob('*.cs'):
        shutil.copyfile(source, destination / source.name)
    assembly = json.loads((package / 'Tests' / kind / (source_name + '.asmdef')).read_text(encoding='utf-8-sig'))
    assembly['name'] = test_name
    (destination / (test_name + '.asmdef')).write_text(json.dumps(assembly, indent=2), encoding='utf-8')
streaming = project / 'Assets/StreamingAssets/VideoToUnity'
streaming.mkdir(parents=True, exist_ok=True)
shutil.copyfile(package / 'Samples~/Basic/Media/packed-alpha.mp4', streaming / 'packed-alpha.mp4')
print('Prepared canonical tests/media in', args.name, '(package installation uses Unity PackageManager.Client).')
