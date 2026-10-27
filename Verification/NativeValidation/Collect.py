"""Collect portable, per-case evidence from completed native Unity Test Runner XML."""
from pathlib import Path
import hashlib
import json
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
package = root / 'Packages/com.victoryik.video-to-unity'
records = []
for project in ['Unity2022', 'Unity6']:
    folder = root / '.validation' / project
    lock = json.loads((folder / 'Packages/packages-lock.json').read_text(encoding='utf-8-sig'))['dependencies']
    packages = {name: lock[name]['version'] for name in ['com.unity.ugui', 'com.unity.timeline', 'com.unity.test-framework']}
    version = (folder / 'ProjectSettings/ProjectVersion.txt').read_text().splitlines()[0].split(': ',1)[1]
    for space in ['gamma', 'linear']:
        for mode in ['editmode', 'playmode']:
            path = folder / f'final-{space}-{mode}.xml'
            report = ET.parse(path).getroot()
            assert int(report.attrib['failed']) == 0 and int(report.attrib['skipped']) == 0, path
            cases = [{'name': case.attrib['fullname'], 'result': case.attrib['result'],
                      'duration_seconds': float(case.attrib.get('duration',0))} for case in report.findall('.//test-case')]
            expected = 82 if mode == 'editmode' else 5
            assert len(cases) == expected and all(case['result'] == 'Passed' for case in cases), path
            records.append({'unity':version, 'pipeline':'Built-in', 'color_space':space, 'mode':mode,
                            'packages':packages,'passed':len(cases),'failed':0,'skipped':0,
                            'native_xml_sha256':hashlib.sha256(path.read_bytes()).hexdigest(),
                            'started_utc':report.attrib.get('start-time'), 'ended_utc':report.attrib.get('end-time'),
                            'cases':cases})
hashes = {}
for source in package.rglob('*'):
    relative = source.relative_to(package).as_posix()
    if source.is_file() and (source.suffix in ['.cs','.shader','.uss','.asmdef'] or source.suffix in ['.mp4','.webm','.mov']):
        if '/Validation~/' in relative or '/Verification~/' in relative:
            continue
        data = source.read_bytes()
        if source.suffix not in ['.mp4','.webm','.mov']:
            data = data.replace(b'\r\n',b'\n')
        hashes[relative] = hashlib.sha256(data).hexdigest()
evidence = {'date':'2026-10-27','platform':'Windows','gpu':'NVIDIA GeForce RTX 5080','api':'Direct3D11',
            'computer_use':False,'native_batch':True,'passed':sum(item['passed'] for item in records),
            'failed':0,'skipped':0,'runs':records,'source_sha256_text_normalized_lf':hashes}
destination = Path(__file__).parent / 'evidence.json'
destination.write_text(json.dumps(evidence,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
print(f"PASS {evidence['passed']} native test executions across {len(records)} runs; portable evidence saved.")
