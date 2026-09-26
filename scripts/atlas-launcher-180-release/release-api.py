#!/usr/bin/env python3
"""Prepare 1.8.0 privately; activate only after approval of API restart/schema 15."""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import pwd
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = Path('/opt/atlas-launcher-releases/1.8.0-20260926/api')
UPLOAD = Path('/tmp/atlas-release-180-20260926/api')
CANDIDATE = Path('/opt/wotlk-launcher-api-releases/auth-1.8.0-20260926')
OLD = Path('/opt/wotlk-launcher-api-releases/shop-gold-1.7.2-20260912')
OVERRIDE = Path('/etc/systemd/system/wotlk-launcher-api.service.d/zzzzzzz-atlas-auth-180.conf')
SERVICE = 'wotlk-launcher-api'
PRESERVED = ['arthas-worldserver.dungeon-clear-8224099', 'arthas-authserver', 'hermesproxy-wotlk']
EXPECTED_OLD = '203484f27efc4a5383a1acdb0c9f642e9361456a7b3d69c8d81cd4c86dc2ea0e'


def run(args, **kwargs):
    result = subprocess.run(args, capture_output=True, timeout=90, **kwargs)
    if result.returncode:
        # Environment/configuration and database command output can contain secrets.
        raise RuntimeError('Command failed: ' + args[0] + ' (details withheld)')
    return result.stdout


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')


def state(name):
    return dict(line.split('=', 1) for line in run(['systemctl', 'show', name,
        '-p', 'MainPID,ActiveState,ExecMainStartTimestampMonotonic,NRestarts'], text=True).splitlines())


def environment(pid):
    return dict(x.decode().split('=', 1) for x in Path('/proc', pid, 'environ').read_bytes().split(b'\0') if b'=' in x)


def config_hash(env):
    selected = {k: v for k, v in env.items() if k.startswith(('WOTLK_', 'AtlasShop', 'LauncherServer', 'ASPNETCORE_', 'DOTNET_'))}
    selected['WOTLK_LAUNCHER_MAX_SCHEMA_VERSION'] = '15'
    return hashlib.sha256(json.dumps(selected, sort_keys=True).encode()).hexdigest()


def mysql(sql=None, backup=None):
    info = json.loads(run(['docker', 'inspect', 'arthas-mysql'], text=True))[0]
    assert info['Name'] == '/arthas-mysql' and info['State']['Running']
    env = dict(x.split('=', 1) for x in info['Config']['Env'] if '=' in x)
    password = env['MYSQL_ROOT_PASSWORD'].replace('\\', '\\\\').replace('"', '\\"')
    assert '\n' not in password and '\r' not in password
    cnf = '[client]\nuser=root\npassword="' + password + '"\n'
    args = ['docker', 'exec', '-i', info['Id']]
    if backup is None:
        return run(args + ['mysql', '--defaults-extra-file=/dev/stdin', '-NBe', sql], input=cnf, text=True).strip()
    assert not backup.exists()
    with backup.open('xb') as output:
        result = subprocess.run(args + ['mysqldump', '--defaults-extra-file=/dev/stdin',
            '--single-transaction', '--skip-lock-tables', '--set-gtid-purged=OFF', '--hex-blob',
            '--routines', '--triggers', '--events', '--no-tablespaces', 'arthas_auth'],
            input=cnf.encode(), stdout=output, stderr=subprocess.PIPE, timeout=120)
        output.flush(); os.fsync(output.fileno())
    assert result.returncode == 0 and backup.stat().st_size > 1000, 'Private auth backup failed'
    assert b'-- Dump completed' in backup.read_bytes()[-512:], 'Backup incomplete'


def history():
    return mysql('SELECT version,name,LOWER(HEX(sha256)) FROM arthas_auth.atlas_launcher_schema_history ORDER BY version;')


def config_paths():
    text = run(['systemctl', 'show', SERVICE, '-p', 'EnvironmentFiles', '--value'], text=True)
    import re
    files = [Path(x) for x in re.findall(r'(/[^\s]+) \(ignore_errors=', text)]
    files += [Path('/etc/systemd/system/wotlk-launcher-api.service')]
    files += list(OVERRIDE.parent.glob('*.conf'))
    files += [OLD / 'appsettings.json', Path('/etc/caddy/Caddyfile')]
    return sorted(set(files))


