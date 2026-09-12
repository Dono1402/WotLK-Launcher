#!/usr/bin/env python3
"""Build one inactive native-service World; never modify a running service."""
import json
import os
from pathlib import Path
import shutil
import subprocess

FIXTURE = Path('/opt/atlas-shop-tests/rename-20260911')
CANDIDATE = Path('/opt/arthas-next/candidates/atlas-shop-rename-native-20260912')


def main():
    os.umask(0o077)
    if os.geteuid() != 0 or FIXTURE.resolve(strict=True) != FIXTURE:
        raise RuntimeError('Expected root and the existing private fixture.')
    if CANDIDATE.exists() or CANDIDATE.is_symlink():
        raise RuntimeError('Candidate already exists; inspect it instead of overwriting it.')
    if CANDIDATE.parent.resolve(strict=True) != CANDIDATE.parent:
        raise RuntimeError('Unexpected candidate parent.')
    memory = dict(line.split(':', 1) for line in Path('/proc/meminfo').read_text().splitlines())
    if int(memory['MemAvailable'].split()[0]) < 5_500_000:
        raise RuntimeError('Insufficient memory headroom for a bounded build.')
    manifest = json.loads((FIXTURE / 'build-native/manifest.json').read_text())
    if not manifest.get('linked') or len(manifest.get('nativeCoreOverlay', [])) != 3:
        raise RuntimeError('Verified native fixture build is required.')
    for name in ('native-account-services-result.json', 'hermes-account-services-result.json'):
        report = json.loads((FIXTURE / name).read_text())
        if not report.get('passed'): raise RuntimeError('Unsuccessful native fixture: ' + name)
    CANDIDATE.mkdir(mode=0o700)
    for folder in ('src', 'conf', 'tests'):
        shutil.copytree(FIXTURE / 'mod-atlas-shop' / folder, CANDIDATE / 'mod-atlas-shop' / folder,
                        ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    (CANDIDATE / 'server/bin').mkdir(parents=True)
    # The compiled directory belongs to this candidate. Tests use only their
    # existing configuration through this link; activation will switch it to a
    # separately reviewed production configuration after explicit authorization.
    (CANDIDATE / 'server/etc').symlink_to(FIXTURE / 'etc', target_is_directory=True)
    (CANDIDATE / 'preparation.json').write_text(json.dumps({
        'version': '1.7.0', 'status': 'building', 'productionActivated': False,
        'fixture': str(FIXTURE), 'candidate': str(CANDIDATE),
        'sourceCommit': 'f0a0354997ee42a0f2f7fba1313dfb3ac2758cf8'}, indent=2) + '\n')
    result = subprocess.run(['systemd-run', '--unit=atlas-shop-release-world-20260912', '--wait', '--collect',
        '--property=MemoryMax=4G', '--property=MemorySwapMax=0', '--property=CPUQuota=100%',
        '--property=Nice=10', '--property=IOWeight=25', '--property=PrivateNetwork=yes',
        '--property=ProtectSystem=strict', '--property=ReadWritePaths=' + str(CANDIDATE),
        '--property=NoNewPrivileges=yes', '--property=RuntimeMaxSec=1500',
        '--property=StandardOutput=append:' + str(CANDIDATE / 'build-console.log'),
        '--property=StandardError=append:' + str(CANDIDATE / 'build-console.log'),
        '/usr/bin/python3', str(CANDIDATE / 'mod-atlas-shop/tests/build_realm_linux.py'),
        '--root', str(CANDIDATE), '--release-candidate'], timeout=1600)
    print((CANDIDATE / 'build-console.log').read_text()[-5000:])
    if result.returncode: raise RuntimeError('Candidate build failed; retained for inspection.')
    (CANDIDATE / 'server/bin/worldserver').symlink_to(CANDIDATE / 'build/worldserver')
    print('PASS: inactive World release built. Its configuration link still points to the private fixture.')


if __name__ == '__main__': main()
