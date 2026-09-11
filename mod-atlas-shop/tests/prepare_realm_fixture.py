#!/usr/bin/env python3
"""Create disposable, networkless MySQL fixtures for the Atlas realm test.

Reads only schema from live auth/characters/playerbots; reads static world data
from the inactive staging database. Never imports into the source container.
All output and new MySQL data stay in the explicitly selected test directory.
"""
import argparse
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import time

CONTAINER = 'atlas-shop-rename-20260911-mysql'
IMAGE = 'sha256:1487abffa4fa011710f99e5ae2135537a5f894b7eb1380758b8915884df00e0c'
DATABASES = {'arthas_auth': 'shop_test_auth', 'arthas_chars': 'shop_test_chars',
             'arthas_playerbots': 'shop_test_playerbots', 'arthas_stage_world': 'shop_test_world'}


def test_root(value):
    root = Path(value).resolve(strict=True)
    if root.parent != Path('/opt/atlas-shop-tests') or not re.fullmatch(r'rename-[0-9A-Za-z-]+', root.name):
        raise RuntimeError('Expected a dedicated /opt/atlas-shop-tests/rename-* directory.')
    return root


def private_write(path, text):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w') as stream:
        stream.write(text)


def source_client_config():
    # Read credentials in memory. Never log inspect output or put credentials in argv.
    info = json.loads(subprocess.check_output(['docker', 'inspect', 'arthas-mysql'], text=True))[0]
    env = dict(x.split('=', 1) for x in info['Config']['Env'] if '=' in x)
    return '[client]\nuser=root\npassword=' + env['MYSQL_ROOT_PASSWORD'] + '\n'


