#!/usr/bin/env python3
"""Publish the approved, already tested 1.7.0 client after live backend validation."""
import base64
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import urllib.request
from release_runtime import ROOT, atomic_copy, digest, service, verify_runtime, SERVICES

PUBLIC = Path('/var/www/wotlk-launcher/launcher')
STORE = Path('/srv/wotlk/launcher-releases')
METADATA = Path('/opt/wotlk-launcher-release')
FEED = Path('/srv/wotlk/launcher-feed/patch-notes.json')

def immutable(source, target):
    if source.resolve(strict=True) != source or target.parent.resolve(strict=True) != target.parent or target.is_symlink():
        raise RuntimeError('Unsafe immutable directory')
    files = {str(x.relative_to(source)): digest(x) for x in source.rglob('*') if x.is_file()}
    if any(x.is_symlink() for x in source.rglob('*')): raise RuntimeError('Symlink in source')
    if target.exists():
        if any(x.is_symlink() for x in target.rglob('*')) or {str(x.relative_to(target)): digest(x) for x in target.rglob('*') if x.is_file()} != files:
            raise RuntimeError('Immutable destination differs')
        return
    stage = Path(tempfile.mkdtemp(prefix='.' + target.name + '.atlas170-', dir=target.parent))
    for path in source.rglob('*'):
        destination = stage / path.relative_to(source)
        if path.is_dir(): destination.mkdir(); destination.chmod(0o755)
        else: atomic_copy(path, destination, 0o644)
    stage.chmod(0o755)
    if target.exists(): raise RuntimeError('Immutable target appeared')
    os.rename(stage, target)

def download(url, size, sha):
    request = urllib.request.Request(url, headers={'Accept-Encoding': 'identity', 'Cache-Control': 'no-cache', 'X-WotLK-Launcher-Update': '1'})
    checksum, count = hashlib.sha256(), 0
    with urllib.request.urlopen(request, timeout=45) as response:
        if response.status != 200 or response.geturl() != url or response.headers.get('Content-Length') != str(size):
            raise RuntimeError('Unexpected public download response')
        for block in iter(lambda: response.read(1024 * 1024), b''):
            count += len(block)
            if count > size: raise RuntimeError('Download too large')
            checksum.update(block)
    if count != size or checksum.hexdigest() != sha: raise RuntimeError('Public download hash differs')
    print('PASS: complete HTTPS download verified: ' + url, flush=True)

