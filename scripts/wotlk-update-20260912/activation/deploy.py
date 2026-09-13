#!/usr/bin/env python3
"""Explicitly authorized Atlas cutover, in reviewable phases; never restore SQL automatically."""
import argparse
from datetime import datetime, timezone
import grp
import gzip
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import threading
import time
import urllib.request

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260912')
DEPLOY = ROOT / 'deployment-20260913'
HERMES = Path('/opt/hermesproxy-wotlk/releases/hermes-all-update-20260912')
WORLD = 'arthas-worldserver.dungeon-clear-8224099'
PROXY = 'hermesproxy-wotlk'
PRESERVED = ('arthas-authserver', 'wotlk-launcher-api')
DROP_NAME = 'zzzzzzzzz-atlas-all-update-20260912.conf'
DATABASES = ('arthas_world', 'arthas_chars', 'arthas_playerbots', 'arthas_auth')


def now():
    return datetime.now(timezone.utc).isoformat()


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    return json.loads(Path(path).read_text())


def save(name, value):
    path = DEPLOY / name
    temporary = path.with_suffix(path.suffix + '.tmp')
    with temporary.open('w') as stream:
        json.dump(value, stream, indent=2)
        stream.write('\n')
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


def run(command, **kwargs):
    return subprocess.run(command, check=True, capture_output=True, text=True, **kwargs).stdout


def show(unit):
    properties = ('ActiveState,MainPID,NRestarts,WorkingDirectory,ExecStart,DropInPaths,'
                  'FragmentPath,User,Group,EnvironmentFiles')
    return dict(line.split('=', 1) for line in run(
        ['systemctl', 'show', unit, '--property=' + properties]).splitlines())


def config_options(path):
    result = {}
    for line in Path(path).read_text().splitlines():
        if '=' in line and not line.lstrip().startswith('#'):
            key, value = line.split('=', 1)
            result[key.strip()] = value.strip().strip('"')
    return result


def mysql(sql=None, raw=None, database=None):
    command = ['mysql', '--defaults-extra-file=' + str(DEPLOY / 'private/mysql-client.cnf')]
    if database:
        if database not in DATABASES:
            raise RuntimeError('Unexpected database.')
        command.append(database)
    if sql is not None:
        command += ['-NBe', sql]
    result = subprocess.run(command, input=raw, capture_output=True, timeout=180)
    if result.returncode:
        # SQL diagnostics remain private and never include the credential file.
        (DEPLOY / 'private/mysql-last-error.log').write_bytes(result.stderr)
        raise RuntimeError('Database command failed; inspect protected mysql-last-error.log.')
    return result.stdout.decode().strip()


def assert_stopped():
    for unit in (PROXY, WORLD):
        state = show(unit)
        if state['MainPID'] != '0' or state['ActiveState'] not in ('inactive', 'failed'):
            raise RuntimeError('Expected stopped service: ' + unit)


def verify_preserved():
    before = read(DEPLOY / 'prepared.json')
    for unit in PRESERVED:
        state = show(unit)
        initial = before['services'][unit]
        if (state['ActiveState'] != 'active' or state['MainPID'] != initial['MainPID']
                or digest('/proc/' + state['MainPID'] + '/exe') != initial['sha256']):
            raise RuntimeError('Preserved service changed: ' + unit)


def verify_files():
    prepared = read(DEPLOY / 'prepared.json')
    for path, expected in prepared['fileHashes'].items():
        if digest(path) != expected:
            raise RuntimeError('Prepared file changed: ' + path)
    for path, expected in prepared['originalFileHashes'].items():
        if digest(path) != expected:
            raise RuntimeError('Original configuration changed: ' + path)
    verify_preserved()
    return prepared


