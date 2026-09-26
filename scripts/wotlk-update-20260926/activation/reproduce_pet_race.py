#!/usr/bin/env python3
"""Synthetic SQL mechanism test on the networkless fixture, never production."""
import json
from pathlib import Path
import subprocess
import time

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260926')
FIXTURE = Path('/opt/atlas-shop-tests/rename-20260926')
NAME = 'atlas-update-fixture-20260926-mysql'
TABLE = 'shop_test_chars.atlas_pet_spell_race_20260926'
CLIENT = ['mysql', '--defaults-extra-file=' + str(FIXTURE / 'mysql-client.cnf'), '-NB', '--unbuffered']


def query(sql):
    return subprocess.run(CLIENT + ['-e', sql], capture_output=True, text=True, timeout=20)


def main():
    info = json.loads(subprocess.check_output(['docker', 'inspect', NAME], text=True))[0]
    if (info['State']['Running'] or info['HostConfig']['NetworkMode'] != 'none'
            or not any(m['Source'] == str(FIXTURE / 'mysql-data') and m['Destination'] == '/var/lib/mysql'
                       for m in info['Mounts'])):
        raise RuntimeError('Expected the stopped networkless synthetic fixture.')
    output = ROOT / 'evidence/pet-spell-race-reproduction.json'
    if output.exists():
        raise RuntimeError('Preserve previous result.')
    subprocess.run(['docker', 'start', info['Id']], check=True, stdout=subprocess.DEVNULL)
    processes = []
    try:
        for _ in range(60):
            if query('SELECT 1').returncode == 0:
                break
            time.sleep(1)
        else:
            raise RuntimeError('Fixture database did not start.')
        created = query('CREATE TABLE ' + TABLE + ' (guid INT UNSIGNED NOT NULL, spell INT UNSIGNED NOT NULL,'
            ' active TINYINT UNSIGNED NOT NULL, PRIMARY KEY(guid,spell)) ENGINE=InnoDB')
        if created.returncode:
            raise RuntimeError('Refuse to reuse or overwrite a previous synthetic table.')
        prefix = 'SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED; START TRANSACTION; DELETE FROM ' + TABLE + ' WHERE guid=1;'
        first = subprocess.Popen(CLIENT + ['-e', prefix + " SELECT 'first-delete-complete'; DO SLEEP(2); INSERT INTO "
            + TABLE + ' VALUES (1,35290,129); COMMIT;'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        processes.append(first)
        if first.stdout.readline().strip() != 'first-delete-complete':
            raise RuntimeError('First synthetic transaction did not reach its synchronization point.')
        second = subprocess.Popen(CLIENT + ['-e', prefix + " SELECT 'second-delete-complete'; DO SLEEP(4); INSERT INTO "
            + TABLE + ' VALUES (1,35290,193); COMMIT;'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        processes.append(second)
        if second.stdout.readline().strip() != 'second-delete-complete':
            raise RuntimeError('Second synthetic delete unexpectedly blocked.')
        _, error_a = first.communicate(timeout=10)
        _, error_b = second.communicate(timeout=10)
        reproduced = first.returncode == 0 and second.returncode != 0 and '1062' in error_b and 'Duplicate entry' in error_b
        # Sequential whole-book rewrite remains valid: the problem is overlapping initial saves.
        sequential = query(prefix + ' INSERT INTO ' + TABLE + ' VALUES (1,35290,193); COMMIT; SELECT active FROM ' + TABLE)
        report = {'passed': reproduced and sequential.returncode == 0 and sequential.stdout.strip() == '193',
            'syntheticOnly': True, 'network': 'none', 'productionDataModified': False,
            'isolation': 'READ-COMMITTED', 'concurrentInitialSavesReproduce1062': reproduced,
            'firstTransactionExit': first.returncode, 'secondTransactionExit': second.returncode,
            'sequentialRewritePassed': sequential.returncode == 0 and sequential.stdout.strip() == '193',
            'interpretation': 'Demonstrates a compatible race mechanism, not a C++ execution trace of the production incidents.'}
        output.write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps(report), flush=True)
        if not report['passed']:
            raise RuntimeError('Synthetic reproduction did not meet expectations.')
    finally:
        for p in processes:
            if p.poll() is None:
                p.kill()
                p.wait(timeout=5)
        subprocess.run(['docker', 'stop', '--time', '30', info['Id']], check=True, stdout=subprocess.DEVNULL)


if __name__ == '__main__':
    main()
