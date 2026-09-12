#!/usr/bin/env python3
"""Prepare the verified rename identity fix; activation requires approved maintenance.

This release changes World and Hermes only. It preserves the active API, Auth,
configuration values, database schema, receipts and launcher distribution.
"""
import argparse
import datetime
import fcntl
import hashlib
import json
import os
from pathlib import Path
import pwd
import shutil
import socket
import subprocess
import sys
import time

ROOT = Path('/opt/atlas-shop-releases/rename-identity-20260912')
UPLOAD = Path('/tmp/atlas-rename-identity-20260912')
WORLD = Path('/opt/arthas-next/candidates/atlas-shop-rename-identity-20260912')
HERMES = Path('/opt/hermesproxy-wotlk/releases/hermes-rename-identity-20260912')
FIXTURE = Path('/opt/atlas-shop-tests/rename-20260911')
CONFIG = Path('/opt/hermesproxy-wotlk/appsettings.atlas.json')
WORLD_SERVICE = 'arthas-worldserver.dungeon-clear-8224099'
SERVICES = (WORLD_SERVICE, 'hermesproxy-wotlk', 'arthas-authserver', 'wotlk-launcher-api')
CHANGED = SERVICES[:2]
SUFFIX = 'zzzzzzz-atlas-shop-rename-identity-20260912.conf'


def run(command, **kwargs):
    return subprocess.check_output(command, text=True, timeout=90, **kwargs).strip()


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def write(path, data):
    path.write_text(json.dumps(data, indent=2) + '\n')


def state(name):
    return dict(line.split('=', 1) for line in run(['systemctl', 'show', name, '-p',
        'MainPID,ActiveState,NRestarts,WorkingDirectory,DropInPaths,FragmentPath,User,Group']).splitlines())


def snapshot():
    result = {}
    for name in SERVICES:
        item = state(name)
        if item['ActiveState'] != 'active' or item['MainPID'] == '0':
            raise RuntimeError('Expected active service: ' + name)
        proc = Path('/proc') / item['MainPID']
        item.update(executable=str((proc / 'exe').resolve()), sha256=sha(proc / 'exe'),
                    unitFiles={p: sha(p) for p in [item['FragmentPath'], *item['DropInPaths'].split()]})
        result[name] = item
    return result


def baseline(plan, names=SERVICES):
    for name in names:
        expected = plan['before'][name]
        current = state(name)
        if any(current[key] != expected[key] for key in ('MainPID', 'ActiveState', 'DropInPaths', 'WorkingDirectory')):
            raise RuntimeError('Service changed since preparation: ' + name)
        if sha('/proc/' + current['MainPID'] + '/exe') != expected['sha256']:
            raise RuntimeError('Active binary changed: ' + name)
        for path, expected_sha in expected['unitFiles'].items():
            if sha(path) != expected_sha: raise RuntimeError('Service definition changed: ' + path)
    for path, expected_sha in plan['unchangedConfiguration'].items():
        if sha(path) != expected_sha: raise RuntimeError('Active configuration changed: ' + path)


def candidate(plan):
    for path, expected_sha in plan['candidateFiles'].items():
        if sha(path) != expected_sha: raise RuntimeError('Tested or prepared file changed: ' + path)