def prepare():
    if DEPLOY.exists() or HERMES.exists() or (ROOT / 'server/etc-production').exists():
        raise RuntimeError('Preparation already exists; inspect it instead of overwriting.')
    if shutil.disk_usage(ROOT).free < 12 * 1024**3:
        raise RuntimeError('Insufficient backup reserve.')
    baseline = read(ROOT / 'evidence/runtime-before.json')
    service_files = read(ROOT / 'evidence/service-configuration-files.json')
    build = read(ROOT / 'build/manifest.json')
    package = read(ROOT / 'hermes/package-manifest.json')
    if build['productionActivationPerformed']:
        raise RuntimeError('Candidate already activated.')
    if (ROOT / 'server/etc').resolve(strict=True) != Path('/opt/atlas-shop-tests/rename-20260911/etc'):
        raise RuntimeError('Expected the inactive fixture configuration link.')
    states, originals = {}, {}
    for unit in (WORLD, PROXY, *PRESERVED):
        state = show(unit)
        expected = baseline['services'][unit]
        if (state['ActiveState'] != 'active' or state['MainPID'] != expected['MainPID']
                or digest('/proc/' + state['MainPID'] + '/exe') != expected['sha256']):
            raise RuntimeError('Runtime drift: ' + unit)
        state['sha256'] = expected['sha256']
        state['executable'] = expected['executable']
        states[unit] = state
        if unit in (WORLD, PROXY):
            directory = Path('/etc/systemd/system') / (unit + '.service.d')
            if directory.resolve(strict=True) != directory or (directory / DROP_NAME).exists():
                raise RuntimeError('Unexpected drop-in destination.')
            if any(Path(p).name >= DROP_NAME for p in state['DropInPaths'].split()):
                raise RuntimeError('A later drop-in needs review.')
    args = Path('/proc/' + states[WORLD]['MainPID'] + '/cmdline').read_bytes().decode().split('\0')
    old_etc = Path(args[args.index('--config') + 1]).resolve(strict=True).parent
    for name, expected in baseline['configurations'].items():
        path = old_etc / name
        if digest(path) != expected:
            raise RuntimeError('World configuration drift: ' + name)
        originals[str(path)] = expected
    for data in service_files.values():
        for item in data['preservedFiles']:
            if digest(item['path']) != item['sha256']:
                raise RuntimeError('Service file drift: ' + item['path'])
            originals[item['path']] = item['sha256']
    if digest(ROOT / 'server/bin/worldserver') != build['worldserverSha256']:
        raise RuntimeError('Candidate World differs from the tested binary.')
    for name, expected in package['files'].items():
        if digest(ROOT / 'hermes/publish' / name) != expected:
            raise RuntimeError('Candidate Hermes package changed: ' + name)

    DEPLOY.mkdir(mode=0o700)
    (DEPLOY / 'private').mkdir(mode=0o700)
    (DEPLOY / 'private/configurations').mkdir(mode=0o700)
    for i, (name, expected) in enumerate(originals.items()):
        target = DEPLOY / 'private/configurations' / (str(i) + '-' + Path(name).name)
        shutil.copyfile(name, target)
        if digest(target) != expected:
            raise RuntimeError('Backup copy differs.')
    info = json.loads(run(['docker', 'inspect', 'arthas-mysql']))[0]
    mounts = {x['Destination']: x['Source'] for x in info['Mounts']}
    if (info['Name'] != '/arthas-mysql' or not info['State']['Running']
            or mounts.get('/var/lib/mysql') != '/opt/arthas/mysql'):
        raise RuntimeError('Unexpected production database container.')
    environment = dict(value.split('=', 1) for value in info['Config']['Env'] if '=' in value)
    password = environment['MYSQL_ROOT_PASSWORD'].replace('\\', '\\\\').replace('"', '\\"')
    if '\n' in password or '\r' in password:
        raise RuntimeError('Invalid credential format.')
    options = config_options(old_etc / 'worldserver.conf')
    host, port, _, _, database = options['WorldDatabaseInfo'].split(';')
    if (host, port, database) != ('127.0.0.1', '3306', 'arthas_world'):
        raise RuntimeError('Unexpected live database endpoint.')
    cnf = DEPLOY / 'private/mysql-client.cnf'
    cnf.write_text('[client]\nhost=127.0.0.1\nport=3306\nprotocol=TCP\nuser=root\npassword="' + password + '"\n')
    cnf.chmod(0o600)
    if mysql('SELECT @@port;') != '3306':
        raise RuntimeError('Unexpected MySQL connection.')
    migrations = read(ROOT / 'evidence/sql-audit.json')['files']
    if len(migrations) != 28:
        raise RuntimeError('Expected the 28 reviewed World migrations.')
    for item in migrations:
        path = ROOT / 'core' / item['path']
        if path.parent != ROOT / 'core/data/sql/updates/db_world' or digest(path) != item['sha256']:
            raise RuntimeError('Migration differs from the reviewed set.')
        existing = mysql("SELECT hash FROM arthas_world.updates WHERE name='" + path.name + "';")
        if existing:
            raise RuntimeError('A pending migration was applied since the audit: ' + path.name)
    engines = mysql("SELECT TABLE_SCHEMA,TABLE_NAME,ENGINE FROM information_schema.tables WHERE TABLE_SCHEMA IN "
                    "('arthas_world','arthas_chars','arthas_playerbots','arthas_auth') AND TABLE_TYPE='BASE TABLE' AND ENGINE<>'InnoDB';")
    nontransactional = [line.split('\t') for line in engines.splitlines()]
    if any(row[0] == 'arthas_auth' for row in nontransactional):
        raise RuntimeError('Auth snapshot requires a different locking plan.')
    file_hashes = {}
    acore = grp.getgrnam('acore').gr_gid
    os.chmod(ROOT, 0o711)
    new_etc = ROOT / 'server/etc-production'
    for p in (ROOT / 'server', ROOT / 'server/bin', new_etc, new_etc / 'modules'):
        p.mkdir(exist_ok=True)
        os.chown(p, 0, acore)
        p.chmod(0o750)
    binary = ROOT / 'server/bin/worldserver'
    os.chown(binary, 0, acore)
    binary.chmod(0o550)
    file_hashes[str(binary)] = build['worldserverSha256']
    for name, expected in baseline['configurations'].items():
        destination = new_etc / name
        shutil.copyfile(old_etc / name, destination)
        os.chown(destination, 0, acore)
        destination.chmod(0o640)
        file_hashes[str(destination)] = expected
    hg = grp.getgrnam('hermesproxy').gr_gid
    shutil.copytree(ROOT / 'hermes/publish', HERMES)
    for p in (HERMES, *HERMES.rglob('*')):
        if p.is_symlink():
            raise RuntimeError('Unexpected package symlink.')
        os.chown(p, 0, hg)
        p.chmod(0o750 if p.is_dir() or p.name == 'HermesProxy' else 0o640)
    for name, expected in package['files'].items():
        file_hashes[str(HERMES / name)] = expected
    for user, path, flag in [('acore', binary, '-x'), ('acore', new_etc / 'worldserver.conf', '-r'),
                             ('acore', Path(options['DataDir']), '-x'),
                             ('hermesproxy', HERMES / 'HermesProxy', '-x'),
                             ('hermesproxy', Path('/opt/hermesproxy-wotlk/appsettings.atlas.json'), '-r')]:
        run(['runuser', '-u', user, '--', 'test', flag, str(path)])
    drops = {WORLD: '[Service]\nExecStart=\nExecStart=' + str(binary) + ' --config ' + str(ROOT / 'server/etc/worldserver.conf') + '\n',
             PROXY: '[Service]\nWorkingDirectory=' + str(HERMES) + '\nExecStart=\nExecStart=' + str(HERMES / 'HermesProxy') + ' --config /opt/hermesproxy-wotlk/appsettings.atlas.json\n'}
    for unit, text in drops.items():
        path = DEPLOY / (unit + '.override.conf')
        path.write_text(text)
        file_hashes[str(path)] = digest(path)
    prepared = {'preparedAt': now(), 'services': states, 'originalFileHashes': originals,
                'fileHashes': file_hashes, 'configurationSource': str(old_etc),
                'configurationCount': len(baseline['configurations']), 'migrations': migrations,
                'databaseContainerId': info['Id'], 'nontransactionalTables': nontransactional,
                'automaticDatabaseRestore': False, 'authorization': 'User go-ahead after migration and cutover explanation.'}
    save('prepared.json', prepared)
    verify_files()
    print(json.dumps({'prepared': True, 'configurationCount': len(baseline['configurations']),
                      'hermesFiles': len(package['files']), 'migrations': len(migrations), 'productionStopped': False}), flush=True)


