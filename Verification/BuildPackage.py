"""Build deterministic UPM ZIP/tarball archives and verify their bytes against source."""
import gzip
import hashlib
import io
import json
import pathlib
import re
import tarfile
import uuid
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
PACKAGE = ROOT / "Packages/com.victoryik.video-to-unity"
SKIP = {"build", "temp", "out", "__pycache__", ".git", ".validation"}

def included(path):
    return not any(part.lower() in SKIP for part in path.relative_to(PACKAGE).parts)

def main():
    # Supply stable GUIDs for imported assets and sample contents. Preserve all existing GUIDs.
    for path in sorted(PACKAGE.rglob("*")):
        relative = path.relative_to(PACKAGE)
        if not included(path) or path.suffix == ".meta" or path.name.startswith("."):
            continue
        ignored = [part for part in relative.parts if part.endswith("~")]
        if ignored and not (ignored == ["Samples~"] and len(relative.parts) > 1):
            continue
        meta = path.with_name(path.name + ".meta")
        if not meta.exists():
            guid = uuid.uuid5(uuid.NAMESPACE_URL, "https://github.com/VictoryIK12/Video-to-Unity/" + relative.as_posix()).hex
            meta.write_text("fileFormatVersion: 2\nguid: " + guid + ("\nfolderAsset: yes\n" if path.is_dir() else "\n"), encoding="utf-8")
    files = [p for p in sorted(PACKAGE.rglob("*")) if p.is_file() and included(p)]
    text_extensions = {".cs", ".meta", ".md", ".json", ".asmdef", ".shader", ".uss", ".uxml", ".py", ".ps1"}
    for path in files:
        if path.suffix in text_extensions or path.name == ".gitignore":
            path.write_bytes(path.read_bytes().replace(b"\r\n", b"\n"))
    guids = set()
    for path in files:
        if path.suffix != ".meta":
            continue
        guid = re.search(r"^guid: ([a-f0-9]{32})$", path.read_text(encoding="utf-8"), re.M)
        assert guid and guid[1] not in guids, "Missing/duplicate GUID: " + str(path)
        assert path.with_suffix("").exists(), "Orphan metadata: " + str(path)
        guids.add(guid[1])
    manifest = json.loads((PACKAGE / "package.json").read_text(encoding="utf-8-sig"))
    version = manifest["version"]
    data = {p.relative_to(PACKAGE).as_posix(): p.read_bytes() for p in files}
    for name in data:
        assert not name.endswith((".dll", ".exe", ".pdb")), "Do not bundle build outputs: " + name
    output = ROOT / "dist"
    output.mkdir(exist_ok=True)
    zpath = output / ("VideoToUnity-" + version + ".zip")
    tpath = output / (manifest["name"] + "-" + version + ".tgz")
    with zipfile.ZipFile(zpath, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, content in data.items():
            info = zipfile.ZipInfo(manifest["name"] + "/" + name, date_time=(2026, 10, 27, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, content)
    with tpath.open("wb") as stream, gzip.GzipFile(fileobj=stream, mode="wb", filename="", mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as archive:
            for name, content in data.items():
                info = tarfile.TarInfo("package/" + name)
                info.size = len(content); info.mode = 0o644; info.mtime = 0
                archive.addfile(info, io.BytesIO(content))
    with zipfile.ZipFile(zpath) as archive:
        assert archive.testzip() is None
        assert len(archive.namelist()) == len(data)
        for name, content in data.items():
            assert archive.read(manifest["name"] + "/" + name) == content
    with tarfile.open(tpath, "r:gz") as archive:
        assert len(archive.getmembers()) == len(data)
        for name, content in data.items():
            assert archive.extractfile("package/" + name).read() == content
    hashes = {name: hashlib.sha256(content).hexdigest() for name, content in data.items()}
    report = {"version": version, "files": len(data), "source_sha256": hashes,
              "archives": {p.name: {"bytes": p.stat().st_size, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in (zpath, tpath)}}
    (ROOT / "Verification/package-manifest.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"verified_files": len(data), "unique_guids": len(guids), "archives": report["archives"]}, indent=2))

if __name__ == "__main__":
    main()
