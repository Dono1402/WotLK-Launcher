#!/usr/bin/env python3
"""Start one bounded validation phase for the inactive combined Atlas candidate."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path('/opt/atlas-shop-tests/rename-20260911')
CANDIDATE = Path('/opt/arthas-next/candidates/atlas-all-update-20260912')
PHASES = {
    'gold': ['--account-services', '--gold-conversion'],
    'identity': ['--account-services', '--rename-identity'],
    'services': ['--account-services'],
    'native': ['--account-services'],
    'modules': ['--account-services', '--hold', '--all-modules'],
    'guild': ['--account-services', '--hold', '--all-modules', '--reserve-guild-bots'],
}
RESULTS = {
    'gold': 'gold-conversion-result.json', 'identity': 'hermes-identity-result.json',
    'services': 'hermes-account-services-result.json', 'native': 'native-account-services-result.json',
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=PHASES)
    args = parser.parse_args()
    os.umask(0o077)
    if ROOT.resolve(strict=True) != ROOT or CANDIDATE.resolve(strict=True) != CANDIDATE:
        raise RuntimeError('Only the dedicated fixture and candidate are accepted.')
    manifest = json.loads((CANDIDATE / 'build/manifest.json').read_text())
    world_binary = CANDIDATE / 'build/worldserver'
    with world_binary.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest() != manifest['worldserverSha256']:
            raise RuntimeError('Candidate executable differs from its build manifest.')
    if (CANDIDATE / 'server/etc').resolve(strict=True) != (ROOT / 'etc').resolve(strict=True):
        raise RuntimeError('The candidate must still use the isolated fixture configuration.')
    hermes_files = 0
    if args.phase != 'native':
        package = json.loads((CANDIDATE / 'hermes/package-manifest.json').read_text())
        for relative, expected in package['files'].items():
            # The fixture writes its own JSON settings; executable and data
            # files must be identical to the preserved publication package.
            copies = [CANDIDATE / 'hermes/publish' / relative]
            if relative != 'appsettings.json':
                copies.append(ROOT / 'hermes-all-update' / relative)
            for path in copies:
                with path.open('rb') as stream:
                    if hashlib.file_digest(stream, 'sha256').hexdigest() != expected:
                        raise RuntimeError('Hermes package differs from its manifest: ' + relative)
            hermes_files += 1
    for process in Path('/proc').glob('[0-9]*/exe'):
        try:
            executable = process.resolve(strict=True)
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            continue
        if ROOT in executable.parents or executable == world_binary.resolve(strict=True):
            raise RuntimeError('An owned fixture process is still running.')
    available = next(int(line.split()[1]) * 1024 for line in Path('/proc/meminfo').read_text().splitlines()
                     if line.startswith('MemAvailable:'))
    all_modules = args.phase in ('modules', 'guild')
    # Shop phases peaked below 2.5 GiB on this candidate. Keep the full module
    # fixture capped at 5 GiB, with at least 2 GiB of starting host headroom.
    required_gib = 7 if all_modules else 6
    if available < required_gib * 1024 ** 3:
        raise RuntimeError(f'Wait for at least {required_gib} GiB available before starting the private realm.')
    container_id = (ROOT / 'container-id').read_text().strip()
    info = json.loads(subprocess.check_output(['docker', 'inspect', container_id], text=True))[0]
    mounts = {item['Destination']: item['Source'] for item in info['Mounts']}
    if (info['Name'] != '/atlas-shop-rename-20260911-mysql'
            or info['HostConfig']['NetworkMode'] != 'none'
            or mounts.get('/var/lib/mysql') != str(ROOT / 'mysql-data')):
        raise RuntimeError('The disposable database container identity does not match.')
    evidence = CANDIDATE / 'evidence' / ('realm-' + args.phase)
    evidence.mkdir(exist_ok=True)
    for name in [RESULTS.get(args.phase), 'dc_testruns.jsonl', 'dc_testrun_live.json']:
        if name and (ROOT / name).exists():
            (evidence / ('before-' + name)).write_bytes((ROOT / name).read_bytes())
    stop_file = ROOT / 'stop-fixture'
    if stop_file.exists():
        if not stop_file.is_file() or stop_file.is_symlink():
            raise RuntimeError('Unexpected fixture stop marker.')
        stop_file.unlink()
    if not info['State']['Running']:
        subprocess.run(['docker', 'start', container_id], check=True,
                       stdout=subprocess.DEVNULL, timeout=40)
    unit = 'atlas-all-update-realm-' + args.phase + '-20260912'
    log = evidence / 'runner.log'
    command = ['systemd-run', '--unit=' + unit, '--wait', '--collect',
               '--property=MemoryHigh=' + ('4800M' if all_modules else '4G'),
               '--property=MemoryMax=5G', '--property=MemorySwapMax=0',
               '--property=CPUQuota=200%', '--property=Nice=15', '--property=IOWeight=20',
               '--property=PrivateNetwork=yes', '--property=ProtectSystem=strict',
               '--property=ProtectHome=yes', '--property=PrivateDevices=yes',
               '--property=TemporaryFileSystem=/dev/shm',
               '--property=ReadWritePaths=' + str(ROOT) + ' ' + str(CANDIDATE / 'evidence'),
               '--property=InaccessiblePaths=' + str(CANDIDATE / 'private'),
               '--property=NoNewPrivileges=yes', '--property=RuntimeMaxSec=2850',
               '--property=StandardOutput=append:' + str(log), '--property=StandardError=append:' + str(log),
               '/usr/bin/python3', str(ROOT / 'mod-atlas-shop/tests/run_realm_fixture.py'),
               '--root', str(ROOT), '--world-candidate', str(CANDIDATE), '--api-package', 'api-gold']
    if args.phase != 'native':
        command += ['--with-hermes', '--hermes-package', 'hermes-all-update']
    command += PHASES[args.phase]
    started = time.time()
    print('START', args.phase, unit, flush=True)
    try:
        result = subprocess.run(command, timeout=2950)
        result_name = RESULTS.get(args.phase)
        report = None
        if result_name and (ROOT / result_name).exists() and (ROOT / result_name).stat().st_mtime >= started:
            report = json.loads((ROOT / result_name).read_text())
            (evidence / result_name).write_text(json.dumps(report, indent=2) + '\n')
        for path in (ROOT / 'logs').glob('*console.log'):
            if path.stat().st_mtime >= started and (
                    path.name in ('auth-console.log', 'hermes-console.log', 'api-console.log', 'world-console.log')
                    or path.name.startswith('world-restart-')):
                (evidence / path.name).write_bytes(path.read_bytes())
        child_status = ROOT / 'child-exit-status.json'
        if child_status.exists() and child_status.stat().st_mtime >= started:
            (evidence / child_status.name).write_bytes(child_status.read_bytes())
        summary = {'phase': args.phase, 'exit': result.returncode,
                   'elapsedSeconds': round(time.time() - started, 2),
                   'reportPassed': report.get('passed') if report else None,
                   'currentReportPresent': report is not None,
                   'hermesPackageFilesVerified': hermes_files}
        (evidence / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
        print(json.dumps(summary), flush=True)
        raise SystemExit(result.returncode or (1 if result_name and not summary['reportPassed'] else 0))
    finally:
        # Kill only this transient test unit if the orchestration was interrupted,
        # then stop only the identity-checked disposable MySQL container.
        state = subprocess.run(['systemctl', 'is-active', unit], capture_output=True, text=True)
        if state.returncode == 0:
            subprocess.run(['systemctl', 'stop', unit], check=True, timeout=100)
        subprocess.run(['docker', 'stop', container_id], check=True,
                       stdout=subprocess.DEVNULL, timeout=70)


if __name__ == '__main__':
    main()
