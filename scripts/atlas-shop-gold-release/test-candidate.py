#!/usr/bin/env python3
"""Verify the staged World/API in the private fixture, then stop that fixture."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import time

ROOT = Path('/opt/atlas-shop-releases/gold-1.7.2-20260912')
FIXTURE = Path('/opt/atlas-shop-tests/rename-20260911')
WORLD = Path('/opt/arthas-next/candidates/atlas-shop-rename-gold-20260912')
API = Path('/opt/wotlk-launcher-api-releases/shop-gold-1.7.2-20260912')


def digest(path):
    with Path(path).open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    os.umask(0o077)
    plan = json.loads((ROOT / 'plan.json').read_text())
    if plan['productionActivated'] or digest(WORLD / 'build/worldserver') != plan['worldSha256']:
        raise RuntimeError('Expected the unchanged inactive World candidate.')
    if (WORLD / 'server/etc').resolve(strict=True) != FIXTURE / 'etc':
        raise RuntimeError('Candidate must still use the private test configuration.')
    for proc in Path('/proc').glob('[0-9]*/exe'):
        try:
            if str(proc.resolve()).startswith((str(FIXTURE) + '/', str(WORLD) + '/')):
                raise RuntimeError('An owned fixture process is still running.')
        except (FileNotFoundError, PermissionError): pass
    with tarfile.open('/tmp/atlas-shop-gold-fixture.tar.gz') as archive:
        for item in archive.getmembers():
            if not item.name.startswith('mod-atlas-shop/tests/'): continue
            target = FIXTURE / item.name
            if not item.isfile() or FIXTURE not in target.resolve().parents or item.issym() or item.islnk():
                raise RuntimeError('Unsafe fixture test path.')
            target.write_bytes(archive.extractfile(item).read())
    for relative, sha in plan['files'].items():
        source = API / relative
        if digest(source) != sha: raise RuntimeError('Staged API bytes changed.')
        target = FIXTURE / 'api-gold' / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    container_id = (FIXTURE / 'container-id').read_text().strip()
    info = json.loads(subprocess.check_output(['docker', 'inspect', container_id], text=True))[0]
    if info['Name'] != '/atlas-shop-rename-20260911-mysql' or info['HostConfig']['NetworkMode'] != 'none':
        raise RuntimeError('Unexpected MySQL fixture.')
    if {x['Destination']: x['Source'] for x in info['Mounts']}.get('/var/lib/mysql') != str(FIXTURE / 'mysql-data'):
        raise RuntimeError('Unexpected database mount.')
    if not info['State']['Running']:
        subprocess.run(['docker', 'start', container_id], check=True, stdout=subprocess.DEVNULL, timeout=40)
    log = FIXTURE / 'logs/gold-final-candidate.log'
    command = ['systemd-run', '--unit=atlas-shop-gold-final-20260912', '--wait', '--collect',
        '--property=MemoryMax=3G', '--property=MemorySwapMax=0', '--property=CPUQuota=150%',
        '--property=Nice=10', '--property=IOWeight=25', '--property=PrivateNetwork=yes',
        '--property=ProtectSystem=strict', '--property=ReadWritePaths=' + str(FIXTURE),
        '--property=NoNewPrivileges=yes', '--property=RuntimeMaxSec=600',
        '--property=StandardOutput=append:' + str(log), '--property=StandardError=append:' + str(log),
        '/usr/bin/python3', str(FIXTURE / 'mod-atlas-shop/tests/run_realm_fixture.py'), '--root', str(FIXTURE),
        '--account-services', '--gold-conversion', '--api-package', 'api-gold',
        '--with-hermes', '--hermes-package', 'hermes-native', '--world-candidate', str(WORLD)]
    try:
        result = subprocess.run(command, timeout=660)
        print(log.read_text()[-9000:], flush=True)
        if result.returncode: raise RuntimeError('Final candidate test failed.')
        proof = json.loads((FIXTURE / 'gold-conversion-result.json').read_text())
        if not proof.get('passed') or len(proof['checks']) < 30: raise RuntimeError('The final report is incomplete.')
        for relative, sha in plan['files'].items():
            if digest(FIXTURE / 'api-gold' / relative) != sha: raise RuntimeError('Tested API bytes differ.')
        proof.update(worldSha256=plan['worldSha256'], apiFiles=plan['files'],
            hermesSha256=digest(FIXTURE / 'hermes-native/HermesProxy'), completedAtUnix=int(time.time()))
        (ROOT / 'final-candidate-test.json').write_text(json.dumps(proof, indent=2) + '\n')
    finally:
        subprocess.run(['docker', 'stop', container_id], check=True, stdout=subprocess.DEVNULL, timeout=70)


if __name__ == '__main__': main()