def prepare():
    if ROOT.exists(): raise RuntimeError('Release already prepared; inspect its plan before retrying.')
    before = snapshot()
    manifest = json.loads((WORLD / 'build/manifest.json').read_text())
    package = json.loads((UPLOAD / 'candidate.json').read_text())
    identity = json.loads((UPLOAD / 'identity-tested.json').read_text())
    regressions = json.loads((UPLOAD / 'regressions-tested.json').read_text())
    expected = {'worldSha256': sha(WORLD / 'build/worldserver'), 'hermesSha256': sha(HERMES / 'HermesProxy')}
    if set(regressions) != {'core', 'hermes', 'gold'}: raise RuntimeError('Missing regression suite.')
    for proof in [identity, *regressions.values()]:
        if not proof['passed'] or any(proof[key] != value for key, value in expected.items()):
            raise RuntimeError('Successful tests of both exact candidate binaries are required.')
    if manifest['worldserverSha256'] != expected['worldSha256'] or package['files']['HermesProxy'] != expected['hermesSha256']:
        raise RuntimeError('Candidate manifest differs from tested bytes.')
    if not manifest['releaseCandidate'] or (WORLD / 'server/etc').resolve() != FIXTURE / 'etc':
        raise RuntimeError('World candidate must still use only fixture configuration.')
    old_world = Path(before[WORLD_SERVICE]['executable']).parents[1]
    old_manifest = json.loads((old_world / 'build/manifest.json').read_text())
    changed_sources = {key for key in set(manifest['moduleSources']) | set(old_manifest['moduleSources'])
                       if manifest['moduleSources'].get(key) != old_manifest['moduleSources'].get(key)}
    if changed_sources != {'src/atlas_shop_native.cpp'}:
        raise RuntimeError('Unexpected production module source changes: ' + repr(changed_sources))
    args = [p.decode() for p in (Path('/proc') / before[WORLD_SERVICE]['MainPID'] / 'cmdline').read_bytes().split(b'\0') if p]
    active_config = Path(args[args.index('--config') + 1]).resolve(strict=True)
    if active_config.parent != (old_world / 'server/etc').resolve(strict=True):
        raise RuntimeError('Unexpected active World configuration directory.')
    configuration = {str(CONFIG): sha(CONFIG), str(active_config): sha(active_config)}
    modules = list((active_config.parent / 'modules').glob('*.conf'))
    if not modules: raise RuntimeError('No active module configurations found.')
    configuration.update({str(p): sha(p) for p in modules})
    ROOT.mkdir(mode=0o700)
    shutil.copy2(Path(__file__).resolve(), ROOT / 'deploy.py')
    operations = ROOT / 'operations'; operations.mkdir()
    production = WORLD / 'server/etc-production'
    production.mkdir(mode=0o750); (production / 'modules').mkdir(mode=0o750)
    shutil.copy2(active_config, production / 'worldserver.conf')
    for path in modules:
        if path.is_symlink(): raise RuntimeError('Unexpected symlinked module config.')
        shutil.copy2(path, production / 'modules' / path.name)
    world_group = pwd.getpwnam(before[WORLD_SERVICE]['User']).pw_gid
    for folder in (WORLD, WORLD / 'build', WORLD / 'server', WORLD / 'server/bin', production, production / 'modules'):
        os.chown(folder, 0, world_group); folder.chmod(0o750)
    for path in production.rglob('*'):
        if path.is_file(): os.chown(path, 0, world_group); path.chmod(0o640)
    os.chown(WORLD / 'build/worldserver', 0, world_group); (WORLD / 'build/worldserver').chmod(0o550)
    HERMES.chmod(0o755)
    for path in HERMES.rglob('*'):
        if path.is_dir(): path.chmod(0o755)
    for name in ('AccountData', 'Logs', 'PacketsLog'):
        shared = Path('/opt/hermesproxy-wotlk') / name
        active = Path(before['hermesproxy-wotlk']['WorkingDirectory']) / name
        if not active.is_symlink() or active.resolve(strict=True) != shared or shared.resolve() != shared:
            raise RuntimeError('Unexpected shared Hermes runtime directory.')
        (HERMES / name).symlink_to(shared, target_is_directory=True)
    texts = {
        WORLD_SERVICE: '[Service]\nExecStart=\nExecStart=' + str(WORLD / 'server/bin/worldserver')
                       + ' --config ' + str(WORLD / 'server/etc/worldserver.conf') + '\n',
        'hermesproxy-wotlk': '[Service]\nWorkingDirectory=' + str(HERMES) + '\nExecStart=\nExecStart='
                            + str(HERMES / 'HermesProxy') + ' --config ' + str(CONFIG) + '\n'}
    destinations = {}
    for name, text in texts.items():
        destination = Path('/etc/systemd/system') / (name + '.service.d') / SUFFIX
        if destination.exists() or any(Path(p).name >= SUFFIX for p in before[name]['DropInPaths'].split()):
            raise RuntimeError('Conflicting service override.')
        source = operations / (name + '.conf'); source.write_text(text)
        rendered = operations / 'rendered' / (name + '.service'); rendered.parent.mkdir(exist_ok=True)
        rendered.write_text(run(['systemctl', 'cat', name]) + '\n' + text)
        run(['systemd-analyze', 'verify', str(rendered)], stderr=subprocess.PIPE)
        destinations[str(source)] = str(destination)
    backup = ROOT / 'backup'
    for path in {Path(before[name]['executable']) for name in CHANGED} | {Path(p) for p in configuration} \
            | {Path(p) for name in CHANGED for p in before[name]['unitFiles']}:
        saved = backup / path.relative_to('/')
        saved.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(path, saved)
        if sha(saved) != sha(path): raise RuntimeError('Backup hash verification failed.')
    files = {str(WORLD / 'build/worldserver'): expected['worldSha256'],
             str(ROOT / 'deploy.py'): sha(ROOT / 'deploy.py'),
             **{str(HERMES / p): digest for p, digest in package['files'].items()},
             **{str(p): sha(p) for p in production.rglob('*') if p.is_file()},
             **{p: sha(p) for p in destinations}}
    plan = {'state': 'prepared-not-activated', 'sourceCommit': package['sourceCommit'], 'before': before,
            'candidateFiles': files, 'unchangedConfiguration': configuration, 'destinations': destinations,
            'identity': identity, 'regressions': regressions, 'backupVerified': True,
            'restartServices': list(CHANGED), 'preservedServices': list(SERVICES[2:]),
            'preparedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat()}
    baseline(plan); candidate(plan)
    for name, binary in ((WORLD_SERVICE, WORLD / 'server/bin/worldserver'), ('hermesproxy-wotlk', HERMES / 'HermesProxy')):
        run(['runuser', '-u', before[name]['User'], '--', 'test', '-x', str(binary)])
    write(ROOT / 'plan.json', plan)
    print('PASS: both tested candidates, identical production configuration, verified backups and inactive overrides prepared.')


def ready(plan):
    for name, binary in ((WORLD_SERVICE, WORLD / 'build/worldserver'), ('hermesproxy-wotlk', HERMES / 'HermesProxy')):
        current = state(name)
        if current['ActiveState'] != 'active' or current['MainPID'] in ('0', plan['before'][name]['MainPID']): return False
        if (Path('/proc') / current['MainPID'] / 'exe').resolve() != binary: return False
    for port in (4000, 8081, 1119, 8084, 8086):
        try:
            with socket.create_connection(('127.0.0.1', port), timeout=1): pass
        except OSError: return False
    # Reuse the predecessor's read-only, credential-safe API/heartbeat checks.
    sys.path.insert(0, '/opt/atlas-shop-releases/gold-1.7.2-20260912/scripts')
    from release_runtime import health, world_ready
    return health() and world_ready()


def verify():
    plan = json.loads((ROOT / 'plan.json').read_text())
    baseline(plan, SERVICES[2:]); candidate(plan)
    if not ready(plan): raise RuntimeError('Activated World/Hermes, listeners or native heartbeats are not ready.')
    result = {'verified': True, 'verifiedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'services': snapshot(), 'identityChecks': len(plan['identity']['checks']),
              'regressionChecks': {k: len(v['checks']) for k, v in plan['regressions'].items()},
              'configurationPreserved': True, 'backupVerified': True, 'graphicalClientTested': False}
    write(ROOT / 'verified.json', result)
    print(json.dumps(result, indent=2))


def activate(authorized):
    if not authorized: raise RuntimeError('Explicit approval to restart World and Hermes is required.')
    lock = (ROOT / 'maintenance.lock').open('a'); fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    plan = json.loads((ROOT / 'plan.json').read_text())
    if (ROOT / 'activation.json').exists(): raise RuntimeError('An activation record exists; inspect before retrying.')
    baseline(plan); candidate(plan)
    config_link = WORLD / 'server/etc'
    if not config_link.is_symlink() or config_link.resolve() != FIXTURE / 'etc':
        raise RuntimeError('World candidate is no longer linked to its test configuration.')
    installed = []
    progress = {'authorized': True, 'startedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat()}
    write(ROOT / 'activation.json', progress)
    try:
        # Stop the old World cleanly so player saves finish before switching.
        subprocess.run(['systemctl', 'stop', WORLD_SERVICE], check=True, timeout=300)
        if state(WORLD_SERVICE)['MainPID'] != '0': raise RuntimeError('Old World has not stopped.')
        for source, target in plan['destinations'].items():
            target = Path(target)
            if target.exists() or target.is_symlink(): raise RuntimeError('Override already installed.')
            target.write_bytes(Path(source).read_bytes()); target.chmod(0o644); installed.append(target)
        replacement = WORLD / 'server/.etc-identity-activate'
        replacement.symlink_to(WORLD / 'server/etc-production', target_is_directory=True)
        os.replace(replacement, config_link)
        run(['systemctl', 'daemon-reload'])
        subprocess.run(['systemctl', 'start', WORLD_SERVICE], check=True, timeout=300)
        subprocess.run(['systemctl', 'restart', 'hermesproxy-wotlk'], check=True, timeout=120)
        deadline = time.monotonic() + 300
        while not ready(plan):
            if time.monotonic() > deadline: raise RuntimeError('New services did not become ready.')
            time.sleep(2)
        baseline(plan, SERVICES[2:]); candidate(plan)
        progress.update(activated=True, completedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
        write(ROOT / 'activation.json', progress)
        verify()
    except BaseException as error:
        progress.update(activated=False, errorType=type(error).__name__)
        failures = []
        try: subprocess.run(['systemctl', 'stop', WORLD_SERVICE], check=True, timeout=300)
        except BaseException as failure: failures.append(type(failure).__name__)
        for path in reversed(installed):
            try:
                source = next(p for p, target in plan['destinations'].items() if target == str(path))
                if sha(path) != sha(source): raise RuntimeError('Installed override was modified.')
                path.unlink()
            except BaseException as failure: failures.append(type(failure).__name__)
        run(['systemctl', 'daemon-reload'])
        for name in CHANGED:
            try: subprocess.run(['systemctl', 'restart', name], check=True, timeout=300)
            except BaseException as failure: failures.append(name + ':' + type(failure).__name__)
        for name in CHANGED:
            current = state(name)
            if current['ActiveState'] != 'active' or current['MainPID'] == '0' \
                    or sha('/proc/' + current['MainPID'] + '/exe') != plan['before'][name]['sha256']:
                failures.append(name + ':previous-executable-not-active')
        progress['rollbackFailures'] = failures
        progress['rolledBack'] = not failures
        write(ROOT / 'activation.json', progress)
        raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=('prepare', 'activate', 'verify'))
    parser.add_argument('--authorized-maintenance', action='store_true')
    args = parser.parse_args()
    os.umask(0o077)
    if os.geteuid() != 0: raise SystemExit('Expected root.')
    if args.phase == 'activate': activate(args.authorized_maintenance)
    else: globals()[args.phase]()
