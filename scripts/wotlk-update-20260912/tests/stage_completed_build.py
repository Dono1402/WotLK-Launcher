#!/usr/bin/env python3
"""Install the completed candidate into its own prefix and bind it to the test fixture."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260912')
FIXTURE = Path('/opt/atlas-shop-tests/rename-20260911')


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    if ROOT.resolve(strict=True) != ROOT or FIXTURE.resolve(strict=True) != FIXTURE:
        raise RuntimeError('Unexpected candidate or fixture path.')
    state = dict(line.split('=', 1) for line in subprocess.check_output(
        ['systemctl', 'show', 'atlas-all-update-build-final-20260912',
         '--property=LoadState,ActiveState,ExecMainStatus'], text=True).splitlines())
    if (state['ActiveState'] != 'inactive' or state['ExecMainStatus'] != '0'
            or state['LoadState'] not in ('loaded', 'not-found')):
        raise RuntimeError('The isolated full build must finish successfully first: ' + str(state))
    unit = 'atlas-all-update-build-final-20260912.service'
    execution = json.loads((ROOT / 'evidence/build-execution.json').read_text())
    journal = subprocess.check_output(['journalctl', '-u', unit, '--no-pager', '-o', 'json',
                                       '--since=@' + str(execution['startedAtUnix'])], text=True)
    completions = [row for row in map(json.loads, journal.splitlines())
                   if row.get('_PID') == '1' and row.get('UNIT') == unit
                   and row.get('MESSAGE_ID') == '7ad2d189f7e94e70a38c781354912448']
    if len(completions) != 1:
        raise RuntimeError('The journal must confirm one successful completion of this build unit.')
    cache = (ROOT / 'build/CMakeCache.txt').read_text()
    if 'CMAKE_INSTALL_PREFIX:PATH=' + str(ROOT / 'server') + '\n' not in cache:
        raise RuntimeError('The install prefix must stay inside this candidate.')
    targets = {}
    for relative in ['src/server/apps/worldserver', 'src/server/apps/authserver',
                     'src/test/unit_tests', 'dungeon_clear_tests']:
        if not (ROOT / 'build' / relative).is_file():
            raise RuntimeError('A required build target is absent: ' + relative)
        targets[relative] = (ROOT / 'build' / relative).stat().st_size
    (ROOT / 'evidence/build-completion.json').write_text(json.dumps({
        'passed': True, 'unit': unit, 'journalMessageId': completions[0]['MESSAGE_ID'],
        'completedAtUnixMicros': int(completions[0]['__REALTIME_TIMESTAMP']),
        'builtTargetBytes': targets}, indent=2) + '\n')
    if (ROOT / 'server/etc').is_symlink() or (ROOT / 'build/manifest.json').exists():
        raise RuntimeError('The candidate was already staged; inspect it before repeating installation.')
    command = ['systemd-run', '--unit=atlas-all-update-install-20260912', '--wait', '--collect',
               '--property=PrivateNetwork=yes', '--property=ProtectSystem=strict',
               '--property=ProtectHome=yes', '--property=PrivateDevices=yes',
               '--property=TemporaryFileSystem=/dev/shm',
               '--property=ReadWritePaths=' + str(ROOT),
               '--property=InaccessiblePaths=' + str(ROOT / 'private'),
               '--property=NoNewPrivileges=yes', '--property=CPUQuota=100%',
               '--property=MemoryMax=1G', '--property=Nice=19',
               '--property=StandardOutput=append:' + str(ROOT / 'evidence/install.log'),
               '--property=StandardError=append:' + str(ROOT / 'evidence/install.log'),
               '/usr/bin/cmake', '--install', str(ROOT / 'build'), '--strip']
    subprocess.run(command, check=True, timeout=300)
    world = ROOT / 'server/bin/worldserver'
    auth = ROOT / 'server/bin/authserver'
    shutil.copy2(world, ROOT / 'build/worldserver')
    if digest(world) != digest(ROOT / 'build/worldserver'):
        raise RuntimeError('Installed and test executables must have identical bytes.')
    distribution = ROOT / 'server/etc-distribution'
    if distribution.exists():
        raise RuntimeError('Distribution backup already exists.')
    (ROOT / 'server/etc').rename(distribution)
    (ROOT / 'server/etc').symlink_to(FIXTURE / 'etc', target_is_directory=True)
    components = {}
    for name, repo in [('core', ROOT / 'core'),
                       ('playerbots', ROOT / 'core/modules/mod-playerbots'),
                       ('dungeon-clear', ROOT / 'core/modules/mod-dungeon-clear')]:
        components[name] = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip()
    manifest = {
        'releaseCandidate': True, 'validationState': 'awaiting isolated runtime checks',
        'worldserverSha256': digest(world), 'authserverSha256': digest(auth),
        'configurationDirectory': str(ROOT / 'server/etc'), 'coreDirectory': str(ROOT / 'core'),
        'configurationMode': 'isolated fixture symlink', 'components': components,
        'nativeCoreOverlay': json.loads((ROOT / 'evidence/native-core-overlay.json').read_text()),
        'productionActivationPerformed': False,
    }
    (ROOT / 'build/manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps({key: manifest[key] for key in ['worldserverSha256', 'authserverSha256', 'validationState']}))


if __name__ == '__main__':
    main()
