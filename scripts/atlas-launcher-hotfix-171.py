#!/usr/bin/env python3
"""Stage or publish only the 1.7.1 client; never restart a service."""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

VERSION = '1.7.1'
ROOT = Path('/opt/atlas-launcher-hotfixes/1.7.1-20260912')
UPLOAD = Path('/tmp/atlas-launcher-hotfix-171-20260912')
PUBLIC = Path('/var/www/wotlk-launcher/launcher')
STORE = Path('/srv/wotlk/launcher-releases')
METADATA = Path('/opt/wotlk-launcher-release')
FEED = Path('/srv/wotlk/launcher-feed/patch-notes.json')
SERVICES = ['arthas-worldserver.dungeon-clear-8224099', 'hermesproxy-wotlk',
            'wotlk-launcher-api', 'arthas-authserver']


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def services():
    return subprocess.check_output(['systemctl', 'show', *SERVICES,
        '-p', 'Id', '-p', 'MainPID', '-p', 'ActiveState', '-p', 'NRestarts'], text=True)


def atomic_copy(source, target, mode=0o644):
    if target.is_symlink() or target.parent.resolve(strict=True) != target.parent:
        raise RuntimeError('Unsafe destination: ' + str(target))
    descriptor, temporary = tempfile.mkstemp(prefix='.atlas171-', dir=target.parent)
    try:
        with os.fdopen(descriptor, 'wb') as out, source.open('rb') as inp:
            shutil.copyfileobj(inp, out)
            out.flush()
            os.fsync(out.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, target)
    finally:
        if os.path.exists(temporary): os.unlink(temporary)


def baseline_paths():
    return [PUBLIC / x for x in ('launcher-update.json', 'WotLK-Launcher.exe',
        'WotLK-Launcher-Installer.exe', 'AtlasLauncherSetup.exe')] + [
        FEED, METADATA / 'current/launcher-update.json', Path('/etc/caddy/Caddyfile')]


