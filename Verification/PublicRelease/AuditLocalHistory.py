"""Read local reachable Git objects and nested release archives without extracting them.

No network calls or Git mutations. Detailed locations go only to the explicitly supplied
private JSON outside the checkout; stdout contains counts, never matched values.
This is a bounded heuristic inventory, not a credential or licensing certification.
"""
import argparse
import collections
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import tarfile
import zipfile

RULES = {
    "machine_path": re.compile(rb"(?i)(?:[a-z]:[\\/]+(?:Users|Program Files|workspace|Projects|build)[\\/]+[^\r\n\"<>]{1,220}|/(?:Users|home)/[^\s\"<>]{1,180})"),
    "credential_candidate": re.compile(rb"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AKIA[0-9A-Z]{16}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|sk-(?:proj-)?[A-Za-z0-9_-]{32,})"),
    "credential_assignment_candidate": re.compile(rb"(?i)(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)\s*[=:]\s*[\"'][^\"'\r\n]{12,}[\"']"),
    "contact_candidate": re.compile(rb"[A-Za-z0-9_.+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"),
}


def git(*args):
    return subprocess.check_output(["git", *args])


def audit(private_output, private_pattern_file=None):
    root = Path(git("rev-parse", "--show-toplevel").decode().strip()).resolve()
    if private_output.resolve().is_relative_to(root):
        raise ValueError("Private detail output must be outside the checkout")
    if private_pattern_file:
        if private_pattern_file.resolve().is_relative_to(root):
            raise ValueError("Private marker configuration must be outside the checkout")
        markers = json.loads(private_pattern_file.read_text(encoding="utf-8-sig"))
        RULES["private_marker"] = re.compile(b"|".join(re.escape(m.encode()) for m in markers), re.I)
    refs = git("for-each-ref", "--format=%(refname) %(objectname)").decode().splitlines()
    commits = git("rev-list", "--all").decode().splitlines()
    objects = git("rev-list", "--objects", "--all").decode(errors="replace").splitlines()
    names = dict(line.split(" ", 1) if " " in line else (line, "") for line in objects)
    # Include local unreachable objects too; their presence does not imply public reachability.
    types = git("cat-file", "--batch-all-objects", "--batch-check=%(objectname) %(objecttype) %(objectsize)").decode().splitlines()
    findings, archives, media, failures = [], [], [], []
    counts = collections.Counter()
    seen_archives = set()

    def scan(data, location, oid, depth=0):
        counts["payloads_read"] += 1
        counts["payload_bytes_read"] += len(data)
        for label, pattern in RULES.items():
            matches = list(pattern.finditer(data))
            if matches:
                # Never retain credential values, even in private output.
                findings.append({"rule": label, "object": oid, "location": location,
                                 "count": len(matches),
                                 "lines": sorted(set(data[:m.start()].count(b"\n") + 1 for m in matches))[:100]})
        lower = location.lower()
        if lower.endswith((".mp4", ".mov", ".webm", ".png", ".jpg", ".jpeg", ".wav", ".mp3")):
            media.append({"location": location, "object": oid, "bytes": len(data),
                          "sha256": hashlib.sha256(data).hexdigest()})
        archive_kind = "zip" if data.startswith(b"PK\x03\x04") else "tar" if (
            data.startswith(b"\x1f\x8b") or (len(data) > 262 and data[257:262] == b"ustar")) else None
        if not archive_kind:
            return
        digest = hashlib.sha256(data).hexdigest()
        archives.append({"location": location, "object": oid, "sha256": digest, "bytes": len(data)})
        if digest in seen_archives:
            counts["duplicate_archive_payloads"] += 1
            return
        seen_archives.add(digest)
        if depth >= 8:
            failures.append({"location": location, "reason": "archive nesting limit"})
            return
        try:
            if archive_kind == "zip":
                with zipfile.ZipFile(io.BytesIO(data)) as arc:
                    for member in arc.infolist():
                        if not member.is_dir():
                            scan(arc.read(member), location + "!" + member.filename, oid, depth + 1)
            else:
                with tarfile.open(fileobj=io.BytesIO(data), mode="r:*") as arc:
                    for member in arc:
                        if member.isfile():
                            scan(arc.extractfile(member).read(), location + "!" + member.name, oid, depth + 1)
        except (OSError, ValueError, tarfile.TarError, zipfile.BadZipFile, RuntimeError) as error:
            failures.append({"location": location, "reason": type(error).__name__})

    batch = subprocess.Popen(["git", "cat-file", "--batch"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    for record in types:
        oid, kind, size = record.split()
        if kind not in ("blob", "commit", "tag"):
            continue
        batch.stdin.write((oid + "\n").encode())
        batch.stdin.flush()
        header = batch.stdout.readline().decode().split()
        data = batch.stdout.read(int(header[2]))
        assert len(data) == int(size) and batch.stdout.read(1) == b"\n"
        counts[kind + "s_read"] += 1
        scan(data, names.get(oid) or kind, oid)
    batch.stdin.close()
    assert batch.wait() == 0
    # Enumerate every historical tree so a blob's first path cannot hide other locations.
    historical_paths = collections.defaultdict(set)
    for commit in commits:
        for item in git("ls-tree", "-rz", "--full-tree", commit).split(b"\0"):
            if item:
                metadata, path = item.split(b"\t", 1)
                oid = metadata.decode().split()[2]
                historical_paths[oid].add(path.decode(errors="replace"))
    summary = dict(counts)
    summary.update(refs=len(refs), commits=len(commits), unique_archives=len(seen_archives),
                   reachable_objects=len(names), total_local_objects=len(types),
                   findings=dict(collections.Counter(f["rule"] for f in findings)),
                   failures=len(failures), media_payloads=len(media))
    result = {"summary": summary, "head": git("rev-parse", "HEAD").decode().strip(),
              "refs": refs, "commits": commits, "archives": archives, "media": media,
              "findings": findings, "failures": failures,
              "historical_paths": {oid: sorted(paths) for oid, paths in historical_paths.items()}}
    private_output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, sort_keys=True))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--private-output", type=Path, required=True)
    parser.add_argument("--private-pattern-file", type=Path)
    args = parser.parse_args()
    audit(args.private_output, args.private_pattern_file)