def mysql(root, sql):
    return subprocess.check_output(['mysql', '--defaults-extra-file=' + str(root / 'mysql-client.cnf'),
                                    '-NBe', sql], text=True, stderr=subprocess.PIPE, timeout=120)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    parser.add_argument('--stop', action='store_true', help='Stop only the recorded fixture container; retain its data and evidence.')
    args = parser.parse_args()
    root = test_root(args.root)
    os.umask(0o077)
    if args.stop:
        container_id = (root / 'container-id').read_text().strip()
        if not re.fullmatch(r'[a-f0-9]{64}', container_id):
            raise RuntimeError('Invalid recorded container identity.')
        info = json.loads(subprocess.check_output(['docker', 'inspect', container_id], text=True))[0]
        if info['Name'] != '/' + CONTAINER or info['HostConfig']['NetworkMode'] != 'none':
            raise RuntimeError('The recorded container is not the expected isolated fixture.')
        mounts = {entry['Destination']: entry['Source'] for entry in info['Mounts']}
        if mounts.get('/var/lib/mysql') != str(root / 'mysql-data'):
            raise RuntimeError('Fixture data mount differs from the selected directory.')
        if info['State']['Running']:
            subprocess.run(['docker', 'stop', '--time', '20', container_id], check=True, stdout=subprocess.DEVNULL, timeout=40)
        print('PASS: only the recorded disposable MySQL container is stopped; fixture data retained.', flush=True)
        return
    if (root / 'mysql-client.cnf').exists():
        raise RuntimeError('Fixture already exists; refusing to reinitialize or overwrite it.')
    if subprocess.run(['docker', 'inspect', CONTAINER], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
        raise RuntimeError('Container name already exists.')
    for directory in ['mysql-data', 'socket', 'seed', 'logs', 'media', 'etc/modules']:
        (root / directory).mkdir(parents=True, exist_ok=True)
    # Only this empty socket directory must be writable by the container mysql UID.
    (root / 'socket').chmod(0o777)
    password = secrets.token_hex(24)
    private_write(root / 'mysql.env', 'MYSQL_ROOT_PASSWORD=' + password + '\n')
    socket = root / 'socket/mysql.sock'
    private_write(root / 'mysql-client.cnf', '[client]\nuser=root\npassword=' + password + '\nprotocol=SOCKET\nsocket=' + str(socket) + '\n')
    command = ['docker', 'run', '-d', '--name', CONTAINER, '--network=none', '--cpus=1',
               '--memory=1g', '--memory-swap=1g', '--pids-limit=150', '--env-file', str(root / 'mysql.env'),
               '--mount', 'type=bind,src=' + str(root / 'mysql-data') + ',dst=/var/lib/mysql',
               '--mount', 'type=bind,src=' + str(root / 'socket') + ',dst=/socket', IMAGE,
               '--socket=/socket/mysql.sock', '--skip-networking', '--mysqlx=OFF',
               '--innodb-buffer-pool-size=256M', '--performance-schema=OFF', '--max-connections=80']
    container_id = subprocess.check_output(command, text=True).strip()
    private_write(root / 'container-id', container_id + '\n')
    for _ in range(120):
        try:
            mysql(root, 'SELECT 1;')
            break
        except subprocess.CalledProcessError:
            time.sleep(1)
    else:
        raise RuntimeError('Disposable MySQL startup timed out.')
    cnf = source_client_config()
    source_query = "SELECT table_schema,table_name,engine FROM information_schema.tables WHERE (table_schema='arthas_chars' AND table_name='characters') OR (table_schema='arthas_stage_world' AND table_name='version');"
    engine = subprocess.check_output(['docker', 'exec', '-i', 'arthas-mysql', 'mysql',
        '--defaults-extra-file=/dev/stdin', '-NBe', source_query], input=cnf, text=True)
    # Launcher-owned tables must start empty AND absent: copying their evolved
    # definitions without migration history is not a valid baseline adoption.
    launcher_tables = subprocess.check_output(['docker', 'exec', '-i', 'arthas-mysql', 'mysql',
        '--defaults-extra-file=/dev/stdin', '-NBe',
        "SELECT table_name FROM information_schema.tables WHERE table_schema='arthas_auth' AND LEFT(table_name,6)='atlas_';"],
        input=cnf, text=True).splitlines()
    if any(not re.fullmatch(r'atlas_[a-z0-9_]+', table) for table in launcher_tables):
        raise RuntimeError('Unexpected launcher table identifier.')
    report = {'containerId': container_id, 'imageId': IMAGE, 'sourceEngineAudit': engine,
              'sourceRowsCopied': {'auth': 0, 'characters': 0, 'playerbots': 0}, 'databases': []}
    for source, target in DATABASES.items():
        print('Preparing', target, 'schema only' if source != 'arthas_stage_world' else 'static staging world data', flush=True)
        dump = root / 'seed' / (target + '.sql')
        command = ['docker', 'exec', '-i', 'arthas-mysql', 'mysqldump', '--defaults-extra-file=/dev/stdin',
                   '--skip-lock-tables', '--no-tablespaces', '--set-gtid-purged=OFF', '--skip-comments']
        command += ['--single-transaction'] if source == 'arthas_stage_world' else ['--no-data']
        if source == 'arthas_auth':
            command += ['--ignore-table=arthas_auth.' + table for table in launcher_tables]
        command.append(source)
        with dump.open('w') as stream:
            result = subprocess.run(command, input=cnf, text=True, stdout=stream, stderr=subprocess.PIPE, timeout=300)
        if result.returncode:
            raise RuntimeError('Read-only source export failed for ' + source)
        mysql(root, 'CREATE DATABASE `' + target + '` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;')
        with dump.open('rb') as stream, (root / 'logs' / ('import-' + target + '.log')).open('wb') as log:
            result = subprocess.run(['mysql', '--defaults-extra-file=' + str(root / 'mysql-client.cnf'), target],
                                    stdin=stream, stdout=log, stderr=subprocess.STDOUT, timeout=600)
        if result.returncode:
            raise RuntimeError('Import failed into disposable ' + target)
        report['databases'].append({'name': target, 'dumpBytes': dump.stat().st_size})
    mysql(root, """
        INSERT INTO shop_test_auth.realmlist (id,name,address,localAddress,localSubnetMask,port,icon,flag,timezone,allowedSecurityLevel,population,gamebuild)
        VALUES (1,'Atlas Shop Test','127.0.0.1','127.0.0.1','255.255.255.0',14001,0,0,1,0,0,12340);
        ALTER TABLE shop_test_chars.characters ENGINE=InnoDB;
        INSERT INTO shop_test_chars.active_arena_season(season_id,season_state) VALUES(8,1);
        """)
    private_write(root / 'fixture-manifest.json', json.dumps(report, indent=2) + '\n')
    print('PASS: isolated fixture databases prepared; no player/account rows copied.', flush=True)


if __name__ == '__main__':
    main()
