#!/usr/bin/env python3
"""Execute the approved maintenance, leaving purchases closed for live checks."""
import fcntl
import json
import os
from pathlib import Path
import subprocess
import sys
import time
from release_runtime import *

def main():
    os.umask(0o077)
    if os.geteuid() != 0 or sys.argv[1:] != ['--authorized-maintenance']: raise RuntimeError('Explicit maintenance argument required')
    if ROOT.resolve(strict=True) != ROOT: raise RuntimeError('Unexpected release root')
    lock = (ROOT / 'maintenance.lock').open('a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    record = ROOT / 'activation-progress.json'
    if record.exists(): raise RuntimeError('An activation record exists; inspect before retrying')
    plan = json.loads((ROOT / 'plan.json').read_text())
    ops = plan
    from preflight import verify
    verify()
    for name, expected in plan['activeBefore']['services'].items():
        if service(name)['MainPID'] != expected['MainPID']: raise RuntimeError('Process changed since preflight')
    for source, expected in ops['destinations'].items():
        if digest(source) != expected['sha256'] or Path(expected['destination']).exists(): raise RuntimeError('Override changed or installed')
    for path, sha in ops['environmentHashes'].items():
        if digest(path) != sha: raise RuntimeError('Prepared flags changed')
    if query('SELECT version FROM arthas_auth.atlas_launcher_schema_history ORDER BY version;') != [[str(x)] for x in range(1, 14)]:
        raise RuntimeError('Production schema changed since preparation')
    state = {'authorized': True, 'startedAtUnix': int(time.time()), 'purchasesEnabled': False}
    installed, stopped = [], []
    api_started = False
    def phase(value):
        state['phase'] = value
        record.write_text(json.dumps(state, indent=2) + '\n')
        print('PHASE: ' + value, flush=True)
    try:
        phase('stopping-public-writers')
        for name in ('wotlk-launcher-api', WORLD_SERVICE):
            subprocess.run(['systemctl', 'stop', name], check=True, timeout=300)
            stopped.append(name)
            if service(name)['MainPID'] != '0': raise RuntimeError('Service did not stop')
        phase('fresh-backup-before-migration')
        subprocess.run(['python3', str(ROOT / 'scripts/backup.py'), '--after-stop'], check=True, timeout=300)
        state['backupProof'] = json.loads((ROOT / 'latest-backup.json').read_text())['proof']
        phase('installing-reviewed-overrides')
        for source, expected in ops['destinations'].items():
            target = Path(expected['destination'])
            target.parent.mkdir(mode=0o755, exist_ok=True)
            atomic_copy(source, target, 0o644); installed.append(target)
        link = WORLD / 'server/etc'
        if not link.is_symlink() or str(link.resolve()) != ops['worldConfigLinkBefore']: raise RuntimeError('Candidate configuration link changed')
        next_link = WORLD / 'server/.etc-production-activate-172'
        next_link.symlink_to(ops['worldConfigLinkAfter'], target_is_directory=True)
        os.replace(next_link, link)
        subprocess.run(['systemctl', 'daemon-reload'], check=True)
        for name, binary in ((WORLD_SERVICE, WORLD / 'server/bin/worldserver'), ('wotlk-launcher-api', API / 'WotLK.Launcher.Server')):
            execution = run(['systemctl', 'show', name, '-p', 'ExecStart', '--value'])
            if 'path=' + str(binary) + ' ;' not in execution: raise RuntimeError('Effective ExecStart differs')
        if service(WORLD_SERVICE)['WorkingDirectory'] != plan['activeBefore']['services'][WORLD_SERVICE]['WorkingDirectory']: raise RuntimeError('World working directory changed')
        phase('migrating-api-with-purchases-closed')
        api_started = True
        subprocess.run(['systemctl', 'start', 'wotlk-launcher-api'], check=True)
        wait_for('API healthy with schema 14', health, 90)
        history = query('SELECT version,name,HEX(sha256),application_version FROM arthas_auth.atlas_launcher_schema_history ORDER BY version;')
        if [int(x[0]) for x in history] != list(range(1, 15)) or history[:13] != plan['activeBefore']['database']['migrationHistory']:
            raise RuntimeError('Migration history differs')
        state['migrationHistory'] = history
        phase('starting-world-and-waiting-for-native-heartbeat')
        subprocess.run(['systemctl', 'start', WORLD_SERVICE], check=True)
        wait_for('World listening on 4000 with native and gold heartbeats', world_ready, 300)
        state['runtime'] = verify_runtime(False)
        state['completedAtUnix'] = int(time.time())
        phase('backend-active-purchases-closed')
    except BaseException as error:
        state['errorType'] = type(error).__name__
        phase('rolling-back-backend')
        failures = []
        for name in (WORLD_SERVICE, 'wotlk-launcher-api'):
            try: subprocess.run(['systemctl', 'stop', name], check=True, timeout=300)
            except BaseException as failure: failures.append(name + ':' + type(failure).__name__)
        # Once API startup may have migrated, preserve its new schema-capable
        # binary and closed-purchase flags. Never restore a database automatically.
        for path in reversed(installed):
            if api_started and path.parent.name == 'wotlk-launcher-api.service.d': continue
            try:
                expected = next(value['sha256'] for value in ops['destinations'].values() if value['destination'] == str(path))
                if digest(path) != expected: raise RuntimeError('Rollback override hash differs')
                path.unlink()
            except BaseException as failure: failures.append(str(path) + ':' + type(failure).__name__)
        subprocess.run(['systemctl', 'daemon-reload'], check=True)
        for name in ('wotlk-launcher-api', WORLD_SERVICE):
            try: subprocess.run(['systemctl', 'start', name], check=True, timeout=300)
            except BaseException as failure: failures.append(name + ':' + type(failure).__name__)
        state['rollbackFailures'] = failures
        phase('rolled-back-review-required')
        raise

if __name__ == '__main__': main()
