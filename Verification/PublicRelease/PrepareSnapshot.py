"""Create a new local repository from a clean, reviewed distribution tree.

No source history, remotes, credentials or ignored local files are copied. This does
not publish anything, change visibility, rewrite the source repository or grant a license.
Run AuditLocalHistory.py against the result before distributing it.
"""
import argparse
from pathlib import Path, PurePosixPath
import subprocess


def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--destination', type=Path, required=True)
    parser.add_argument('--author-name', required=True)
    parser.add_argument('--author-email', required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    destination = args.destination.resolve()
    if destination.exists() or destination.is_relative_to(root):
        raise ValueError('Destination must be a new folder outside the source checkout.')
    if git(root, 'status', '--porcelain').strip():
        raise ValueError('Commit and review source changes before creating a snapshot.')
    allowed = {'.gitattributes', '.gitignore', 'README.md', 'LICENSE', 'LICENSE.md',
               'Packages', 'Verification', 'dist'}
    entries = git(root, 'ls-tree', '-rz', '--full-tree', 'HEAD').split(b'\0')
    files = []
    for entry in entries:
        if not entry:
            continue
        metadata, raw_path = entry.split(b'\t', 1)
        mode, kind, object_id = metadata.decode().split()
        path = PurePosixPath(raw_path.decode('utf-8'))
        if path.parts[0] not in allowed:
            raise ValueError('Review unexpected distribution root: ' + path.parts[0])
        if mode != '100644' or kind != 'blob' or path.is_absolute() or '..' in path.parts:
            raise ValueError('Only regular reviewed distribution files are accepted.')
        files.append((path, object_id))
    destination.mkdir(parents=True)
    for path, object_id in files:
        output = destination.joinpath(*path.parts)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(git(root, 'cat-file', 'blob', object_id))
    git(destination, 'init', '-b', 'main')
    git(destination, 'config', 'user.name', args.author_name)
    git(destination, 'config', 'user.email', args.author_email)
    git(destination, 'add', '--all', '--force')
    git(destination, 'commit', '-m', 'Initial generic Video to Unity distribution')
    assert git(destination, 'rev-list', '--count', '--all').strip() == b'1'
    assert not git(destination, 'remote').strip()
    print(f'Created {len(files)} reviewed files with one fresh local commit and no remote.')


if __name__ == '__main__':
    main()