def stop_backup():
    before = verify_files()
    if (DEPLOY / 'backup-result.json').exists():
        raise RuntimeError('Backup already completed; inspect before retrying.')
    for unit in (PROXY, WORLD):
        if show(unit)['MainPID'] != before['services'][unit]['MainPID']:
            raise RuntimeError('Runtime changed before the stop.')
    save('stop-started.json', {'at': now()})
    print('Stopping Hermes, then World using their existing graceful timeouts.', flush=True)
    run(['systemctl', 'stop', PROXY], timeout=110)
    run(['systemctl', 'stop', WORLD], timeout=330)
    assert_stopped()
    verify_preserved()
    save('stopped.json', {'at': now(), 'preservedServices': {u: show(u)['MainPID'] for u in PRESERVED}})
    print('World and Hermes stopped. Starting protected database backups.', flush=True)
    baseline = mysql('CHECKSUM TABLE arthas_world.acore_string,arthas_world.item_template,arthas_chars.guild,arthas_chars.guild_member;')
    save('custom-data-before.json', {'checksumRows': baseline})
    backups = []
    for db in DATABASES:
        partial = DEPLOY / 'private' / (db + '.sql.gz.partial')
        destination = DEPLOY / 'private' / (db + '.sql.gz')
        if partial.exists() or destination.exists():
            raise RuntimeError('Backup path already exists: ' + db)
        command = ['mysqldump', '--defaults-extra-file=' + str(DEPLOY / 'private/mysql-client.cnf'),
                   '--single-transaction' if db == 'arthas_auth' else '--lock-tables',
                   '--quick', '--no-tablespaces', '--set-gtid-purged=OFF', '--hex-blob',
                   '--routines', '--events', '--triggers', '--databases', db]
        with (DEPLOY / 'private' / (db + '.stderr')).open('xb') as error, partial.open('xb') as destination_file:
            with gzip.GzipFile(filename='', fileobj=destination_file, mode='wb', compresslevel=1) as output:
                process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=error)
                deadline = threading.Timer(600, process.kill)
                deadline.daemon = True
                deadline.start()
                try:
                    for block in iter(lambda: process.stdout.read(1024 * 1024), b''):
                        output.write(block)
                        if shutil.disk_usage(ROOT).free < 8 * 1024**3:
                            raise RuntimeError('Backup disk reserve reached.')
                    if process.wait() != 0:
                        raise RuntimeError('Backup failed: ' + db)
                finally:
                    deadline.cancel()
                    if process.poll() is None:
                        process.kill()
                    process.wait(timeout=15)
                    process.stdout.close()
            destination_file.flush()
            os.fsync(destination_file.fileno())
        size, tail = 0, b''
        with gzip.open(partial, 'rb') as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b''):
                size += len(block)
                tail = (tail + block)[-4096:]
        if size < 100_000 or b'-- Dump completed on ' not in tail:
            raise RuntimeError('Backup footer/size validation failed: ' + db)
        partial.rename(destination)
        row = {'database': db, 'path': str(destination), 'sha256': digest(destination),
               'compressedBytes': destination.stat().st_size, 'uncompressedBytes': size,
               'gzipIntegrityVerified': True, 'completionFooterVerified': True,
               'locking': 'single-transaction' if db == 'arthas_auth' else 'database-table-locks'}
        backups.append(row)
        save('backup-progress.json', {'backups': backups})
        print('BACKUP', db, destination.stat().st_size, 'bytes; verified.', flush=True)
    save('backup-result.json', {'completedAt': now(), 'backups': backups, 'passed': True,
                               'restoreRehearsed': False, 'authAndApiKeptRunning': True,
                               'crossDatabaseAtomicSnapshot': False})
    verify_preserved()


