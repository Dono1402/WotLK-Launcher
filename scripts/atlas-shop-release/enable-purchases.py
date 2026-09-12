#!/usr/bin/env python3
"""Open rename purchases only after successful live checks; restore closed flags on failure."""
import fcntl
import json
import os
import subprocess
import time
from release_runtime import *

def main():
    os.umask(0o077)
    if os.geteuid() != 0: raise RuntimeError('Expected root')
    lock = (ROOT / 'maintenance.lock').open('a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    if (ROOT / 'purchases-enabled.json').exists(): raise RuntimeError('Purchases activation already recorded')
    if not json.loads((ROOT / 'live-check-closed.json').read_text())['passed']: raise RuntimeError('Closed live check required')
    before = verify_runtime(False)
    ops = json.loads((ROOT / 'operations/operations.json').read_text())
    current, enabled = ROOT / 'operations/api.env', ROOT / 'operations/api-enabled.env'
    for path, sha in ops['environmentHashes'].items():
        if digest(path) != sha: raise RuntimeError('Prepared activation flags changed')
    closed = ROOT / 'operations/api-closed.rollback.env'
    if closed.exists(): raise RuntimeError('An enable attempt already exists')
    atomic_copy(current, closed, 0o600)
    try:
        atomic_copy(enabled, current, 0o600)
        subprocess.run(['systemctl', 'restart', 'wotlk-launcher-api'], check=True, timeout=90)
        wait_for('API healthy with rename purchases enabled', health, 90)
        after = verify_runtime(True)
        for name in (WORLD_SERVICE, 'hermesproxy-wotlk', 'arthas-authserver'):
            if before[name]['MainPID'] != after[name]['MainPID']: raise RuntimeError('An unrelated service changed')
        subprocess.run(['python3', str(ROOT / 'scripts/check-live.py'), 'enabled'], check=True, timeout=90)
        (ROOT / 'purchases-enabled.json').write_text(json.dumps({'enabled': True, 'completedAtUnix': int(time.time()), 'runtime': after}, indent=2) + '\n')
        print('PASS: native rename purchases enabled and verified through authenticated HTTPS; zero real orders created.', flush=True)
    except BaseException:
        atomic_copy(closed, current, 0o600)
        subprocess.run(['systemctl', 'restart', 'wotlk-launcher-api'], check=True, timeout=90)
        wait_for('API healthy after restoring closed purchases', health, 90)
        raise

if __name__ == '__main__': main()
