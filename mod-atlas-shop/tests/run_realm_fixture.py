#!/usr/bin/env python3
"""Start the disposable API/world pair inside a private network namespace.

The test MySQL container is networkless and reached by its dedicated Unix
socket. Production configuration is never copied. Children are stopped on
exit. This fixture has no public listener, real account, money or game client.
"""
import argparse
import configparser
import importlib.util
import json
import os
from pathlib import Path
import re
import select
import signal
import socket as sockets
import socketserver
import subprocess
import threading
import time
import urllib.request

BASE = Path('/opt/arthas-next/candidates/modules-update-20260905T1016Z/core')
DATA = Path('/opt/arthas-next/candidates/dungeon-clear-8224099-20260903T062903Z/server/data')


def replace_options(text, values):
    pending = dict(values)
    result = []
    for line in text.splitlines():
        match = re.match(r'^\s*([A-Za-z0-9_.]+)\s*=', line)
        if match and match[1] in values:
            if match[1] in pending:
                result.append(match[1] + ' = ' + str(pending.pop(match[1])))
        else:
            result.append(line)
    result += [key + ' = ' + str(value) for key, value in pending.items()]
    return '\n'.join(result) + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    parser.add_argument('--hold', action='store_true', help='Keep the fixture available for bounded diagnostic tests.')
    args = parser.parse_args()
    root = Path(args.root).resolve(strict=True)
    if root.parent != Path('/opt/atlas-shop-tests') or not re.fullmatch(r'rename-[0-9A-Za-z-]+', root.name):
        raise RuntimeError('Expected an existing dedicated test directory.')
    if os.readlink('/proc/self/ns/net') == os.readlink('/proc/1/ns/net'):
        raise RuntimeError('PrivateNetwork=yes is mandatory for the test API/world.')
    os.umask(0o077)
    (root / 'native-test-result.json').write_text(json.dumps({'passed': False, 'state': 'starting', 'startedAtUnix': int(time.time())}) + '\n')
    config = configparser.ConfigParser()
    config.read(root / 'mysql-client.cnf')
    password = config['client']['password']
    socket = str(root / 'socket/mysql.sock')
    if not Path(socket).is_socket():
        raise RuntimeError('The disposable MySQL Unix socket is unavailable.')
    databases = {'Login': 'shop_test_auth', 'Character': 'shop_test_chars',
                 'World': 'shop_test_world', 'Playerbots': 'shop_test_playerbots'}
    values = {key + 'DatabaseInfo': '"127.0.0.1;13308;root;' + password + ';' + database + '"'
              for key, database in databases.items()}
    values.update({key + 'Database.WorkerThreads': 1 for key in databases})
    values.update({key + 'Database.SynchThreads': 1 for key in databases})
    values.update({'RealmID': 1, 'WorldServerPort': 14001, 'BindIP': '"127.0.0.1"',
                   'DataDir': '"' + str(DATA) + '"', 'LogsDir': '"' + str(root / 'logs') + '"',
                   'SourceDirectory': '"' + str(root / 'empty-source') + '"',
                   'Updates.EnableDatabases': 0, 'Playerbots.Updates.EnableDatabases': 0,
                   'Console.Enable': 0, 'Ra.Enable': 0, 'SOAP.Enabled': 0,
                   'InstantLogout': 0,
                   'Warden.Enabled': 0, 'MapUpdate.Threads': 1, 'ThreadPool': 1,
                   'MaxCoreStuckTime': 0, 'AiPlayerbot.Enabled': 0, 'AiPlayerbot.RandomBotAutologin': 0,
                   'AtlasShop.Enable': 1, 'AtlasFriends.Enable': 0, 'AtlasArmory.Enable': 0,
                   'AtlasChat.Enable': 0, 'Account.Achievements.Enable': 0,
                   'DungeonClear.Enable': 0, 'DungeonClear.DungeonQueueFill.Enable': 0,
                   'AuctionHouseBot.EnableSeller': 0, 'AuctionHouseBot.EnableBuyer': 0,
                   'Transmogrification.Enable': 0})
    etc = root / 'etc'
    etc.mkdir(exist_ok=True)
    (root / 'empty-source').mkdir(exist_ok=True)
    # Start from public distribution defaults, never from the production configuration.
    template = BASE / 'src/server/apps/worldserver/worldserver.conf.dist'
    (etc / 'worldserver.conf').write_text(replace_options(template.read_text(), values))
    for path in (BASE / 'modules').glob('*/conf/*.conf.dist'):
        module_text = path.read_text()
        keys = set(re.findall(r'^\s*([A-Za-z0-9_.]+)\s*=', module_text, re.M))
        (etc / 'modules' / path.name.removesuffix('.dist')).write_text(replace_options(
            module_text, {key: value for key, value in values.items() if key in keys}))
    (etc / 'modules/mod_atlas_shop.conf').write_text('[worldserver]\nAtlasShop.Enable = 1\n')
    api_config = {'Urls': 'http://127.0.0.1:18081', 'LauncherServer': {
        'ConnectionString': 'Server=127.0.0.1;Port=13308;User ID=root;Password=' + password + ';Database=shop_test_auth;SSL Mode=None;',
        'CharacterDatabaseName': 'shop_test_chars', 'WorldDatabaseName': 'shop_test_world',
        'PlayerbotsDatabaseName': 'shop_test_playerbots', 'AvatarMediaRoot': str(root / 'media/avatars'),
        'ChatMediaRoot': str(root / 'media/chat'), 'FeedRoot': str(root / 'feed'),
        'AddonRoot': str(root / 'addons'), 'PublicBaseUrl': 'http://127.0.0.1:18081', 'BrevoApiKey': ''},
        'AtlasShop': {'Purchases': {'RenameEnabled': True, 'RealmId': 1}}}
    (root / 'api-linux/appsettings.Testing.json').write_text(json.dumps(api_config, indent=2) + '\n')
    env = {'PATH': '/usr/local/bin:/usr/bin:/bin', 'HOME': str(root), 'TMPDIR': str(root / 'tmp'),
           'DOTNET_ENVIRONMENT': 'Testing', 'ASPNETCORE_ENVIRONMENT': 'Testing',
           'WOTLK_LAUNCHER_MAX_SCHEMA_VERSION': '12', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
           'AC_UPDATES_ENABLE_DATABASES': '0', 'AC_PLAYERBOTS_UPDATES_ENABLE_DATABASES': '0'}
    (root / 'tmp').mkdir(exist_ok=True)
    children = []
    logs = []

    # This core mutates shared connection info from "." to "localhost" after
    # its first Unix-socket connection, losing the custom socket in later pool
    # connections. Keep the core unchanged: bridge ONLY inside this private net.
    class MySqlBridge(socketserver.BaseRequestHandler):
        def handle(self):
            with sockets.socket(sockets.AF_UNIX, sockets.SOCK_STREAM) as database_socket:
                database_socket.connect(socket)
                peers = (self.request, database_socket)
                while True:
                    readable, _, _ = select.select(peers, (), (), 60)
                    if not readable:
                        continue
                    for peer in readable:
                        data = peer.recv(65536)
                        if not data:
                            return
                        (database_socket if peer is self.request else self.request).sendall(data)

    class BridgeServer(socketserver.ThreadingTCPServer):
        daemon_threads = True
        allow_reuse_address = True

    bridge = BridgeServer(('127.0.0.1', 13308), MySqlBridge)
    threading.Thread(target=bridge.serve_forever, daemon=True).start()

    def start(name, command, cwd):
        log = (root / 'logs' / (name + '-console.log')).open('w')
        logs.append(log)
        process = subprocess.Popen(command, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                   stdout=log, stderr=subprocess.STDOUT)
        children.append(process)
        (root / (name + '.pid')).write_text(str(process.pid) + '\n')
        print('STARTED', name, process.pid, flush=True)
        return process

    def stop(*_):
        raise KeyboardInterrupt()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        api = start('api', [str(root / 'api-linux/WotLK.Launcher.Server')], root / 'api-linux')
        for _ in range(90):
            if api.poll() is not None:
                raise RuntimeError('Test API exited; inspect its private log.')
            try:
                with urllib.request.urlopen('http://127.0.0.1:18081/health', timeout=1) as response:
                    if response.status == 200:
                        break
            except OSError:
                time.sleep(1)
        else:
            raise RuntimeError('Test API did not become healthy.')
        world = start('world', [str(root / 'build/worldserver'), '--config', str(etc / 'worldserver.conf')], root)
        (root / 'fixture.pid').write_text(str(os.getpid()) + '\n')
        if args.hold:
            print('Fixture API healthy; world starting. Hold mode, maximum 45 minutes.', flush=True)
            deadline = time.monotonic() + 2700
            while time.monotonic() < deadline and not (root / 'stop-fixture').exists():
                if api.poll() is not None or world.poll() is not None:
                    raise RuntimeError('A fixture child exited; inspect private logs.')
                time.sleep(1)
        else:
            script = root / 'mod-atlas-shop/tests/test_native_realm.py'
            spec = importlib.util.spec_from_file_location('atlas_native_realm_test', script)
            test = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(test)
            restart_count = 0

            def restart_world():
                nonlocal world, restart_count
                world.terminate()
                world.wait(timeout=45)
                restart_count += 1
                name = 'world-restart-' + str(restart_count)
                world = start(name, [str(root / 'build/worldserver'), '--config', str(etc / 'worldserver.conf')], root)
                (root / 'world.pid').write_text(str(world.pid) + '\n')
                log = root / 'logs' / (name + '-console.log')
                for _ in range(180):
                    if world.poll() is not None:
                        raise RuntimeError('The restarted test world exited.')
                    if '(worldserver-daemon) ready...' in log.read_text(errors='replace'):
                        return
                    time.sleep(1)
                raise RuntimeError('Test world restart timed out.')

            test.run(root, restart_world=restart_world)
    finally:
        for process in reversed(children):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=45)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
        for log in logs:
            log.close()
        bridge.shutdown()
        bridge.server_close()
        print('Fixture API/world stopped.', flush=True)


if __name__ == '__main__':
    main()