def migrate():
    prepared = verify_files()
    assert_stopped()
    backups = read(DEPLOY / 'backup-result.json')
    if not backups['passed'] or len(backups['backups']) != 4:
        raise RuntimeError('All four verified backups are required.')
    for backup in backups['backups']:
        if digest(backup['path']) != backup['sha256']:
            raise RuntimeError('Backup file changed.')
    if (DEPLOY / 'migration-result.json').exists():
        raise RuntimeError('Migration execution already recorded; inspect before retrying.')
    result = {'startedAt': now(), 'database': 'arthas_world', 'applied': [], 'passed': False}
    save('migration-result.json', result)
    for item in prepared['migrations']:
        path = ROOT / 'core' / item['path']
        if digest(path) != item['sha256']:
            raise RuntimeError('Migration bytes changed.')
        if mysql("SELECT hash FROM arthas_world.updates WHERE name='" + path.name + "';"):
            raise RuntimeError('Migration unexpectedly recorded: ' + path.name)
        started = time.monotonic()
        mysql(raw=path.read_bytes(), database='arthas_world')
        elapsed = int((time.monotonic() - started) * 1000)
        mysql("INSERT INTO arthas_world.updates (name,hash,state,speed) VALUES ('" + path.name
              + "','" + item['sha1'].upper() + "','RELEASED'," + str(elapsed) + ');')
        result['applied'].append({'name': path.name, 'sha256': item['sha256'], 'elapsedMs': elapsed})
        save('migration-result.json', result)
        print('APPLIED', path.name, flush=True)
    checksum = mysql('CHECKSUM TABLE arthas_world.acore_string,arthas_world.item_template,arthas_chars.guild,arthas_chars.guild_member;')
    if checksum != read(DEPLOY / 'custom-data-before.json')['checksumRows']:
        raise RuntimeError('Preserved custom/guild table checksum differs after migrations.')
    result.update(passed=True, completedAt=now(), customAndGuildTablesUnchanged=True)
    save('migration-result.json', result)
    verify_preserved()


