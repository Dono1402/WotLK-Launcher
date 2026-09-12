#!/usr/bin/env python3
"""Run compiled upstream protocol suites only inside the disposable Atlas realm."""
import argparse
import configparser
import json
import os
from pathlib import Path
import re
import subprocess
import time

ROOT = Path('/opt/atlas-shop-tests/rename-20260911')
CANDIDATE = Path('/opt/arthas-next/candidates/atlas-all-update-20260912')
SUITES = ('smoke', 'group', 'trade', 'session', 'death', 'ulduar', 'stratholme', 'atlas-guild', 'atlas-dc')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('suites', choices=SUITES, nargs='+')
    args = parser.parse_args()
    network = os.readlink('/proc/self/ns/net')
    if network == os.readlink('/proc/1/ns/net'):
        raise RuntimeError('A private fixture network is mandatory.')
    fixture_pid = int((ROOT / 'fixture.pid').read_text())
    if network != os.readlink(f'/proc/{fixture_pid}/ns/net'):
        raise RuntimeError('This is not the active fixture network.')
    world_pid = int((ROOT / 'world.pid').read_text())
    if Path(f'/proc/{world_pid}/exe').resolve(strict=True) != (CANDIDATE / 'build/worldserver').resolve(strict=True):
        raise RuntimeError('The fixture must be running the combined candidate.')
    if (CANDIDATE / 'server/etc').resolve(strict=True) != (ROOT / 'etc').resolve(strict=True):
        raise RuntimeError('The compiled configuration directory must still point to the fixture.')
    config = configparser.ConfigParser()
    config.read(ROOT / 'mysql-client.cnf')
    password = config['client']['password']
    env = {'PATH': '/usr/bin:/bin', 'HOME': str(ROOT), 'TMPDIR': str(ROOT / 'tmp'),
           'E2E_AUTH_ADDR': '127.0.0.1:13724', 'E2E_WORLD_LOG': 'warn', 'GOMAXPROCS': '1'}
    for key, database in [('AUTH', 'auth'), ('CHAR', 'chars'), ('WORLD', 'world')]:
        env[f'E2E_{key}_DSN'] = f'root:{password}@tcp(127.0.0.1:13308)/shop_test_{database}'
    output = CANDIDATE / 'evidence/protocol'
    output.mkdir(exist_ok=True)
    results = []
    for suite in args.suites:
        binary = 'atlas-custom' if suite.startswith('atlas-') else suite
        timeout = '25m' if suite == 'atlas-dc' else '20m'
        command = [str(CANDIDATE / f'e2e-bins/{binary}.test'), '-test.v', '-test.count=1',
                   '-test.parallel=1', '-test.timeout=' + timeout]
        if suite.startswith('atlas-'):
            test = 'PrepareGuildReservation' if suite == 'atlas-guild' else 'StartDungeonClear'
            command.append('-test.run=^TestAtlas_' + test + '$')
        started = time.time()
        log_path = output / f'{suite}.log'
        print('START', suite, flush=True)
        with log_path.open('w') as log:
            try:
                result = subprocess.run(command, cwd=CANDIDATE / 'core/e2e', env=env,
                                        stdout=log, stderr=subprocess.STDOUT,
                                        timeout=1530 if suite == 'atlas-dc' else 1230)
                code = result.returncode
            except subprocess.TimeoutExpired:
                code = 124
        text = log_path.read_text(errors='replace')
        row = {'suite': suite, 'exit': code, 'elapsedSeconds': round(time.time() - started, 2),
               'passedTests': len(re.findall(r'^--- PASS:', text, re.M)),
               'failedTests': len(re.findall(r'^--- FAIL:', text, re.M)),
               'skippedTests': len(re.findall(r'^--- SKIP:', text, re.M)),
               'log': str(log_path)}
        results.append(row)
        (output / f'{suite}.json').write_text(json.dumps(row, indent=2) + '\n')
        print(json.dumps(row), flush=True)
        if code:
            print('The failing suite is preserved for diagnosis; later suites remain independent.', flush=True)
    raise SystemExit(1 if any(row['exit'] for row in results) else 0)


if __name__ == '__main__':
    main()
