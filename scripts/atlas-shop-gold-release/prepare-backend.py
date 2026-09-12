#!/usr/bin/env python3
"""Stage API, configurations and reviewed overrides; never activate production."""
import hashlib
import json
import os
from pathlib import Path
import pwd
import shutil
import subprocess
import tarfile

ROOT = Path('/opt/atlas-shop-releases/gold-1.7.2-20260912')
WORLD = Path('/opt/arthas-next/candidates/atlas-shop-rename-gold-20260912')
API = Path('/opt/wotlk-launcher-api-releases/shop-gold-1.7.2-20260912')
SUFFIX = 'zzzzzz-atlas-shop-native-172.conf'


def digest(path):
    with Path(path).open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    os.umask(0o077)
    if os.geteuid() != 0: raise RuntimeError('Root is required for private staging.')
    if any(path.exists() or path.is_symlink() for path in (ROOT, API, WORLD / 'server/etc-production')):
        raise RuntimeError('Preparation already exists; inspect it before retrying.')
    if WORLD.resolve(strict=True) != WORLD or API.parent.resolve(strict=True) != API.parent:
        raise RuntimeError('Unsafe candidate path.')
    baseline = json.loads(Path('/tmp/atlas-shop-gold-live.json').read_text(encoding='utf-8-sig'))
    for name, state in baseline['services'].items():
        current = subprocess.check_output(['systemctl', 'show', name, '-p', 'MainPID', '--value'], text=True).strip()
        if current != state['MainPID'] or digest('/proc/' + current + '/exe') != state['ExecutableSha256']:
            raise RuntimeError('Live service identity changed since preflight.')
        for path, sha in state['UnitFileSha256'].items():
            if digest(path) != sha: raise RuntimeError('Live service definition changed.')
    if baseline['world']['options']['CharacterDatabase.WorkerThreads'] != '4':
        raise RuntimeError('Expected the reviewed four-worker World configuration.')
    if [int(row[0]) for row in baseline['database']['migrationHistory']] != list(range(1, 14)):
        raise RuntimeError('Expected schema 0013 before this release.')
    ROOT.mkdir(parents=True, mode=0o700); API.mkdir(mode=0o755)
    API.chmod(0o755)  # The private process umask must not block the service user.
    files = {}
    with tarfile.open('/tmp/atlas-shop-gold-fixture.tar.gz') as archive:
        for item in archive.getmembers():
            if not item.name.startswith('api-gold/'): continue
            relative = item.name.removeprefix('api-gold/')
            target = API / relative
            if not item.isfile() or API not in target.resolve().parents or item.issym() or item.islnk():
                raise RuntimeError('Unsafe API archive entry.')
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.extractfile(item).read())
            target.chmod(0o555 if target.name == 'WotLK.Launcher.Server' else 0o444)
            files[relative] = digest(target)
    if 'WotLK.Launcher.Server' not in files: raise RuntimeError('Missing API executable.')
    api_group = pwd.getpwnam('wotlklauncher').pw_gid
    for path, sha in baseline['api']['configurationFiles'].items():
        if digest(path) != sha: raise RuntimeError('Active API configuration changed.')
        target = API / Path(path).name
        shutil.copyfile(path, target); os.chown(target, 0, api_group); target.chmod(0o640)
        files.pop(target.name, None)
    production = WORLD / 'server/etc-production'
    production.mkdir(mode=0o750); (production / 'modules').mkdir(mode=0o750)
    source = Path(baseline['world']['configPath'])
    if digest(source) != baseline['world']['configSha256']: raise RuntimeError('Active World config changed.')
    shutil.copy2(source, production / 'worldserver.conf')
    for name, sha in baseline['world']['moduleConfigHashes'].items():
        module = source.parent.resolve(strict=True) / 'modules' / name
        if not module.is_file() or module.is_symlink() or digest(module) != sha: raise RuntimeError('Active module configuration changed.')
        shutil.copy2(module, production / 'modules' / name)
    (production / 'modules/mod_atlas_shop.conf').write_text('[worldserver]\nAtlasShop.Enable = 1\nAtlasShop.AccountServices = 1\nAtlasShop.GoldConversion = 1\n')
    group = pwd.getpwnam('acore').pw_gid
    for directory in (WORLD, WORLD / 'build', WORLD / 'server', WORLD / 'server/bin', production, production / 'modules'):
        os.chown(directory, 0, group); directory.chmod(0o750)
    for path in production.rglob('*'):
        if path.is_file(): os.chown(path, 0, group); path.chmod(0o640)
    os.chown(WORLD / 'build/worldserver', 0, group); (WORLD / 'build/worldserver').chmod(0o550)
    for path in API.rglob('*'):
        if path.is_dir(): path.chmod(0o755)
    operations = ROOT / 'operations'; operations.mkdir(mode=0o700)
    env = ('WOTLK_LAUNCHER_MAX_SCHEMA_VERSION=14\nAtlasShop__Purchases__AccountServicesEnabled=true\n'
        'AtlasShop__Purchases__RenameEnabled=false\nAtlasShop__Purchases__RealmId=1\n'
        'AtlasShop__GoldConversion__Enabled=false\nAtlasShop__GoldConversion__RealmId=1\n')
    (operations / 'api.env').write_text(env)
    (operations / 'api-enabled.env').write_text(env.replace('Enabled=false', 'Enabled=true'))
    world_service = next(name for name in baseline['services'] if name.startswith('arthas-worldserver'))
    overrides = {
        world_service: '[Service]\nExecStart=\nExecStart=' + str(WORLD / 'server/bin/worldserver') + ' --config ' + str(WORLD / 'server/etc/worldserver.conf') + '\n',
        'wotlk-launcher-api': '[Service]\nWorkingDirectory=' + str(API) + '\nExecStart=\nExecStart=' + str(API / 'WotLK.Launcher.Server') + '\nEnvironmentFile=' + str(operations / 'api.env') + '\n'
    }
    destinations = {}
    for name, content in overrides.items():
        target = Path('/etc/systemd/system') / (name + '.service.d') / SUFFIX
        if target.exists() or target.is_symlink() or any(Path(p).name >= SUFFIX for p in baseline['services'][name]['DropInPaths'].split()):
            raise RuntimeError('A conflicting override needs review.')
        prepared = operations / (name + '.conf'); prepared.write_text(content)
        rendered = operations / 'rendered' / (name + '.service'); rendered.parent.mkdir(exist_ok=True)
        rendered.write_text(subprocess.check_output(['systemctl', 'cat', name], text=True) + '\n' + content)
        subprocess.run(['systemd-analyze', 'verify', str(rendered)], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        destinations[str(prepared)] = {'destination': str(target), 'sha256': digest(prepared)}
    plan = {'version': '1.7.2', 'state': 'prepared-not-activated', 'productionActivated': False,
        'world': str(WORLD), 'api': str(API), 'activeBefore': baseline, 'requiredSchema': 14,
        'characterDatabaseWorkers': 4, 'files': files, 'worldSha256': digest(WORLD / 'build/worldserver'),
        'configurationFiles': {str(p): digest(p) for p in [*API.glob('appsettings*.json'), *production.rglob('*')] if p.is_file()},
        'destinations': destinations, 'environmentHashes': {str(p): digest(p) for p in operations.glob('*.env')},
        'worldConfigLinkBefore': str((WORLD / 'server/etc').resolve(strict=True)),
        'worldConfigLinkAfter': str(production), 'restartServices': [world_service, 'wotlk-launcher-api'],
        'preservedServices': ['hermesproxy-wotlk', 'arthas-authserver'],
        'rollback': 'Keep the schema-14 API with gold conversions disabled; restore the previous World override. Preserve all receipts and balances.'}
    (ROOT / 'plan.json').write_text(json.dumps(plan, indent=2) + '\n')
    print(json.dumps({key: plan[key] for key in ('version', 'state', 'world', 'api', 'requiredSchema', 'restartServices')}, indent=2))
    print('PASS: staged binaries and verified service overrides; no production pointer, database or service changed.')


if __name__ == '__main__': main()
