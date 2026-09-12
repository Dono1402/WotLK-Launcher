#!/usr/bin/env python3
"""Populate missing upstream reference tables in the disposable bot fixture."""
import configparser
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260912')
FIXTURE = Path('/opt/atlas-shop-tests/rename-20260911')
TABLES = {
    'characters': ('playerbots_names', 'playerbots_guild_names', 'playerbots_arena_team_names'),
    'playerbots': ('playerbots_weightscales', 'playerbots_weightscale_data',
                  'playerbots_travelnode', 'playerbots_travelnode_link', 'playerbots_travelnode_path',
                  'ai_playerbot_texts', 'ai_playerbot_texts_chance', 'playerbots_enchants',
                  'playerbots_speech', 'playerbots_speech_probability',
                  'playerbots_dungeon_suggestion_abbrevation', 'playerbots_dungeon_suggestion_definition',
                  'playerbots_dungeon_suggestion_strategy'),
}


def main():
    os.umask(0o077)
    if ROOT.resolve(strict=True) != ROOT or FIXTURE.resolve(strict=True) != FIXTURE:
        raise RuntimeError('Unexpected fixture or candidate path.')
    config = configparser.ConfigParser()
    config.read(FIXTURE / 'mysql-client.cnf')
    if (config['client'].get('protocol', '').upper() != 'SOCKET'
            or config['client'].get('socket') != str(FIXTURE / 'socket/mysql.sock')):
        raise RuntimeError('Only the disposable database socket is accepted.')
    for phase in ('modules', 'guild'):
        pid = subprocess.check_output(['systemctl', 'show', 'atlas-all-update-realm-' + phase + '-20260912',
                                       '--property=MainPID', '--value'], text=True).strip()
        if pid != '0':
            raise RuntimeError('Stop the held fixture before seeding reference data.')
    output = ROOT / 'evidence/bot-reference-seed.json'
    if output.exists():
        raise RuntimeError('Inspect the existing seed evidence before any repeat import.')
    container_id = (FIXTURE / 'container-id').read_text().strip()
    info = json.loads(subprocess.check_output(['docker', 'inspect', container_id], text=True))[0]
    mounts = {row['Destination']: row['Source'] for row in info['Mounts']}
    if (info['Name'] != '/atlas-shop-rename-20260911-mysql' or info['HostConfig']['NetworkMode'] != 'none'
            or mounts.get('/var/lib/mysql') != str(FIXTURE / 'mysql-data') or info['State']['Running']):
        raise RuntimeError('Expected the stopped, identity-checked disposable MySQL container.')
    client = ['mysql', '--defaults-extra-file=' + str(FIXTURE / 'mysql-client.cnf')]

    def query(sql):
        return subprocess.check_output(client + ['-NBe', sql], text=True,
                                       stderr=subprocess.PIPE, timeout=60).strip()

    report = {'productionWritePerformed': False, 'source': 'pinned upstream Playerbots base reference data',
              'tables': [], 'passed': False}
    subprocess.run(['docker', 'start', container_id], check=True, stdout=subprocess.DEVNULL)
    try:
        for _ in range(60):
            try:
                query('SELECT 1')
                break
            except subprocess.CalledProcessError:
                time.sleep(1)
        else:
            raise RuntimeError('Disposable MySQL startup timed out.')
        for kind, tables in TABLES.items():
            database = 'shop_test_chars' if kind == 'characters' else 'shop_test_playerbots'
            backup = ROOT / 'private' / ('fixture-reference-before-' + kind + '.sql')
            if backup.exists():
                raise RuntimeError('A reference backup already exists; inspect it before retrying.')
            with backup.open('wb') as stream:
                subprocess.run(['mysqldump', client[1], '--skip-lock-tables', '--no-tablespaces',
                                '--set-gtid-purged=OFF', '--skip-comments', database, *tables],
                               stdout=stream, stderr=subprocess.PIPE, check=True, timeout=180)
            for table in tables:
                path = ROOT / 'core/modules/mod-playerbots/data/sql' / kind / 'base' / (table + '.sql')
                raw = path.read_bytes()
                text = raw.decode('utf-8')
                without_values = re.sub(r"'(?:''|\\.|[^'])*'", "''", text, flags=re.S)
                touched = set(re.findall(r'(?:CREATE\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?|'
                    r'DROP\s+TABLE(?:\s+IF\s+EXISTS)?|INSERT\s+INTO|REPLACE\s+INTO|ALTER\s+TABLE|UPDATE)'
                    r'\s+`?([\w.]+)', without_values, re.I))
                if touched != {table} or re.search(r'\b(?:USE|GRANT|SOURCE)\s+|\barthas_|\bOUTFILE\b', without_values, re.I):
                    raise RuntimeError('Unexpected SQL scope in reference file: ' + path.name)
                before = int(query('SELECT COUNT(*) FROM ' + database + '.' + table))
                if before:
                    raise RuntimeError('Reference seeding expects an empty fixture table: ' + table)
                with (ROOT / 'private' / ('seed-' + table + '.log')).open('wb') as log:
                    subprocess.run(client + [database], input=raw, stdout=log, stderr=subprocess.STDOUT,
                                   check=True, timeout=300)
                after = int(query('SELECT COUNT(*) FROM ' + database + '.' + table))
                row = {'database': database, 'table': table, 'beforeRows': before, 'afterRows': after,
                       'sourceSha256': hashlib.sha256(raw).hexdigest()}
                report['tables'].append(row)
                output.write_text(json.dumps(report, indent=2) + '\n')
                print(json.dumps(row), flush=True)
        report['passed'] = True
        output.write_text(json.dumps(report, indent=2) + '\n')
    finally:
        subprocess.run(['docker', 'stop', '--time', '20', container_id], check=True,
                       stdout=subprocess.DEVNULL, timeout=50)


if __name__ == '__main__':
    main()
