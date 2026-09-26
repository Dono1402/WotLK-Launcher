#!/usr/bin/env python3
"""Authorized September 26 cutover. Explicit phases, no automatic SQL restore."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import time
import urllib.request

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260926')
OLD = Path('/opt/arthas-next/candidates/atlas-all-update-20260912')
DEPLOY = ROOT / 'deployment-20260926'
PRIVATE = DEPLOY / 'private'
HERMES = Path('/opt/hermesproxy-wotlk/releases/hermes-all-update-20260926')
AUTH_CONFIG = Path('/opt/arthas-next/candidates/dungeon-clear-20260830T183457Z/server/etc/authserver.conf')
WORLD = 'arthas-worldserver.dungeon-clear-8224099'
AUTH = 'arthas-authserver'
PROXY = 'hermesproxy-wotlk'
UNITS = (WORLD, AUTH, PROXY, 'wotlk-launcher-api', 'wotlk-launcher-server')
DROP = 'zzzzzzzzz-atlas-all-update-20260926.conf'
DB = Path('/opt/arthas/mysql')
EXPECTED = {
    'worldserver': '3bca64f7bda124478ccd100891d897ececd0b34db60e2ff302bf43b4ab92e577',
    'authserver': '6fb632eef9426d8cc10f46f22ae7baf60ae053693d0e351fffe3eb0e51b499a9',
    'HermesProxy': '22eb4afad62d73d8eeb725dbf72cc765f7e76dfd4125b0cf20f5bf6fe4511b5c',
}


def now():
    return datetime.now(timezone.utc).isoformat()


def run(args, **kwargs):
    return subprocess.check_output([str(x) for x in args], text=True, **kwargs).strip()


def read(path):
    return json.loads(Path(path).read_text())


def save(name, data):
    target = DEPLOY / name
    temp = target.with_suffix(target.suffix + '.tmp')
    with temp.open('w') as stream:
        json.dump(data, stream, indent=2)
        stream.write('\n')
        stream.flush()
        os.fsync(stream.fileno())
    temp.replace(target)


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def show(unit):
    result = {}
    for line in run(['systemctl', 'show', unit,
            '-p', 'ActiveState,SubState,MainPID,NRestarts,ExecStart,WorkingDirectory,FragmentPath,DropInPaths,EnvironmentFiles']).splitlines():
        key, value = line.split('=', 1)
        result[key] = (result.get(key, '') + ' ' + value).strip()
    return result


def dbinfo():
    info = read_json_command(['docker', 'inspect', 'arthas-mysql'])[0]
    if not any(m['Source'] == str(DB) and m['Destination'] == '/var/lib/mysql' for m in info['Mounts']):
        raise RuntimeError('Production database mount changed.')
    return info


def read_json_command(args):
    return json.loads(run(args))


def stopped(database=False):
    for unit in UNITS:
        state = show(unit)
        if state['ActiveState'] != 'inactive' or state['MainPID'] != '0':
            raise RuntimeError('Expected stopped service: ' + unit)
    if database and dbinfo()['State']['Running']:
        raise RuntimeError('Expected stopped production MySQL.')


def verify_hashes(values):
    for path, expected in values.items():
        if digest(path) != expected:
            raise RuntimeError('File drift: ' + path)


def mysql(sql=None, raw=None, database=None):
    args = ['mysql', '--defaults-extra-file=' + str(PRIVATE / 'mysql-client.cnf')]
    if database:
        if database != 'arthas_world':
            raise RuntimeError('Only the reviewed World migrations are allowed.')
        args.append(database)
    if sql is not None:
        args += ['-NBe', sql]
    result = subprocess.run(args, input=raw, capture_output=True, timeout=180)
    if result.returncode:
        (PRIVATE / 'mysql-last-error.log').write_bytes(result.stderr)
        raise RuntimeError('SQL failed; diagnostics saved privately.')
    return result.stdout.decode().strip()


def prepare_backup():
    import grp

    stopped(database=True)
    if DEPLOY.exists() or HERMES.exists() or (ROOT / 'server/etc-production').exists():
        raise RuntimeError('Refuse to overwrite an existing preparation.')
    if DB.resolve(strict=True) != DB or shutil.disk_usage(ROOT).free < 40 * 1024**3:
        raise RuntimeError('Unexpected database path or insufficient disk reserve.')
    for name in ('atlas-update-migration-20260926-mysql', 'atlas-update-fixture-20260926-mysql'):
        if read_json_command(['docker', 'inspect', name])[0]['State']['Running']:
            raise RuntimeError('An isolated test database is still running.')
    baseline = read(ROOT / 'private/baseline.json')
    verify_hashes(baseline)
    package = read(ROOT / 'hermes/package-manifest.json')['files']
    verify_hashes({str(ROOT / 'hermes/publish' / p): h for p, h in package.items()})
    for name in ('worldserver', 'authserver'):
        if digest(ROOT / 'server/bin' / name) != EXPECTED[name]:
            raise RuntimeError('Untested binary: ' + name)
    if digest(ROOT / 'hermes/publish/HermesProxy') != EXPECTED['HermesProxy']:
        raise RuntimeError('Untested Hermes binary.')
    if (ROOT / 'server/etc').resolve(strict=True) != Path('/opt/atlas-shop-tests/rename-20260926/etc'):
        raise RuntimeError('Unexpected candidate configuration link.')
    DEPLOY.mkdir(mode=0o700)
    PRIVATE.mkdir(mode=0o700)
    states = {u: show(u) for u in UNITS}
    info = dbinfo()
    # Private container metadata is needed for disaster recovery, never for publication.
    save('private/container.json', info)
    files = set(baseline) | {str(AUTH_CONFIG), '/etc/hermesproxy-wotlk.env',
        '/opt/hermesproxy-wotlk/appsettings.atlas.json', '/usr/local/sbin/atlas-sync-hermes-quests'}
    for state in states.values():
        files.add(state['FragmentPath'])
        files.update(state['DropInPaths'].split())
        files.update(re.findall(r'(/[^\s;()]+)\s+\(ignore_errors=', state.get('EnvironmentFiles', '')))
    for folder in ('/opt/hermesproxy-wotlk/AccountData', '/opt/hermesproxy-wotlk/certs', '/opt/arthas-next/server/logs'):
        files.update(str(p) for p in Path(folder).rglob('*') if p.is_file())
    backed = {}
    for name in sorted(files):
        path = Path(name)
        target = PRIVATE / 'files' / path.relative_to('/')
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        backed[name] = digest(path)
        if digest(target) != backed[name]:
            raise RuntimeError('File backup differs: ' + name)
    save('private/file-backup.json', backed)
    print('Copying stopped MySQL; all application writers remain stopped.', flush=True)
    target = PRIVATE / 'mysql-cold-backup'
    target.mkdir()
    run(['cp', '-a', '--reflink=auto', str(DB) + '/.', target])
    difference = run(['rsync', '-aHnci', '--numeric-ids', str(DB) + '/', str(target) + '/'])
    stopped(database=True)
    if difference:
        raise RuntimeError('Cold backup checksum differs.')
    save('backup-result.json', {'completedAt': now(), 'passed': True, 'coldCopyChecksumVerified': True,
        'crossDatabaseConsistent': True, 'databasePath': str(target), 'fileCount': len(backed),
        'restoreRehearsed': False, 'automaticRestore': False, 'freeBytes': shutil.disk_usage(ROOT).free})
    print('PASS cold backup: content, metadata, all writers stopped.', flush=True)
    env = dict(x.split('=', 1) for x in info['Config']['Env'] if '=' in x)
    password = env['MYSQL_ROOT_PASSWORD'].replace('\\', '\\\\').replace('"', '\\"')
    if '\n' in password or '\r' in password:
        raise RuntimeError('Unsupported credential format.')
    cnf = PRIVATE / 'mysql-client.cnf'
    cnf.write_text('[client]\nhost=127.0.0.1\nport=3306\nprotocol=TCP\nuser=root\npassword="' + password + '"\n')
    cnf.chmod(0o600)
    new_etc = ROOT / 'server/etc-production'
    shutil.copytree(OLD / 'server/etc', new_etc)
    shutil.copy2(AUTH_CONFIG, new_etc / 'authserver.conf')
    staged = {}
    group = grp.getgrnam('acore').gr_gid
    ROOT.chmod(0o711)
    for p in [ROOT / 'server', ROOT / 'server/bin', new_etc, *new_etc.rglob('*')]:
        if p.is_symlink():
            raise RuntimeError('Unexpected staged configuration symlink.')
        os.chown(p, 0, group)
        p.chmod(0o750 if p.is_dir() else 0o640)
        if p.is_file():
            staged[str(p)] = digest(p)
    for name in ('worldserver', 'authserver'):
        p = ROOT / 'server/bin' / name
        os.chown(p, 0, group)
        p.chmod(0o550)
        staged[str(p)] = digest(p)
    shutil.copytree(ROOT / 'hermes/publish', HERMES)
    hg = grp.getgrnam('hermesproxy').gr_gid
    for p in [HERMES, *HERMES.rglob('*')]:
        if p.is_symlink():
            raise RuntimeError('Unexpected Hermes package symlink.')
        os.chown(p, 0, hg)
        p.chmod(0o750 if p.is_dir() or p.name == 'HermesProxy' else 0o640)
        if p.is_file():
            staged[str(p)] = digest(p)
    # Shared account persistence remains outside the immutable release.
    (HERMES / 'AccountData').symlink_to('/opt/hermesproxy-wotlk/AccountData', target_is_directory=True)
    logs = HERMES / 'Logs'
    logs.mkdir()
    shutil.chown(logs, user='hermesproxy', group='hermesproxy')
    logs.chmod(0o750)
    for user, p, access in [('acore', new_etc / 'worldserver.conf', '-r'),
            ('acore', new_etc / 'authserver.conf', '-r'), ('acore', ROOT / 'server/bin/worldserver', '-x'),
            ('hermesproxy', HERMES / 'HermesProxy', '-x'), ('hermesproxy', HERMES / 'AccountData', '-w')]:
        run(['runuser', '-u', user, '--', 'test', access, p])
    migrations = []
    for name in read(ROOT / 'inputs/world-migrations.json'):
        path = ROOT / 'core' / name
        if path.parent != ROOT / 'core/data/sql/updates/db_world':
            raise RuntimeError('Unexpected SQL path.')
        migrations.append({'path': name, 'sha256': digest(path), 'sha1': hashlib.sha1(path.read_bytes()).hexdigest().upper()})
    if len(migrations) != 62:
        raise RuntimeError('Expected 62 reviewed World migrations.')
    drops = {}
    for unit, binary, conf in [(WORLD, ROOT / 'server/bin/worldserver', ROOT / 'server/etc/worldserver.conf'),
            (AUTH, ROOT / 'server/bin/authserver', ROOT / 'server/etc/authserver.conf'),
            (PROXY, HERMES / 'HermesProxy', Path('/opt/hermesproxy-wotlk/appsettings.atlas.json'))]:
        if any(Path(p).name >= DROP for p in states[unit]['DropInPaths'].split()):
            raise RuntimeError('Later service override needs review.')
        text = '[Service]\nExecStart=\nExecStart=' + str(binary) + ' --config ' + str(conf) + '\n'
        if unit == PROXY:
            text += 'WorkingDirectory=' + str(HERMES) + '\n'
        p = DEPLOY / (unit + '.override.conf')
        p.write_text(text)
        staged[str(p)] = digest(p)
        drops[unit] = str(p)
    save('prepared.json', {'at': now(), 'services': states, 'originalHashes': baseline,
        'stagedHashes': staged, 'migrations': migrations, 'drops': drops, 'containerId': info['Id'],
        'authorization': 'User: vasy fait tout, after explicit production cutover plan.'})
    print('PASS staged production configuration, binaries, Hermes persistence and overrides.', flush=True)


def prepared():
    data = read(DEPLOY / 'prepared.json')
    verify_hashes(data['originalHashes'])
    verify_hashes(data['stagedHashes'])
    if dbinfo()['Id'] != data['containerId']:
        raise RuntimeError('Database container identity changed.')
    return data


def audit_backup():
    """Independent coverage audit, including every repeated systemd EnvironmentFiles row."""
    stopped(database=True)
    data = prepared()
    backed = read(PRIVATE / 'file-backup.json')
    for unit in UNITS:
        state = show(unit)
        data['services'][unit] = state
        files = [state['FragmentPath'], *state['DropInPaths'].split(),
            *re.findall(r'(/[^\s;()]+)\s+\(ignore_errors=', state.get('EnvironmentFiles', ''))]
        for name in files:
            path = Path(name)
            target = PRIVATE / 'files' / path.relative_to('/')
            if name not in backed:
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(path, target)
                backed[name] = digest(path)
            if digest(path) != backed[name] or digest(target) != backed[name]:
                raise RuntimeError('Original or backup configuration drift: ' + name)
            data['originalHashes'][name] = backed[name]
    save('private/file-backup.json', backed)
    save('prepared.json', data)
    backup = read(DEPLOY / 'backup-result.json')
    backup.update(fileCount=len(backed), serviceFileCoverageAuditedAt=now())
    save('backup-result.json', backup)
    print('PASS backup coverage for every service, drop-in and environment file.', flush=True)


def migrate():
    data = prepared()
    stopped(database=True)
    backup = read(DEPLOY / 'backup-result.json')
    if not backup['passed'] or not backup.get('serviceFileCoverageAuditedAt') or (DEPLOY / 'migration-result.json').exists():
        raise RuntimeError('Verified backup and no previous migration attempt required.')
    # Recheck the untouched rollback copy immediately before changing production.
    if run(['rsync', '-aHnci', '--numeric-ids', str(DB) + '/', str(PRIVATE / 'mysql-cold-backup') + '/']):
        raise RuntimeError('Cold backup drift before activation.')
    save('mysql-started.json', {'at': now()})
    run(['docker', 'start', data['containerId']])
    for _ in range(120):
        try:
            mysql('SELECT 1')
            break
        except RuntimeError:
            time.sleep(1)
    else:
        raise RuntimeError('Production MySQL failed readiness.')
    # Auth keeps its existing updater setting. Confirm no unreviewed SQL is pending.
    auth_rows = dict(line.split('\t', 1) for line in mysql('SELECT name,hash FROM arthas_auth.updates').splitlines())
    auth_pending = []
    for p in (ROOT / 'core/data/sql/updates/db_auth').glob('*.sql'):
        if auth_rows.get(p.name, '').upper() != hashlib.sha1(p.read_bytes()).hexdigest().upper():
            auth_pending.append(p.name)
    save('auth-sql-audit.json', {'passed': not auth_pending, 'pendingOrDifferent': auth_pending})
    if auth_pending:
        raise RuntimeError('Auth updater would encounter unreviewed migrations; inspect before continuing.')
    checksum = mysql('CHECKSUM TABLE arthas_chars.guild,arthas_chars.guild_member,arthas_chars.characters,arthas_chars.pet_spell;')
    save('private/characters-before.json', {'checksums': checksum})
    result = {'startedAt': now(), 'applied': [], 'alreadyApplied': [], 'passed': False}
    save('migration-result.json', result)
    for item in data['migrations']:
        path = ROOT / 'core' / item['path']
        if digest(path) != item['sha256']:
            raise RuntimeError('Reviewed SQL changed.')
        existing = mysql("SELECT hash FROM arthas_world.updates WHERE name='" + path.name + "'")
        if existing:
            if existing.upper() != item['sha1']:
                raise RuntimeError('Recorded SQL hash differs: ' + path.name)
            result['alreadyApplied'].append(path.name)
            continue
        stopped()
        started = time.monotonic()
        mysql(raw=path.read_bytes(), database='arthas_world')
        elapsed = int((time.monotonic() - started) * 1000)
        mysql("INSERT INTO arthas_world.updates (name,hash,state,speed) VALUES ('" + path.name + "','" + item['sha1'] + "','RELEASED'," + str(elapsed) + ')')
        result['applied'].append({'name': path.name, 'sha256': item['sha256'], 'elapsedMs': elapsed})
        save('migration-result.json', result)
        print('APPLIED', path.name, flush=True)
    if mysql('CHECKSUM TABLE arthas_chars.guild,arthas_chars.guild_member,arthas_chars.characters,arthas_chars.pet_spell;') != checksum:
        raise RuntimeError('Character data changed while writers should be stopped.')
    result.update(passed=True, completedAt=now(), characterChecksumsUnchanged=True)
    save('migration-result.json', result)


def activate_core():
    data = prepared()
    stopped()
    result = read(DEPLOY / 'migration-result.json')
    if not result['passed'] or len(result['applied']) + len(result['alreadyApplied']) != 62:
        raise RuntimeError('62 reconciled SQL migrations required.')
    link = ROOT / 'server/etc'
    if not link.is_symlink() or link.resolve(strict=True) != Path('/opt/atlas-shop-tests/rename-20260926/etc'):
        raise RuntimeError('Unexpected pre-activation configuration link.')
    temp = ROOT / 'server/etc.activation-link'
    temp.symlink_to(ROOT / 'server/etc-production', target_is_directory=True)
    temp.replace(link)
    for unit, source in data['drops'].items():
        directory = Path('/etc/systemd/system') / (unit + '.service.d')
        directory.mkdir(exist_ok=True)
        directory.chmod(0o755)
        if directory.resolve(strict=True) != directory:
            raise RuntimeError('Unexpected override directory.')
        with (directory / DROP).open('x') as stream:
            stream.write(Path(source).read_text())
        (directory / DROP).chmod(0o644)
    run(['systemctl', 'daemon-reload'])
    for unit in data['drops']:
        if Path(show(unit)['DropInPaths'].split()[-1]).name != DROP:
            raise RuntimeError('New override is not effective.')
    save('activation-started.json', {'at': now(), 'expectedHashes': EXPECTED})
    run(['systemctl', 'start', AUTH], timeout=60)
    run(['systemctl', 'start', WORLD], timeout=60)
    print('Auth and World started. Hermes and launcher still gated on readiness.', flush=True)


def port_open(port):
    try:
        with socket.create_connection(('127.0.0.1', port), timeout=2):
            return True
    except OSError:
        return False


def core_ready():
    counts = []
    for table, protocol in [('atlas_shop_delivery_health', 2), ('atlas_shop_conversion_health', 1)]:
        counts.append(mysql('SELECT COUNT(*) FROM arthas_auth.' + table + ' WHERE realm_id=1 AND protocol=' + str(protocol)
            + " AND character_database='arthas_chars' AND last_seen_at BETWEEN UTC_TIMESTAMP(6)-INTERVAL 30 SECOND AND UTC_TIMESTAMP(6)+INTERVAL 5 SECOND") == '1')
    return all(counts) and port_open(4000) and port_open(3724)


def open_services():
    prepared()
    if not core_ready():
        raise RuntimeError('Core readiness and shop heartbeats required.')
    for unit in (PROXY, 'wotlk-launcher-api', 'wotlk-launcher-server'):
        if show(unit)['ActiveState'] != 'inactive':
            raise RuntimeError('Expected stopped service: ' + unit)
        run(['systemctl', 'start', unit], timeout=60)
    save('frontends-started.json', {'at': now()})
    print('Hermes, launcher API and launcher server started.', flush=True)


def healthy_body(port, body):
    if port == 4322:
        return body.strip() == 'ok'
    return json.loads(body).get('status') == 'ok'


def verify():
    prepared()
    result = {'checkedAt': now(), 'services': {}, 'coreReady': core_ready(), 'http': {}}
    for unit in UNITS:
        s = show(unit)
        p = Path('/proc/' + s['MainPID'] + '/exe')
        if s['ActiveState'] != 'active' or not p.exists():
            raise RuntimeError('Service is not active: ' + unit)
        row = {'pid': int(s['MainPID']), 'restarts': int(s['NRestarts']), 'executable': str(p.resolve()), 'sha256': digest(p)}
        if unit in (WORLD, AUTH, PROXY):
            name = {WORLD: 'worldserver', AUTH: 'authserver', PROXY: 'HermesProxy'}[unit]
            if row['sha256'] != EXPECTED[name]:
                raise RuntimeError('Wrong active executable: ' + unit)
        result['services'][unit] = row
    for port in (4322, 4323):
        with urllib.request.urlopen('http://127.0.0.1:' + str(port) + '/health', timeout=5) as response:
            body = response.read().decode()
            result['http'][str(port)] = {'status': response.status, 'healthy': healthy_body(port, body)}
    result['ports'] = {str(p): port_open(p) for p in (1119, 8081, 8084, 8086, 8099)}
    result['realm'] = mysql('SELECT id,port,flag,gamebuild FROM arthas_auth.realmlist WHERE id=1').split('\t')
    result['onlineByAccountType'] = mysql('SELECT COALESCE(p.account_type,0),COUNT(*) FROM arthas_chars.characters c LEFT JOIN arthas_playerbots.playerbots_account_type p ON p.account_id=c.account WHERE c.online=1 GROUP BY p.account_type')
    result['freeDiskBytes'] = shutil.disk_usage(ROOT).free
    result['passed'] = (result['coreReady'] and all(result['ports'].values())
        and all(x['healthy'] for x in result['http'].values()) and result['realm'] == ['1', '4000', '0', '12340'])
    save('runtime-result.json', result)
    print(json.dumps(result), flush=True)
    if not result['passed']:
        raise RuntimeError('Runtime checks incomplete.')


def close_credentials():
    prepared()
    report = read(DEPLOY / 'deployment-result.json')
    if not report['operationalChecksPassed']:
        raise RuntimeError('A completed operational report is required.')
    for unit, expected in report['runtime']['services'].items():
        state = show(unit)
        if state['ActiveState'] != 'active' or int(state['MainPID']) != expected['pid'] or int(state['NRestarts']) != expected['restarts']:
            raise RuntimeError('Service drift before credential cleanup: ' + unit)
    count = int(mysql('SELECT COUNT(*) FROM arthas_chars.characters c JOIN arthas_playerbots.playerbots_account_type p ON p.account_id=c.account WHERE c.online=1 AND p.account_type=1'))
    text = Path('/opt/arthas-next/server/logs/Server.log').read_text(errors='replace')
    errors = len(re.findall(r'(?m)^.*\[1062\].*$', text))
    expected_errors = report['latestObservation']['worldLogErrors']['Server.log']['sql1062']
    if count != 1000 or errors != expected_errors or not core_ready():
        raise RuntimeError('Runtime evidence changed; review before closing.')
    path = PRIVATE / 'mysql-client.cnf'
    if path.parent.resolve(strict=True) != PRIVATE or path.is_symlink() or not path.is_file():
        raise RuntimeError('Unexpected temporary credential path.')
    path.unlink()
    result = {'at': now(), 'temporaryMysqlCredentialsRemoved': True, 'onlineRandomBots': count,
        'sql1062IncidentsStill': errors, 'servicePidsAndRestartCountsUnchanged': True}
    save('credential-cleanup.json', result)
    print(json.dumps(result), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=['prepare-backup', 'audit-backup', 'migrate', 'activate-core', 'open-services', 'verify', 'close-credentials'])
    args = parser.parse_args()
    os.umask(0o077)
    if os.geteuid() != 0 or ROOT.resolve(strict=True) != ROOT:
        raise RuntimeError('Expected root and the exact candidate directory.')
    globals()[args.phase.replace('-', '_')]()