def verify_files(plan):
    for path, digest in plan['configuration'].items():
        if path == '/etc/caddy/Caddyfile' and (ROOT / 'caddy-recovery-policy.json').exists():
            repair = json.loads((ROOT / 'caddy-recovery-policy.json').read_text())
            assert repair['beforeSha256'] == digest and repair['verified']
            digest = repair['afterSha256']
        assert sha(path) == digest, 'Production configuration changed: ' + path
    for name, digest in plan['candidate'].items():
        assert sha(CANDIDATE / name) == digest, 'Candidate changed: ' + name
    for name, digest in plan['preparedConfiguration'].items():
        assert sha(ROOT / name) == digest, 'Prepared configuration changed: ' + name


def prepare():
    assert not ROOT.exists() and not CANDIDATE.exists() and not OVERRIDE.exists()
    before = state(SERVICE)
    assert before['ActiveState'] == 'active'
    executable = Path('/proc', before['MainPID'], 'exe')
    assert executable.resolve() == OLD / 'WotLK.Launcher.Server' and sha(executable) == EXPECTED_OLD
    env = environment(before['MainPID'])
    assert env['WOTLK_LAUNCHER_MAX_SCHEMA_VERSION'] == '14'
    assert env['WOTLK_BREVO_SANDBOX'] == 'false' and env.get('WOTLK_BREVO_API_KEY') and env.get('WOTLK_BREVO_SENDER_EMAIL')
    assert env['WOTLK_PUBLIC_BASE_URL'] == 'https://animeclub.fr/wotlk'
    before_history = history()
    assert [int(row.split('\t')[0]) for row in before_history.splitlines()] == list(range(1, 15))
    assert mysql("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='arthas_auth' AND table_name='atlas_launcher_password_reset';") == '0'
    assert mysql("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='arthas_auth' AND table_type='BASE TABLE' AND engine<>'InnoDB';") == '0'
    inputs = json.loads((UPLOAD / 'inputs.json').read_text())
    assert set(inputs['files']) == {'WotLK.Launcher.Server', 'libSkiaSharp.so', 'appsettings.json'}
    assert shutil.disk_usage('/opt').free > 5_000_000_000
    ROOT.mkdir(parents=True, mode=0o700)
    user = pwd.getpwnam('wotlklauncher')
    CANDIDATE.mkdir(mode=0o750)
    os.chown(CANDIDATE, 0, user.pw_gid)
    CANDIDATE.chmod(0o750)  # umask 077 must not prevent the service group from traversing.
    for name, item in inputs['files'].items():
        source = UPLOAD / name
        assert source.resolve(strict=True) == source and source.stat().st_size == item['bytes'] and sha(source) == item['sha256']
        shutil.copyfile(source, CANDIDATE / name)
        os.chown(CANDIDATE / name, 0, user.pw_gid)
        (CANDIDATE / name).chmod(0o750 if name == 'WotLK.Launcher.Server' else 0o640)
    assert (CANDIDATE / 'WotLK.Launcher.Server').read_bytes()[:4] == b'\x7fELF'
    # Production appsettings remains byte-identical. Never import sample settings.
    shutil.copyfile(OLD / 'appsettings.json', CANDIDATE / 'appsettings.json')
    configuration = {}
    for path in config_paths():
        assert path.resolve(strict=True) == path
        backup = ROOT / 'before' / path.relative_to('/')
        backup.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, backup)
        configuration[str(path)] = sha(path)
        assert sha(backup) == configuration[str(path)]
    (ROOT / 'schema.env').write_text('WOTLK_LAUNCHER_MAX_SCHEMA_VERSION=15\n')
    (ROOT / 'api.override').write_text('[Service]\nExecStart=\nExecStart=' + str(CANDIDATE / 'WotLK.Launcher.Server')
        + '\nWorkingDirectory=' + str(CANDIDATE) + '\nEnvironmentFile=' + str(ROOT / 'schema.env') + '\n')
    plan = dict(version='1.8.0', apiBefore=before, historyBefore=before_history,
        configuration=configuration, sourceCommit=inputs['commit'],
        runtimeConfigurationHash=config_hash(env),
        preparedConfiguration={name: sha(ROOT / name) for name in ('schema.env', 'api.override')},
        migration15Sha256=inputs['migration15Sha256'],
        candidate={p.name: sha(p) for p in CANDIDATE.iterdir()},
        protectedServices={name: state(name) for name in PRESERVED})
    assert all(s['ActiveState'] == 'active' for s in plan['protectedServices'].values())
    assert state(SERVICE) == before
    save(ROOT / 'plan.json', plan)
    print('PASS: API 1.8.0 prepared privately; schema 14 and all services unchanged.', flush=True)


