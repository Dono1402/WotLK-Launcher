#!/usr/bin/env python3
"""Start the disposable API/world pair inside a private network namespace.

The test MySQL container is networkless and reached by its dedicated Unix
socket. Production configuration is never copied. Children are stopped on
exit. This fixture has no public listener, real account, money or game client.
"""
import argparse
import configparser
import hashlib
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
    parser.add_argument('--with-hermes', action='store_true', help='Run the isolated Hermes/3.4.3 suite, or add Hermes to --hold.')
    parser.add_argument('--hermes-package', choices=('hermes', 'hermes-disconnect', 'hermes-native'), default='hermes')
    parser.add_argument('--api-package', choices=('api-linux', 'api-candidate', 'api-account-services', 'api-gold'), default='api-linux')
    parser.add_argument('--gold-conversion', action='store_true', help='Exercise gold conversion and the full native rename chain.')
    parser.add_argument('--rename-identity', action='store_true', help='Verify cached player names across rename, gameplay and observer sessions.')
    parser.add_argument('--account-services', action='store_true', help='Test the real native account consumer with four character database workers.')
    parser.add_argument('--world-candidate', type=Path,
                        help='Test an inactive release candidate whose compiled config directory points to this fixture.')
    args = parser.parse_args()
    if args.rename_identity and not (args.account_services and args.with_hermes and not args.gold_conversion):
        parser.error('Rename identity tests require --account-services --with-hermes without --gold-conversion.')
    if args.gold_conversion and not (args.account_services and args.with_hermes and args.api_package == 'api-gold'):
        parser.error('Gold conversion tests require --account-services --with-hermes --api-package api-gold.')
    root = Path(args.root).resolve(strict=True)
    if root.parent != Path('/opt/atlas-shop-tests') or not re.fullmatch(r'rename-[0-9A-Za-z-]+', root.name):
        raise RuntimeError('Expected an existing dedicated test directory.')
    if os.readlink('/proc/self/ns/net') == os.readlink('/proc/1/ns/net'):
        raise RuntimeError('PrivateNetwork=yes is mandatory for the test API/world.')
    os.umask(0o077)
    world_binary = root / ('build-native/worldserver' if args.account_services else 'build/worldserver')
    if args.account_services and not args.world_candidate:
        manifest = json.loads((root / 'build-native/manifest.json').read_text())
        if len(manifest.get('nativeCoreOverlay', [])) != 3 or manifest['configurationDirectory'] != str(root / 'etc'):
            raise RuntimeError('Native fixture requires the verified core overlay and its private configuration directory.')
        with world_binary.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != manifest['worldserverSha256']:
                raise RuntimeError('Native fixture binary differs from its manifest.')
    if args.world_candidate:
        candidate = args.world_candidate.resolve(strict=True)
        if candidate.parent != Path('/opt/arthas-next/candidates') or not re.fullmatch(r'atlas-shop-rename-[0-9A-Za-z-]+', candidate.name):
            raise RuntimeError('Expected a dedicated AtlasShop candidate.')
        manifest = json.loads((candidate / 'build/manifest.json').read_text())
        config_dir = candidate / 'server/etc'
        if not manifest.get('releaseCandidate') or manifest['configurationDirectory'] != str(config_dir):
            raise RuntimeError('Candidate manifest does not declare its isolated configuration directory.')
        if not config_dir.is_symlink() or config_dir.resolve(strict=True) != (root / 'etc').resolve(strict=True):
            raise RuntimeError('Candidate must still use the test-only configuration link, never live module configurations.')
        world_binary = candidate / 'build/worldserver'
        with world_binary.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != manifest['worldserverSha256']:
                raise RuntimeError('Candidate binary differs from its build manifest.')
    api_package = root / args.api_package
    if not (api_package / 'WotLK.Launcher.Server').is_file():
        raise RuntimeError('Expected an existing fixture API package.')
    if not args.hold:
        result_name = 'gold-conversion-result.json' if args.gold_conversion else ('hermes-account-services-result.json' if args.with_hermes else 'native-account-services-result.json') if args.account_services else (
            'hermes-test-result.json' if args.with_hermes else 'native-test-result.json')
        if args.rename_identity:
            result_name = 'hermes-identity-result.json'
        (root / result_name).write_text(json.dumps({'passed': False, 'state': 'starting', 'startedAtUnix': int(time.time())}) + '\n')
    config = configparser.ConfigParser()
    config.read(root / 'mysql-client.cnf')
    password = config['client']['password']
    socket = str(root / 'socket/mysql.sock')
    for _ in range(60):
        if Path(socket).is_socket() and subprocess.run(
                ['mysql', '--defaults-extra-file=' + str(root / 'mysql-client.cnf'), '-NBe', 'SELECT 1;'],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5).returncode == 0:
            break
        time.sleep(1)
    else:
        raise RuntimeError('The disposable MySQL Unix socket did not become ready.')
    databases = {'Login': 'shop_test_auth', 'Character': 'shop_test_chars',
                 'World': 'shop_test_world', 'Playerbots': 'shop_test_playerbots'}
    values = {key + 'DatabaseInfo': '"127.0.0.1;13308;root;' + password + ';' + database + '"'
              for key, database in databases.items()}
    values.update({key + 'Database.WorkerThreads': 1 for key in databases})
    if args.account_services:
        values['CharacterDatabase.WorkerThreads'] = 4
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
    (etc / 'modules/mod_atlas_shop.conf').write_text('[worldserver]\nAtlasShop.Enable = 1\nAtlasShop.AccountServices = '
        + ('1' if args.account_services else '0') + '\nAtlasShop.GoldConversion = ' + ('1' if args.gold_conversion else '0') + '\n')
    api_config = {'Urls': 'http://127.0.0.1:18081', 'LauncherServer': {
        'ConnectionString': 'Server=127.0.0.1;Port=13308;User ID=root;Password=' + password + ';Database=shop_test_auth;SSL Mode=None;',
        'CharacterDatabaseName': 'shop_test_chars', 'WorldDatabaseName': 'shop_test_world',
        'PlayerbotsDatabaseName': 'shop_test_playerbots', 'AvatarMediaRoot': str(root / 'media/avatars'),
        'ChatMediaRoot': str(root / 'media/chat'), 'FeedRoot': str(root / 'feed'),
        'AddonRoot': str(root / 'addons'), 'PublicBaseUrl': 'http://127.0.0.1:18081', 'BrevoApiKey': ''},
        'AtlasShop': {'Purchases': {'RenameEnabled': True, 'RealmId': 1, 'AccountServicesEnabled': args.account_services},
                      'GoldConversion': {'Enabled': args.gold_conversion, 'RealmId': 1}}}
    (api_package / 'appsettings.Testing.json').write_text(json.dumps(api_config, indent=2) + '\n')
    env = {'PATH': '/usr/local/bin:/usr/bin:/bin', 'HOME': str(root), 'TMPDIR': str(root / 'tmp'),
           'DOTNET_ENVIRONMENT': 'Testing', 'ASPNETCORE_ENVIRONMENT': 'Testing',
           'WOTLK_LAUNCHER_MAX_SCHEMA_VERSION': '14' if args.gold_conversion or args.api_package == 'api-gold' else '13' if args.account_services or args.api_package == 'api-account-services' else '12',
           'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
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
        if args.with_hermes:
            from hermes_realm_fixture import configure
            auth_command, hermes_command = configure(root, BASE, values, password, args.hermes_package, args.api_package)
            start('auth', auth_command, root)
            start('hermes', hermes_command, root / args.hermes_package)
        api = start('api', [str(api_package / 'WotLK.Launcher.Server')], api_package)
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
        world = start('world', [str(world_binary), '--config', str(etc / 'worldserver.conf')], root)
        (root / 'fixture.pid').write_text(str(os.getpid()) + '\n')
        if args.hold:
            print('Fixture API healthy; world starting. Hold mode, maximum 45 minutes.', flush=True)
            deadline = time.monotonic() + 2700
            while time.monotonic() < deadline and not (root / 'stop-fixture').exists():
                if any(child.poll() is not None for child in children):
                    raise RuntimeError('A fixture child exited; inspect private logs.')
                time.sleep(1)
        else:
            script_name = 'test_gold_conversion_realm.py' if args.gold_conversion else ('test_hermes_account_services.py' if args.with_hermes else 'test_account_services_realm.py') if args.account_services else (
                'test_hermes_realm.py' if args.with_hermes else 'test_native_realm.py')
            script = root / 'mod-atlas-shop/tests' / script_name
            if args.rename_identity:
                script = root / 'mod-atlas-shop/tests/test_hermes_identity_realm.py'
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
                world = start(name, [str(world_binary), '--config', str(etc / 'worldserver.conf')], root)
                (root / 'world.pid').write_text(str(world.pid) + '\n')
                log = root / 'logs' / (name + '-console.log')
                for _ in range(180):
                    if world.poll() is not None:
                        raise RuntimeError('The restarted test world exited.')
                    if '(worldserver-daemon) ready...' in log.read_text(errors='replace'):
                        return
                    time.sleep(1)
                raise RuntimeError('Test world restart timed out.')

            if args.with_hermes and not args.gold_conversion:
                test.run(root)
            else:
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