def main():
    os.umask(0o077)
    if os.geteuid() != 0 or ROOT.resolve(strict=True) != ROOT: raise RuntimeError('Expected prepared root')
    lock = (ROOT / 'maintenance.lock').open('a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    if (ROOT / 'publication-result.json').exists(): raise RuntimeError('Publication already attempted; inspect the recorded state')
    if not json.loads((ROOT / 'live-check-enabled.json').read_text())['passed']: raise RuntimeError('Enabled live check required')
    verify_runtime(True)
    before_services = {name: service(name) for name in SERVICES}
    original_dns = socket.getaddrinfo
    socket.getaddrinfo = lambda host, port, family=0, type=0, proto=0, flags=0: original_dns(host, port,
        socket.AF_INET if host == 'animeclub.fr' else family, type, proto, flags)
    client = ROOT / 'launcher'
    prepared = client / 'prepared'
    proof = json.loads((client / 'proof.json').read_text())
    for relative, sha in proof['preparedFiles'].items():
        if digest(prepared / relative) != sha: raise RuntimeError('Prepared file changed')
    for path, sha in proof['publicBefore'].items():
        if digest(path) != sha: raise RuntimeError('Public baseline changed')
    old_notes = json.loads(FEED.read_text())
    new_notes = json.loads((prepared / 'patch-notes.next.json').read_text())
    if new_notes[1:] != old_notes or new_notes[0]['id'] != 'atlas-launcher-1-7-0': raise RuntimeError('Historical notes differ')
    manifest = prepared / 'public/launcher-update.json'
    trust = json.loads((client / 'tools/trusted-keys.json').read_text())
    anchor = next(x for x in trust['keys'] if x['keyId'] == 'atlas-prod-p256-2026-01')
    public_der = client / 'release-verification-public.der'
    public_pem = client / 'release-verification-public.pem'
    public_der.write_bytes(base64.b64decode(anchor['subjectPublicKeyInfo'], validate=True))
    subprocess.run(['openssl', 'pkey', '-pubin', '-inform', 'DER', '-in', str(public_der), '-out', str(public_pem)], check=True)
    subprocess.run(['python3', str(client / 'tools/manifest.py'), 'verify', '--manifest', str(manifest),
        '--package', str(prepared / 'public/releases/1.7.0/WotLK-Launcher.exe'), '--public-key', str(public_pem),
        '--expected-key-id', 'atlas-prod-p256-2026-01'], check=True)
    if shutil.disk_usage(PUBLIC).free < 5_000_000_000: raise RuntimeError('Insufficient publication headroom')
    ownership = {path: {'uid': Path(path).stat().st_uid, 'gid': Path(path).stat().st_gid,
        'mode': Path(path).stat().st_mode & 0o777, 'sha256': sha} for path, sha in proof['publicBefore'].items()}
    (client / 'public-before-ownership.json').write_text(json.dumps(ownership, indent=2) + '\n')
    changed = []
    def interrupted(signum, frame): raise RuntimeError('Publication interrupted')
    signal.signal(signal.SIGTERM, interrupted); signal.signal(signal.SIGINT, interrupted)
    try:
        immutable(prepared / 'public/releases/1.7.0', PUBLIC / 'releases/1.7.0')
        immutable(prepared / 'store/v1.7.0', STORE / 'v1.7.0')
        launcher, installer = proof['inputs']['WotLK-Launcher.exe'], proof['inputs']['AtlasLauncherSetup.exe']
        download('https://animeclub.fr/wotlk/launcher/releases/1.7.0/WotLK-Launcher.exe', launcher['bytes'], launcher['sha256'])
        download('https://animeclub.fr/wotlk/launcher/releases/1.7.0/WotLK-Launcher-Installer.exe', installer['bytes'], installer['sha256'])
        publish_metadata = client / 'publication-metadata'
        publish_metadata.mkdir()
        metadata = json.loads((prepared / 'metadata/releases/v1.7.0/release.json').read_text())
        metadata.update(publishedRoot=str(PUBLIC), artifactStore=str(STORE / 'v1.7.0'),
            sourceCommit='ee2ea060ed29db30b6a16a70a807fadc17f00172',
            publishedAt=json.loads(manifest.read_text())['publishedAt'])
        (publish_metadata / 'release.json').write_text(json.dumps(metadata, indent=2) + '\n')
        shutil.copyfile(manifest, publish_metadata / 'launcher-update.json')
        immutable(publish_metadata, METADATA / 'releases/v1.7.0')
        for path, sha in proof['publicBefore'].items():
            if digest(path) != sha: raise RuntimeError('Public state changed before announcement')
        verify_runtime(True)
        operations = [
            (manifest, METADATA / 'current/launcher-update.json'),
            (prepared / 'public/releases/1.7.0/WotLK-Launcher.exe', PUBLIC / 'WotLK-Launcher.exe'),
            (prepared / 'public/releases/1.7.0/WotLK-Launcher-Installer.exe', PUBLIC / 'WotLK-Launcher-Installer.exe'),
            (prepared / 'public/releases/1.7.0/WotLK-Launcher-Installer.exe', PUBLIC / 'AtlasLauncherSetup.exe'),
            (prepared / 'patch-notes.next.json', FEED),
            (manifest, PUBLIC / 'launcher-update.json')]
        for source, destination in operations:
            changed.append(destination)
            atomic_copy(source, destination)
        for source, destination in operations:
            if digest(source) != digest(destination): raise RuntimeError('Published file differs')
        if digest('/etc/caddy/Caddyfile') != proof['publicBefore']['/etc/caddy/Caddyfile']: raise RuntimeError('Caddy changed')
        with urllib.request.urlopen(urllib.request.Request('https://animeclub.fr/wotlk/launcher/launcher-update.json',
            headers={'Cache-Control': 'no-cache', 'Accept-Encoding': 'identity'}), timeout=20) as response:
            if response.read(65537) != manifest.read_bytes() or 'no-store' not in response.headers.get('Cache-Control', ''):
                raise RuntimeError('Public manifest bytes or cache policy differ')
        for name, size in (('releases/1.7.0/WotLK-Launcher.exe', launcher['bytes']),
                           ('releases/1.7.0/WotLK-Launcher-Installer.exe', installer['bytes']),
                           ('AtlasLauncherSetup.exe', installer['bytes'])):
            with urllib.request.urlopen(urllib.request.Request('https://animeclub.fr/wotlk/launcher/' + name,
                method='HEAD', headers={'Accept-Encoding': 'identity', 'Cache-Control': 'no-cache', 'X-WotLK-Launcher-Update': '1'}), timeout=20) as response:
                if response.status != 200 or response.headers.get('Content-Length') != str(size): raise RuntimeError('Public HEAD differs')
        subprocess.run(['python3', str(ROOT / 'scripts/check-live.py'), 'published'], check=True, timeout=90)
        after_services = {name: service(name) for name in SERVICES}
        if before_services != after_services: raise RuntimeError('A service changed during client publication')
        result = {'published': True, 'version': '1.7.0', 'completedAtUnix': int(time.time()),
            'launcher': launcher, 'installer': installer, 'manifestSha256': digest(manifest),
            'signedManifestVerified': True, 'publicDownloadsFullyHashed': True, 'publicManifestMatches': True,
            'threeAliasesVerified': True, 'historicalNotesPreserved': True,
            'authenticatedShopAndPatchNotesVerified': True, 'servicesUnchangedDuringPublication': True}
        (ROOT / 'publication-result.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result), flush=True)
    except BaseException as error:
        signal.signal(signal.SIGTERM, signal.SIG_IGN); signal.signal(signal.SIGINT, signal.SIG_IGN)
        failures = []
        public_manifest = PUBLIC / 'launcher-update.json'
        order = ([public_manifest] if public_manifest in changed else []) + [x for x in reversed(changed) if x != public_manifest]
        for path in order:
            try:
                info = ownership[str(path)]
                backup = client / 'public-before' / path.relative_to('/')
                if digest(backup) != info['sha256']: raise RuntimeError('Public rollback backup changed')
                atomic_copy(backup, path, info['mode']); os.chown(path, info['uid'], info['gid'])
            except BaseException as failure: failures.append(str(path) + ':' + type(failure).__name__)
        (ROOT / 'publication-result.json').write_text(json.dumps({'published': False, 'errorType': type(error).__name__,
            'rollbackFailures': failures, 'immutableCandidateRetained': True}, indent=2) + '\n')
        raise

if __name__ == '__main__': main()