def activate_world():
    prepared = verify_files()
    assert_stopped()
    migration = read(DEPLOY / 'migration-result.json')
    if not migration['passed'] or len(migration['applied']) != 28:
        raise RuntimeError('Verified migrations are required.')
    link = ROOT / 'server/etc'
    if not link.is_symlink() or link.resolve(strict=True) != Path('/opt/atlas-shop-tests/rename-20260911/etc'):
        raise RuntimeError('Unexpected inactive configuration link.')
    temporary = ROOT / 'server/etc.activation-link'
    if temporary.exists() or temporary.is_symlink():
        raise RuntimeError('Unexpected temporary configuration link.')
    temporary.symlink_to(ROOT / 'server/etc-production', target_is_directory=True)
    temporary.replace(link)
    for unit in (WORLD, PROXY):
        path = Path('/etc/systemd/system') / (unit + '.service.d') / DROP_NAME
        with path.open('x') as stream:
            stream.write((DEPLOY / (unit + '.override.conf')).read_text())
        path.chmod(0o644)
    run(['systemctl', 'daemon-reload'], timeout=60)
    for unit, binary in ((WORLD, ROOT / 'server/bin/worldserver'), (PROXY, HERMES / 'HermesProxy')):
        state = show(unit)
        if str(binary) not in state['ExecStart'] or Path(state['DropInPaths'].split()[-1]).name != DROP_NAME:
            raise RuntimeError('Effective service command differs from the prepared override.')
    if show(WORLD)['WorkingDirectory'] != prepared['services'][WORLD]['WorkingDirectory']:
        raise RuntimeError('World working directory changed.')
    save('activation-started.json', {'at': now(), 'worldBinary': str(ROOT / 'server/bin/worldserver'),
                                   'hermesBinary': str(HERMES / 'HermesProxy')})
    run(['systemctl', 'start', WORLD], timeout=60)
    print('New World started; Hermes remains stopped until World is ready.', flush=True)


def ready():
    delivery = mysql("SELECT COUNT(*) FROM arthas_auth.atlas_shop_delivery_health WHERE realm_id=1 AND protocol=2 "
                     "AND character_database='arthas_chars' AND last_seen_at BETWEEN UTC_TIMESTAMP(6)-INTERVAL 30 SECOND AND UTC_TIMESTAMP(6)+INTERVAL 5 SECOND;")
    conversion = mysql("SELECT COUNT(*) FROM arthas_auth.atlas_shop_conversion_health WHERE realm_id=1 AND protocol=1 "
                       "AND copper_per_cent=10000 AND character_database='arthas_chars' AND last_seen_at BETWEEN UTC_TIMESTAMP(6)-INTERVAL 30 SECOND AND UTC_TIMESTAMP(6)+INTERVAL 5 SECOND;")
    try:
        with socket.create_connection(('127.0.0.1', 4000), timeout=2):
            listening = True
    except OSError:
        listening = False
    return delivery == conversion == '1' and listening


