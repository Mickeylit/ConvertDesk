#!/usr/bin/env python3
"""Fetch the document renderer pinned in the inherited, reviewed engine manifest."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import tarfile
import tempfile
import urllib.request
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--runtime', required=True, choices=['osx-arm64', 'osx-x64', 'win-x64'])
parser.add_argument('--destination', required=True, type=Path)
parser.add_argument('--engine', default='rhwp', choices=['rhwp', 'pandoc'])
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
manifest = json.loads((root / 'src/Filee.Engines/Infrastructure/engines.json').read_text())
components = manifest['components'] if args.runtime == 'win-x64' else manifest['platforms'][args.runtime]['components']
component = components[args.engine]
cache = root / 'build/.cache/document-engines'
cache.mkdir(parents=True, exist_ok=True)
archive = cache / (component['sha256'].lower() + '.' + component['kind'])

def checksum(path):
    with path.open('rb') as stream:
        digest = hashlib.sha256()
        for block in iter(lambda: stream.read(1024*1024), b''):
            digest.update(block)
        return digest.hexdigest()

if not archive.exists():
    request = urllib.request.Request(component['url'], headers={'User-Agent': 'ConvertDesk-build/0.2.0'})
    with tempfile.NamedTemporaryFile(dir=cache, suffix='.partial', delete=False) as out:
        pending = Path(out.name)
        with urllib.request.urlopen(request, timeout=90) as response:
            shutil.copyfileobj(response, out)
    if checksum(pending).lower() != component['sha256'].lower():
        raise SystemExit(f'Checksum mismatch; download retained for inspection: {pending}')
    pending.replace(archive)
if checksum(archive).lower() != component['sha256'].lower():
    raise SystemExit(f'Cached archive checksum mismatch: {archive}')

def safe_name(name):
    path = Path(name.replace('\\', '/'))
    if path.is_absolute() or '..' in path.parts or ':' in name:
        raise ValueError(f'Unsafe archive entry: {name}')

with tempfile.TemporaryDirectory(dir=cache) as temporary:
    stage = Path(temporary)
    if zipfile.is_zipfile(archive):
        with zipfile.ZipFile(archive) as bundle:
            links = []
            for member in bundle.infolist():
                safe_name(member.filename)
                if (member.external_attr >> 16) & 0o170000 == 0o120000:
                    links.append(member)
                else:
                    bundle.extract(member, stage)
            # Pandoc's official macOS archive has pandoc-lua/server aliases. Restore only
            # links to regular files already extracted inside this staging directory.
            # No extraction is performed after links exist, and directory links are refused.
            for member in links:
                link = bundle.read(member).decode('utf-8')
                safe_name(link)
                destination = stage / member.filename
                resolved = (destination.parent / link).resolve()
                if stage.resolve() not in resolved.parents or not resolved.is_file():
                    raise ValueError(f'Unsafe archive link: {member.filename}')
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.symlink_to(link)
    else:
        with tarfile.open(archive) as bundle:
            for member in bundle.getmembers():
                safe_name(member.name)
                if not (member.isfile() or member.isdir()):
                    raise ValueError('Archive contains a link or special file')
            bundle.extractall(stage)
    executable = args.engine + ('.exe' if args.runtime == 'win-x64' else '')
    candidates = [path for path in stage.rglob(executable) if path.is_file()]
    if len(candidates) != 1:
        raise SystemExit(f'Expected one {executable}; found {len(candidates)}')
    target = args.destination / args.engine
    if target.exists():
        raise SystemExit(f'Use an empty engine destination: {target}')
    # Preserve the full distribution (Pandoc shares resources beside bin/).
    children = list(stage.iterdir())
    payload = children[0] if len(children) == 1 and children[0].is_dir() else stage
    relative = candidates[0].relative_to(payload)
    shutil.copytree(payload, target, symlinks=True)
    if args.runtime != 'win-x64':
        (target / relative).chmod(0o755)
print(f'{args.engine} {component["version"]} verified and bundled for {args.runtime}')
