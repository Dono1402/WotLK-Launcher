#!/usr/bin/env python3
"""Configure disposable auth/Hermes instances for protocol tests without a UI."""
import json
from pathlib import Path
import secrets

AUTH = Path('/opt/arthas-next/candidates/dungeon-clear-20260830T183457Z/server/bin/authserver')


def configure(root, base, values, password, package='hermes', api_package='api-linux'):
    from run_realm_fixture import replace_options
    auth_values = {key: value for key, value in values.items() if key.startswith('LoginDatabase')}
    auth_values.update({'RealmServerPort': 13724, 'BindIP': '"127.0.0.1"',
        'LogsDir': '"' + str(root / 'logs/auth') + '"', 'Updates.EnableDatabases': 0,
        'SourceDirectory': '"' + str(root / 'empty-source') + '"', 'PidFile': '""'})
    (root / 'logs/auth').mkdir(exist_ok=True)
    config = root / 'etc/authserver.conf'
    config.write_text(replace_options((base / 'src/server/apps/authserver/authserver.conf.dist').read_text(), auth_values))
    if package not in ('hermes', 'hermes-disconnect', 'hermes-native'):
        raise ValueError('Expected a reviewed fixture Hermes package name.')
    hermes = root / package
    if not (hermes / 'HermesProxy').is_file():
        raise RuntimeError('Place the reviewed Hermes package in the dedicated fixture directory first.')
    secret = secrets.token_hex(32)
    if api_package not in ('api-linux', 'api-candidate', 'api-account-services'):
        raise ValueError('Expected a reviewed fixture API package name.')
    api_path = root / api_package / 'appsettings.Testing.json'
    api_config = json.loads(api_path.read_text())
    api_config['LauncherServer'].update({'HermesSharedSecret': secret,
        'HermesTicketUrl': 'http://127.0.0.1:18099/internal/launcher-ticket/'})
    api_path.write_text(json.dumps(api_config, indent=2) + '\n')
    hermes_config = {
        'ClientOptions': {'ClientBuild': 'V3_4_3_54261', 'SeedHex': '179D3DC3235629D07113A9B3867F97A7',
                          'ReportedOS': 'Win', 'ReportedPlatform': 'x86', 'RequireDeathKnightLevel': False},
        'LegacyServerOptions': {'Build': 'V3_3_5a_12340', 'Address': '127.0.0.1', 'Port': 13724},
        'ProxyNetworkOptions': {'ExternalAddress': '127.0.0.1', 'RestPort': 8081, 'BNetPort': 1119,
                                'RealmPort': 8084, 'InstancePort': 8086},
        'AzerothCoreBridgeOptions': {'Enabled': True, 'InternalTicketSharedSecret': secret,
            'InternalTicketPort': 18099, 'ConnectionString':
            'Server=127.0.0.1;Port=13308;User ID=root;Password=' + password + ';Database=shop_test_auth;SSL Mode=None;'},
        'DiagnosticsOptions': {'PacketsLog': False, 'EnableMetrics': False, 'EnableVersionCheck': False,
                               'ForwardTransportsV343': True},
        'LoggingOptions': {'MinimumLevel': 'Information', 'ToFile': True, 'Directory': str(root / 'logs/hermes')}
    }
    (hermes / 'appsettings.json').write_text('{}\n')
    (hermes / 'appsettings.Testing.json').write_text(json.dumps(hermes_config, indent=2) + '\n')
    return [str(AUTH), '--config', str(config)], [str(hermes / 'HermesProxy')]
