#!/usr/bin/env python3
"""Build the inactive gold-conversion World; production remains untouched."""
import json
import os
from pathlib import Path
import shutil
import subprocess

FIXTURE = Path('/opt/atlas-shop-tests/rename-20260911')
CANDIDATE = Path('/opt/arthas-next/candidates/atlas-shop-rename-gold-20260912')


def main():
    os.umask(0o077)
    if os.geteuid() != 0 or FIXTURE.resolve(strict=True) != FIXTURE:
        raise RuntimeError('Expected root and the existing private fixture.')
    if CANDIDATE.exists() or CANDIDATE.is_symlink() or CANDIDATE.parent.resolve(strict=True) != CANDIDATE.parent:
        raise RuntimeError('Candidate path exists or is unsafe; inspect before retry.')
    memory = dict(line.split(':', 1) for line in Path('/proc/meminfo').read_text().splitlines())
    if int(memory['MemAvailable'].split()[0]) < 5_500_000:
        raise RuntimeError('Insufficient memory headroom for the bounded build.')
    proof = json.loads((FIXTURE / 'gold-conversion-result.json').read_text())
    if not proof.get('passed') or len(proof['checks']) < 30:
        raise RuntimeError('Successful real conversion and native rename tests are required.')
    CANDIDATE.mkdir(mode=0o700)
    for folder in ('src', 'conf', 'tests'):
        shutil.copytree(FIXTURE / 'mod-atlas-shop' / folder, CANDIDATE / 'mod-atlas-shop' / folder,
                        ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    (CANDIDATE / 'server/bin').mkdir(parents=True)
    (CANDIDATE / 'server/etc').symlink_to(FIXTURE / 'etc', target_is_directory=True)
    result = subprocess.run(['systemd-run', '--unit=atlas-shop-gold-world-build-20260912', '--wait', '--collect',
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
    print('PASS: gold World built with its own configuration directory, still linked to the private fixture.')


if __name__ == '__main__': main()