def activate_hermes():
    verify_files()
    world = show(WORLD)
    if world['ActiveState'] != 'active' or Path('/proc/' + world['MainPID'] + '/exe').resolve(strict=True) != ROOT / 'server/bin/worldserver':
        raise RuntimeError('New World is not running.')
    if not ready():
        raise RuntimeError('World readiness and shop heartbeats are required before opening Hermes.')
    if show(PROXY)['MainPID'] != '0':
        raise RuntimeError('Hermes must still be stopped.')
    run(['systemctl', 'start', PROXY], timeout=60)
    save('hermes-started.json', {'at': now(), 'worldPid': world['MainPID'], 'hermesPid': show(PROXY)['MainPID']})
    print('New Hermes started after verified World and shop readiness.', flush=True)


def verify():
    prepared = verify_files()
    result = {'checkedAt': now(), 'services': {}, 'ready': ready(), 'productionActivationPerformed': True}
    for unit, binary in ((WORLD, ROOT / 'server/bin/worldserver'), (PROXY, HERMES / 'HermesProxy')):
        state = show(unit)
        process = Path('/proc/' + state['MainPID'] + '/exe')
        if (state['ActiveState'] != 'active' or process.resolve(strict=True) != binary
                or digest(process) != prepared['fileHashes'][str(binary)]):
            raise RuntimeError('Unexpected active executable: ' + unit)
        result['services'][unit] = {'pid': int(state['MainPID']), 'executable': str(binary),
                                    'sha256': digest(process), 'restarts': int(state['NRestarts'])}
    with urllib.request.urlopen('http://127.0.0.1:4323/health', timeout=4) as response:
        result['apiHealthy'] = response.status == 200 and json.load(response).get('status') == 'ok'
    result['preservedServices'] = {unit: int(show(unit)['MainPID']) for unit in PRESERVED}
    result['onlineCharacters'] = int(mysql('SELECT COUNT(*) FROM arthas_chars.characters WHERE online=1;'))
    result['realm'] = mysql('SELECT id,port,flag,gamebuild FROM arthas_auth.realmlist WHERE id=1;').split('\t')
    result['configurationFilesUnchanged'] = prepared['configurationCount']
    result['passed'] = result['ready'] and result['apiHealthy'] and result['realm'] == ['1', '4000', '0', '12340']
    save('runtime-result.json', result)
    print(json.dumps(result), flush=True)
    if not result['passed']:
        raise RuntimeError('Runtime checks incomplete.')


def close_credentials():
    verify_files()
    result = read(DEPLOY / 'deployment-result.json')
    if not result['productionActivationPerformed'] or not result['operationalChecksPassed']:
        raise RuntimeError('The recorded deployment must be complete before credential cleanup.')
    path = DEPLOY / 'private/mysql-client.cnf'
    if path.parent.resolve(strict=True) != path.parent or not path.is_file() or path.is_symlink():
        raise RuntimeError('Unexpected temporary credential path.')
    path.unlink()
    save('credential-cleanup.json', {'at': now(), 'temporaryMysqlCredentialsRemoved': True})
    print('Temporary MySQL client credential file removed; private backups retained.', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=('prepare', 'stop-backup', 'migrate', 'activate-world', 'activate-hermes', 'verify', 'close-credentials'))
    args = parser.parse_args()
    os.umask(0o077)
    if os.geteuid() != 0 or ROOT.resolve(strict=True) != ROOT:
        raise RuntimeError('Expected the root-owned prepared candidate.')
    {'prepare': prepare, 'stop-backup': stop_backup, 'migrate': migrate,
     'activate-world': activate_world, 'activate-hermes': activate_hermes, 'verify': verify,
     'close-credentials': close_credentials}[args.phase]()


if __name__ == '__main__':
    main()