def prepare():
    if ROOT.exists(): raise RuntimeError('Preparation already exists; inspect before retry.')
    if json.loads((PUBLIC / 'launcher-update.json').read_text())['version'] != '1.7.0':
        raise RuntimeError('Public version changed')
    ROOT.mkdir(parents=True, mode=0o700)
    expected = json.loads((UPLOAD / 'inputs.json').read_text())
    allowed = {'WotLK-Launcher.exe', 'AtlasLauncherSetup.exe', 'publisher.sh', 'manifest.py',
               'trusted-keys.json', 'patch-note.draft.json', 'patch-note.en.draft.json'}
    if set(expected['files']) != allowed: raise RuntimeError('Unexpected input inventory')
    for name, item in expected['files'].items():
        source, target = UPLOAD / name, ROOT / name
        if source.resolve(strict=True) != source or not source.is_file(): raise RuntimeError('Unsafe input')
        shutil.copyfile(source, target)
        target.chmod(0o600)
        if target.stat().st_size != item['bytes'] or digest(target) != item['sha256']:
            raise RuntimeError('Frozen input differs: ' + name)
    write_json(ROOT / 'inputs.json', expected)
    baseline = {}
    for path in baseline_paths():
        if path.resolve(strict=True) != path: raise RuntimeError('Unsafe baseline')
        backup = ROOT / 'before' / path.relative_to('/')
        backup.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, backup)
        baseline[str(path)] = {'sha256': digest(path), 'uid': path.stat().st_uid,
            'gid': path.stat().st_gid, 'mode': path.stat().st_mode & 0o777}
        if digest(backup) != baseline[str(path)]['sha256']: raise RuntimeError('Backup differs')
    prepared = ROOT / 'prepared'
    env = dict(os.environ, PUBLIC_ROOT=str(prepared / 'public'),
        ARTIFACT_ROOT=str(prepared / 'store'), REPO_ROOT=str(prepared / 'metadata'),
        MANIFEST_TOOL=str(ROOT / 'manifest.py'), ATLAS_LAUNCHER_TRUST_STORE=str(ROOT / 'trusted-keys.json'),
        ATLAS_LAUNCHER_SIGNING_KEY_ID='atlas-prod-p256-2026-01')
    subprocess.run(['bash', str(ROOT / 'publisher.sh'), str(ROOT / 'WotLK-Launcher.exe'),
        str(ROOT / 'AtlasLauncherSetup.exe'), VERSION], env=env, check=True)
    manifest = json.loads((prepared / 'public/launcher-update.json').read_text())
    old_notes = json.loads(FEED.read_text())
    notes = []
    for suffix in ('', '.en'):
        note = json.loads((ROOT / f'patch-note{suffix}.draft.json').read_text())
        if note['id'] != 'atlas-launcher-1-7-1': raise RuntimeError('Unexpected patch note')
        note.pop('version'); note.pop('isDraft'); note['publishedAt'] = manifest['publishedAt']
        notes.append(note)
        markdown = '# ' + note['title'] + '\n\n' + note['summary'] + '\n\n'
        for section in note['sections']:
            markdown += '## ' + section['title'] + '\n\n'
            markdown += ''.join('- ' + item + '\n' for item in section['items']) + '\n'
        for directory in (prepared / 'store/v1.7.1', prepared / 'metadata/releases/v1.7.1'):
            write_json(directory / f'patch-note{suffix}.json', note)
            (directory / f'PATCH-NOTES{suffix}.md').write_text(markdown, encoding='utf-8')
    if any(n['id'] == notes[0]['id'] for n in old_notes): raise RuntimeError('Note already published')
    write_json(prepared / 'patch-notes.next.json', [notes[0], *old_notes])
    armory = STORE / 'v1.7.0/armory-runtime.zip'
    if digest(armory) != '84a57db71c985be18c47f62e5761e21effe7d032a7316e0f69e9edc650a7e645':
        raise RuntimeError('Existing armory changed')
    shutil.copyfile(armory, prepared / 'store/v1.7.1/armory-runtime.zip')
    metadata_path = prepared / 'metadata/releases/v1.7.1/release.json'
    metadata = json.loads(metadata_path.read_text())
    metadata.update(publishedRoot=str(PUBLIC), artifactStore=str(STORE / 'v1.7.1'),
                    sourceCommit=expected['commit'], publishedAt=manifest['publishedAt'])
    write_json(metadata_path, metadata)
    for path, item in baseline.items():
        if digest(path) != item['sha256']: raise RuntimeError('Public baseline changed')
    write_json(ROOT / 'preparation.json', dict(version=VERSION, publicFilesChanged=False,
        baseline=baseline, files={str(p.relative_to(prepared)): digest(p)
        for p in prepared.rglob('*') if p.is_file()}))
    print('PASS: signed 1.7.1 prepared privately; public baseline unchanged.', flush=True)


def immutable(source, target):
    if target.exists() or target.is_symlink(): raise RuntimeError('Immutable destination exists')
    stage = Path(tempfile.mkdtemp(prefix='.atlas171-', dir=target.parent))
    for path in source.rglob('*'):
        if path.is_symlink(): raise RuntimeError('Unexpected symbolic link')
        destination = stage / path.relative_to(source)
        if path.is_dir(): destination.mkdir(mode=0o755)
        else: atomic_copy(path, destination)
    stage.chmod(0o755)
    os.rename(stage, target)


def download(url, expected):
    request = urllib.request.Request(url, headers={'Accept-Encoding': 'identity',
        'Cache-Control': 'no-cache', 'X-WotLK-Launcher-Update': '1'})
    sha, count = hashlib.sha256(), 0
    with urllib.request.urlopen(request, timeout=45) as response:
        if response.status != 200 or response.geturl() != url or response.headers.get('Content-Length') != str(expected['bytes']):
            raise RuntimeError('Unexpected HTTPS response')
        for block in iter(lambda: response.read(1024 * 1024), b''):
            count += len(block)
            if count > expected['bytes']: raise RuntimeError('Download too large')
            sha.update(block)
    if count != expected['bytes'] or sha.hexdigest() != expected['sha256']:
        raise RuntimeError('HTTPS download differs')
    print('PASS: complete HTTPS download verified: ' + url, flush=True)