def request(path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request('https://animeclub.fr/wotlk' + path, data=data,
        headers={'Content-Type': 'application/json', 'Cache-Control': 'no-cache'})
    try:
        with urllib.request.urlopen(req, timeout=12) as response:
            return response.status, dict(response.headers), response.read(100000)
    except urllib.error.HTTPError as error:
        return error.code, dict(error.headers), error.read(100000)


def activate():
    # Explicit CLI acknowledgement must accompany user approval for this phase.
    assert len(sys.argv) == 3 and sys.argv[2] == '--approved-api-restart-and-schema15'
    plan = json.loads((ROOT / 'plan.json').read_text())
    assert not OVERRIDE.exists() and not (ROOT / 'activation.json').exists()
    verify_files(plan)
    assert state(SERVICE) == plan['apiBefore'] and history() == plan['historyBefore']
    assert {name: state(name) for name in PRESERVED} == plan['protectedServices']
    backup = ROOT / 'arthas-auth-before-schema15.sql'
    mysql(backup=backup)
    save(ROOT / 'backup.json', dict(bytes=backup.stat().st_size, sha256=sha(backup), completed=True))
    verify_files(plan)
    assert history() == plan['historyBefore']
    assert state(SERVICE) == plan['apiBefore']
    started = time.monotonic()
    # New drop-in sorts after the existing files; all other environment entries stay intact.
    run(['install', '-o', 'root', '-g', 'root', '-m', '0644', str(ROOT / 'api.override'), str(OVERRIDE)])
    run(['systemctl', 'daemon-reload'])
    run(['systemctl', 'restart', SERVICE])
    ready = False
    for _ in range(30):
        try:
            with urllib.request.urlopen('http://127.0.0.1:4323/health', timeout=2) as response:
                ready = response.status == 200 and json.load(response)['status'] == 'ok'
        except (OSError, ValueError):
            pass
        if ready: break
        time.sleep(1)
    assert ready, 'API not healthy; preserve diagnostics and investigate; never restore live SQL automatically'
    verify_activation(round(time.monotonic() - started, 2))


def verify_activation(activation_seconds=None):
    plan = json.loads((ROOT / 'plan.json').read_text())
    assert sha(OVERRIDE) == plan['preparedConfiguration']['api.override']
    assert sha(ROOT / 'arthas-auth-before-schema15.sql') == json.loads((ROOT / 'backup.json').read_text())['sha256']
    after = state(SERVICE)
    assert after['ActiveState'] == 'active' and after['MainPID'] != plan['apiBefore']['MainPID']
    assert Path('/proc', after['MainPID'], 'exe').resolve() == CANDIDATE / 'WotLK.Launcher.Server'
    assert sha(Path('/proc', after['MainPID'], 'exe')) == plan['candidate']['WotLK.Launcher.Server']
    assert environment(after['MainPID'])['WOTLK_LAUNCHER_MAX_SCHEMA_VERSION'] == '15'
    assert config_hash(environment(after['MainPID'])) == plan['runtimeConfigurationHash'], 'API runtime configuration differs'
    rows = history().splitlines()
    assert '\n'.join(rows[:-1]) == plan['historyBefore']
    assert rows[-1].split('\t') == ['15', 'password_recovery', plan['migration15Sha256']]
    code, headers, body = request('/api/v1/auth/password-reset')
    assert code == 200 and b'Atlas' in body and 'no-store' in headers.get('Cache-Control', '')
    assert 'nonce-' in headers.get('Content-Security-Policy', '')
    assert request('/api/v1/auth/password-reset/request', {'email': ''})[0] == 400
    assert request('/api/v1/auth/password-reset/confirm', {'token': 'invalid', 'password': 'verification-only-180', 'confirmation': 'verification-only-180'})[0] == 400
    verify_files(plan)
    assert {name: state(name) for name in PRESERVED} == plan['protectedServices'], 'Protected service changed'
    save(ROOT / 'activation.json', dict(activated=True, version='1.8.0', schemaVersion=15,
        sourceCommit=plan['sourceCommit'], apiSha256=plan['candidate']['WotLK.Launcher.Server'],
        checksCompletedInSeconds=activation_seconds, protectedServicesUnchanged=True,
        previousMigrationsUnchanged=True, productionEmailSent=False, publicRoutesVerified=True))
    print('PASS: API 1.8.0/schema 15 active; reset routes checked; game/Auth/Hermes processes unchanged.', flush=True)


if __name__ == '__main__':
    os.umask(0o077)
    assert os.geteuid() == 0 and len(sys.argv) >= 2 and sys.argv[1] in ('prepare', 'activate', 'verify-active')
    with open('/run/lock/atlas-launcher-api-release.lock', 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        {'prepare': prepare, 'activate': activate, 'verify-active': verify_activation}[sys.argv[1]]()
