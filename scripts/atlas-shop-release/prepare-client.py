#!/usr/bin/env python3
"""Sign and stage 1.7.0 under the private release root; never publish it."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
from prepare_backend import ROOT, digest, digest_bytes

PUBLIC = Path('/var/www/wotlk-launcher/launcher')
FEED = Path('/srv/wotlk/launcher-feed/patch-notes.json')
METADATA = Path('/opt/wotlk-launcher-release')
ARMORY_SHA = '84a57db71c985be18c47f62e5761e21effe7d032a7316e0f69e9edc650a7e645'

def main():
    os.umask(0o077)
    if os.geteuid() != 0 or ROOT.resolve(strict=True) != ROOT:
        raise RuntimeError('Expected the prepared root, as root.')
    stage = ROOT / 'launcher'
    if stage.exists(): raise RuntimeError('Client preparation already exists; inspect before retry.')
    baseline_paths = [PUBLIC / name for name in ('launcher-update.json', 'WotLK-Launcher.exe',
        'WotLK-Launcher-Installer.exe', 'AtlasLauncherSetup.exe')]
    baseline_paths += [FEED, METADATA / 'current/launcher-update.json', Path('/etc/caddy/Caddyfile')]
    baseline = {}
    for path in baseline_paths:
        if path.resolve(strict=True) != path or not path.is_file(): raise RuntimeError('Unexpected public path')
        baseline[str(path)] = digest(path)
    old_manifest = json.loads((PUBLIC / 'launcher-update.json').read_text())
    if old_manifest['version'] != '1.6.0': raise RuntimeError('Public version changed')
    files = json.loads(Path('/tmp/atlas-shop-170-client-files.json').read_text())
    with tarfile.open('/tmp/atlas-shop-170-client.tar') as archive:
        members = archive.getmembers()
        if len(members) != len(files) or {x.name for x in members} != set(files):
            raise RuntimeError('Client inventory differs')
        for item in members:
            if not item.isfile() or item.name.startswith('/') or '..' in Path(item.name).parts:
                raise RuntimeError('Unsafe client archive entry')
            if item.size != files[item.name]['bytes'] or digest_bytes(archive.extractfile(item).read()) != files[item.name]['sha256']:
                raise RuntimeError('Client archive hash differs')
        stage.mkdir(mode=0o700)
        for item in members:
            target = stage / item.name
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(item) as source, target.open('wb') as output:
                shutil.copyfileobj(source, output)
    publisher = stage / 'tools/publisher.sh'
    publisher.write_bytes(publisher.read_bytes().replace(b'\r\n', b'\n'))
    prepared = stage / 'prepared'
    env = os.environ.copy()
    env.update(PUBLIC_ROOT=str(prepared / 'public'), ARTIFACT_ROOT=str(prepared / 'store'),
        REPO_ROOT=str(prepared / 'metadata'), MANIFEST_TOOL=str(stage / 'tools/manifest.py'),
        ATLAS_LAUNCHER_TRUST_STORE=str(stage / 'tools/trusted-keys.json'),
        ATLAS_LAUNCHER_SIGNING_KEY_ID='atlas-prod-p256-2026-01')
    subprocess.run(['bash', str(publisher), str(stage / 'WotLK-Launcher.exe'),
        str(stage / 'AtlasLauncherSetup.exe'), '1.7.0'], env=env, check=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    manifest = prepared / 'public/launcher-update.json'
    document = json.loads(manifest.read_text())
    old_notes = json.loads(FEED.read_text())
    note = json.loads((stage / 'patch-note.draft.json').read_text())
    if any(x['id'] == note['id'] for x in old_notes): raise RuntimeError('Note already published')
    note.pop('isDraft'); note.pop('version')
    note['publishedAt'] = document['publishedAt']
    (prepared / 'patch-notes.next.json').write_text(json.dumps([note, *old_notes], ensure_ascii=False, indent=2) + '\n')
    armory = Path('/srv/wotlk/launcher-releases/v1.6.0/armory-runtime.zip')
    if digest(armory) != ARMORY_SHA: raise RuntimeError('Reused armory changed')
    shutil.copy2(armory, prepared / 'store/v1.7.0/armory-runtime.zip')
    backup = stage / 'public-before'
    for path in baseline_paths:
        target = backup / path.relative_to('/')
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        if digest(target) != baseline[str(path)] or digest(path) != baseline[str(path)]:
            raise RuntimeError('Public baseline changed while preparing')
    proof = {'version': '1.7.0', 'published': False, 'signedManifestVerified': True,
        'manifestSha256': digest(manifest), 'inputs': files, 'publicBefore': baseline,
        'armoryRuntimeSha256': ARMORY_SHA, 'historicalNotesPreserved': True,
        'preparedFiles': {str(x.relative_to(prepared)): digest(x) for x in prepared.rglob('*') if x.is_file()}}
    (stage / 'proof.json').write_text(json.dumps(proof, indent=2) + '\n')
    print('PASS: exact client signed with the embedded production key; private staging only; public files unchanged.')
    print(json.dumps({key: proof[key] for key in ('version', 'published', 'signedManifestVerified', 'manifestSha256')}))

if __name__ == '__main__': main()
