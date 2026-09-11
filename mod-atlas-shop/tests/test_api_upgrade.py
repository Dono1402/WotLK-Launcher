#!/usr/bin/env python3
"""Rehearse the deployed schema-8 API upgrade on a private fixture.

Only the reviewed API executables are copied; no production configuration,
account rows, tokens or passwords are imported. All test data stays disposable.
"""
import argparse
import configparser
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import select
import shutil
import signal
import socket
import socketserver
import subprocess
import threading
import time
import urllib.error
import urllib.request

ROOT = Path('/opt/atlas-shop-tests/rename-20260911')
DATABASE = 'shop_upgrade_auth'
LEGACY = Path('/opt/wotlk-launcher-api-releases/media-20260907T132544Z')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--maximum-schema-version', type=int, choices=(12, 13), default=13)
    parser.add_argument('--candidate-package', choices=('api-candidate', 'api-account-services'), default='api-candidate')
    args = parser.parse_args()
    target = args.maximum_schema_version
    root = args.root.resolve(strict=True)
    if root != ROOT or os.readlink('/proc/self/ns/net') == os.readlink('/proc/1/ns/net'):
        raise RuntimeError('Expected the existing fixture in a private network namespace.')
    os.umask(0o077)
    config = configparser.ConfigParser()
    config.read(root / 'mysql-client.cnf')
    password = config['client']['password']
    checks, children, logs = [], [], []
    report_path = root / 'api-upgrade-result.json'
    report_path.write_text(json.dumps({'passed': False, 'state': 'starting'}) + '\n')

    def check(value, message):
        if not value:
            raise AssertionError(message)
        checks.append(message)
        report_path.write_text(json.dumps({'passed': False, 'state': 'running', 'checks': checks}, indent=2) + '\n')
        print('PASS', message, flush=True)

    def sql(statement):
        return subprocess.check_output(['mysql', '--defaults-extra-file=' + str(root / 'mysql-client.cnf'),
            '-NBe', statement], text=True, stderr=subprocess.PIPE, timeout=60).strip()

    for _ in range(60):
        try:
            if sql('SELECT 1;') == '1':
                break
        except subprocess.CalledProcessError:
            time.sleep(1)
    else:
        raise RuntimeError('The fixture database did not become ready.')

    def http(path, body=None, token=None, expected=200):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        request = urllib.request.Request('http://127.0.0.1:18082/' + path,
            data=None if body is None else json.dumps(body).encode(), headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                status, data = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, data = error.code, error.read()
        if status != expected:
            raise RuntimeError('Unexpected HTTP status for ' + path + ': ' + str(status))
        return json.loads(data) if data else None

    class Bridge(socketserver.BaseRequestHandler):
        def handle(self):
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as db:
                db.connect(str(root / 'socket/mysql.sock'))
                while True:
                    ready, _, _ = select.select((self.request, db), (), (), 30)
                    for peer in ready:
                        data = peer.recv(65536)
                        if not data:
                            return
                        (db if peer is self.request else self.request).sendall(data)

    class Server(socketserver.ThreadingTCPServer):
        daemon_threads = True
        allow_reuse_address = True

    def start(package, ceiling, label):
        settings = {'Urls': 'http://127.0.0.1:18082', 'LauncherServer': {
            'ConnectionString': 'Server=127.0.0.1;Port=13308;User ID=root;Password=' + password + ';Database=' + DATABASE + ';SSL Mode=None;',
            'CharacterDatabaseName': 'shop_test_chars', 'WorldDatabaseName': 'shop_test_world',
            'PlayerbotsDatabaseName': 'shop_test_playerbots', 'AvatarMediaRoot': str(root / 'media/upgrade-avatars'),
            'ChatMediaRoot': str(root / 'media/upgrade-chat'), 'FeedRoot': str(root / 'feed'),
            'AddonRoot': str(root / 'addons'), 'PublicBaseUrl': 'http://127.0.0.1:18082', 'BrevoApiKey': ''},
            'AtlasShop': {'Purchases': {'RenameEnabled': False, 'AccountServicesEnabled': ceiling >= 13, 'RealmId': 1},
                          'ManualPayPal': {'Enabled': False}}}
        (package / 'appsettings.Testing.json').write_text(json.dumps(settings, indent=2) + '\n')
        env = {'PATH': '/usr/local/bin:/usr/bin:/bin', 'HOME': str(root), 'TMPDIR': str(root / 'tmp'),
            'DOTNET_ENVIRONMENT': 'Testing', 'ASPNETCORE_ENVIRONMENT': 'Testing',
            'WOTLK_LAUNCHER_MAX_SCHEMA_VERSION': str(ceiling)}
        log = (root / 'logs' / ('api-upgrade-' + label + '.log')).open('w')
        logs.append(log)
        process = subprocess.Popen([str(package / 'WotLK.Launcher.Server')], cwd=package,
            env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
        children.append(process)
        for _ in range(90):
            if process.poll() is not None:
                raise RuntimeError('The ' + label + ' API exited; inspect its private log.')
            try:
                if http('health')['status'] == 'ok':
                    return process
            except OSError:
                time.sleep(0.5)
        raise RuntimeError('API startup timed out.')

    def stop_signal(*_):
        raise KeyboardInterrupt()

    signal.signal(signal.SIGTERM, stop_signal)
    signal.signal(signal.SIGINT, stop_signal)
    bridge = Server(('127.0.0.1', 13308), Bridge)
    threading.Thread(target=bridge.serve_forever, daemon=True).start()
    created = False
    try:
        if sql("SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name='" + DATABASE + "';") != '0':
            raise RuntimeError('Upgrade database already exists; refusing to overwrite it.')
        sql('CREATE DATABASE ' + DATABASE + ' CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;')
        created = True
        # The deployed schema already contains later profile columns. Preserve
        # its audited migration metadata rather than adopting it as version 1.
        with (root / 'api-upgrade-schema8.sql').open('rb') as seed:
            subprocess.run(['mysql', '--defaults-extra-file=' + str(root / 'mysql-client.cnf'), DATABASE],
                stdin=seed, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=120, check=True)
        history = json.loads((root / 'api-upgrade-schema8-history.json').read_text())
        if [int(row[0]) for row in history] != list(range(1, 9)) or any(
                not re.fullmatch(r'[a-z0-9_]+', row[1]) or not re.fullmatch(r'[0-9A-F]{64}', row[2]) for row in history):
            raise RuntimeError('Expected audited schema-8 migration names and checksums.')
        for version, name, checksum in history:
            sql('INSERT INTO ' + DATABASE + '.atlas_launcher_schema_history '
                "VALUES(" + str(int(version)) + ",'" + name + "',UNHEX('" + checksum
                + "'),UTC_TIMESTAMP(6),0,'schema8-fixture');")
        # This invariant is static migration data, not a player's chat history.
        sql('INSERT INTO ' + DATABASE + '.atlas_launcher_chat_v2_sequence(id,revision) VALUES(1,0);')
        old = root / 'api-upgrade-old'
        new = root / args.candidate_package
        old.mkdir(exist_ok=True)
        for name in ('WotLK.Launcher.Server', 'libSkiaSharp.so'):
            shutil.copyfile(LEGACY / name, old / name)
        (old / 'WotLK.Launcher.Server').chmod(0o700)
        legacy_hash = hashlib.sha256((old / 'WotLK.Launcher.Server').read_bytes()).hexdigest()
        candidate_hash = hashlib.sha256((new / 'WotLK.Launcher.Server').read_bytes()).hexdigest()
        current = start(old, 8, 'legacy8')
        check(sql('SELECT MAX(version) FROM ' + DATABASE + '.atlas_launcher_schema_history;') == '8',
              'The actual deployed API accepts the copied production schema and migration metadata at version 8.')
        username = 'UPGRADE' + secrets.token_hex(4).upper()
        account = http('api/v1/accounts', {'username': username, 'password': secrets.token_hex(16),
                                         'email': username.lower() + '@example.invalid'})
        account_id = int(account['profile']['accountId'])
        deadline = sql('SELECT refresh_expires_at FROM ' + DATABASE + '.atlas_launcher_session WHERE account_id=' + str(account_id) + ';')
        check(bool(account['refreshToken']) and bool(deadline),
              'The deployed API issues a real synthetic session before the upgrade.')
        current.terminate()
        current.wait(timeout=30)
        current = start(new, target, 'candidate' + str(target))
        check(sql('SELECT COUNT(*),MAX(version) FROM ' + DATABASE + '.atlas_launcher_schema_history;') == f'{target}\t{target}',
              'The candidate API migrates the existing schema from 8 through ' + str(target) + '.')
        preserved = sql('SELECT refresh_expires_at,absolute_expires_at FROM ' + DATABASE + '.atlas_launcher_session WHERE account_id=' + str(account_id) + ';')
        check(preserved == deadline + '\t' + deadline,
              'The pre-upgrade session keeps its original refresh deadline and absolute expiration.')
        shop = http('api/v1/shop', token=account['accessToken'])
        check(shop is not None, 'The old access token still authenticates against the upgraded API.')
        if target >= 13:
            check(shop.get('checkoutAvailable') is False and shop.get('purchases', {}).get('accountServices') is True,
                  'Account-service storage is readable while checkout stays closed without a compatible native consumer.')
        rotated = http('api/v1/auth/refresh', {'refreshToken': account['refreshToken']})
        check(rotated['profile']['accountId'] == account_id and rotated['refreshToken'] != account['refreshToken'],
              'A refresh token issued by the deployed API rotates successfully after the upgrade.')
        check(sql('SELECT absolute_expires_at FROM ' + DATABASE + '.atlas_launcher_session WHERE account_id=' + str(account_id) + ';') == deadline,
              'Token rotation does not extend the pre-upgrade session lifetime.')
        check(sql('SELECT COUNT(*) FROM ' + DATABASE + '.atlas_shop_wallet;') == '0'
              and sql('SELECT COUNT(*) FROM ' + DATABASE + '.atlas_shop_order;') == '0',
              'Migration grants no funds and creates no purchase or entitlement.')
        current.terminate()
        current.wait(timeout=30)
        current = start(new, target, 'candidate' + str(target) + '-restart')
        check(http('api/v1/shop', token=rotated['accessToken']) is not None
              and sql('SELECT COUNT(*) FROM ' + DATABASE + '.atlas_launcher_schema_history;') == str(target),
              'The upgraded API restarts cleanly without repeating migrations or losing the session.')
        report = {'passed': True, 'checkCount': len(checks), 'checks': checks,
            'legacyExecutableSha256': legacy_hash, 'candidateExecutableSha256': candidate_hash,
            'completedAtUnix': int(time.time()), 'productionRowsCopied': 0, 'database': DATABASE,
            'maximumSchemaVersion': target, 'candidatePackage': args.candidate_package}
        report_path.write_text(json.dumps(report, indent=2) + '\n')
        print('PASS:', len(checks), 'real API upgrade checks.', flush=True)
    finally:
        for process in reversed(children):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
        for log in logs:
            log.close()
        bridge.shutdown()
        bridge.server_close()
        if created:
            sql('DROP DATABASE ' + DATABASE + ';')
            print('Upgrade database removed; both API versions stopped.', flush=True)


if __name__ == '__main__':
    main()