def publish():
    proof = json.loads((ROOT / 'preparation.json').read_text())
    prepared = ROOT / 'prepared'
    for relative, sha in proof['files'].items():
        if digest(prepared / relative) != sha: raise RuntimeError('Prepared file changed')
    for path, item in proof['baseline'].items():
        if digest(path) != item['sha256']: raise RuntimeError('Baseline changed')
    if shutil.disk_usage(PUBLIC).free < 5_000_000_000: raise RuntimeError('Insufficient free space')
    before_services = services()
    if before_services.count('ActiveState=active') != len(SERVICES): raise RuntimeError('Service inactive')
    old_dns = socket.getaddrinfo
    socket.getaddrinfo = lambda host, port, family=0, type=0, proto=0, flags=0: old_dns(
        host, port, socket.AF_INET if host == 'animeclub.fr' else family, type, proto, flags)
    inputs = json.loads((ROOT / 'inputs.json').read_text())['files']
    manifest = prepared / 'public/launcher-update.json'
    changed = []
    try:
        immutable(prepared / 'public/releases/1.7.1', PUBLIC / 'releases/1.7.1')
        immutable(prepared / 'store/v1.7.1', STORE / 'v1.7.1')
        immutable(prepared / 'metadata/releases/v1.7.1', METADATA / 'releases/v1.7.1')
        for name, input_name in [('WotLK-Launcher.exe', 'WotLK-Launcher.exe'),
                                 ('WotLK-Launcher-Installer.exe', 'AtlasLauncherSetup.exe')]:
            download('https://animeclub.fr/wotlk/launcher/releases/1.7.1/' + name, inputs[input_name])
        operations = [(manifest, METADATA / 'current/launcher-update.json'),
            (prepared / 'public/releases/1.7.1/WotLK-Launcher.exe', PUBLIC / 'WotLK-Launcher.exe'),
            (prepared / 'public/releases/1.7.1/WotLK-Launcher-Installer.exe', PUBLIC / 'WotLK-Launcher-Installer.exe'),
            (prepared / 'public/releases/1.7.1/WotLK-Launcher-Installer.exe', PUBLIC / 'AtlasLauncherSetup.exe'),
            (prepared / 'patch-notes.next.json', FEED), (manifest, PUBLIC / 'launcher-update.json')]
        for path, item in proof['baseline'].items():
            if digest(path) != item['sha256']: raise RuntimeError('Baseline changed before announcement')
        for source, target in operations:
            changed.append(target)
            atomic_copy(source, target)
            if digest(source) != digest(target): raise RuntimeError('Publication differs')
        with urllib.request.urlopen(urllib.request.Request(
            'https://animeclub.fr/wotlk/launcher/launcher-update.json',
            headers={'Cache-Control': 'no-cache'}), timeout=20) as response:
            if response.read(65537) != manifest.read_bytes() or 'no-store' not in response.headers.get('Cache-Control', ''):
                raise RuntimeError('Public manifest differs')
        if services() != before_services: raise RuntimeError('Services changed')
        if digest('/etc/caddy/Caddyfile') != proof['baseline']['/etc/caddy/Caddyfile']['sha256']:
            raise RuntimeError('Caddy changed')
        write_json(ROOT / 'publication.json', dict(published=True, version=VERSION,
            completedAtUnix=int(time.time()), manifestSha256=digest(manifest),
            signedManifestVerified=True, publicDownloadsFullyHashed=True,
            servicesUnchanged=True, historicalNotesPreserved=True,
            launcher=inputs['WotLK-Launcher.exe'], installer=inputs['AtlasLauncherSetup.exe']))
        print('PASS: 1.7.1 public, downloads verified, all service processes unchanged.', flush=True)
    except BaseException:
        for path in reversed(changed):
            info = proof['baseline'][str(path)]
            backup = ROOT / 'before' / path.relative_to('/')
            if digest(backup) != info['sha256']: raise RuntimeError('Rollback backup differs')
            atomic_copy(backup, path, info['mode'])
            os.chown(path, info['uid'], info['gid'])
        raise


if __name__ == '__main__':
    os.umask(0o077)
    if os.geteuid() != 0 or len(sys.argv) != 2 or sys.argv[1] not in ('prepare', 'publish'):
        raise RuntimeError('Run as root with prepare or publish')
    with open('/run/lock/atlas-launcher-client-release.lock', 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        (prepare if sys.argv[1] == 'prepare' else publish)()
