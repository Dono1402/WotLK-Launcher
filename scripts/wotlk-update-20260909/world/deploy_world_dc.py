#!/usr/bin/env python3
"""Reviewed phase-by-phase DC cutover; private backups stay on Atlas. No DB restore."""
import argparse
import datetime
import grp
import gzip
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading

ROOT = Path('/opt/arthas-next/candidates/modules-update-20260909T1035Z')
OLD = Path('/opt/arthas-next/candidates/atlas-chat-whispers-20260907/server')
MODULES = Path('/opt/arthas-next/candidates/modules-update-20260905T1016Z/server/etc/modules')
WD = '/opt/arthas-next/candidates/modules-update-20260905T1016Z/server/bin'
UNIT = 'arthas-worldserver.dungeon-clear-8224099.service'
DROP = Path('/etc/systemd/system') / (UNIT + '.d/70-atlas-dungeon-clear-20260909.conf')
DEPLOY = ROOT / 'deployment'
RUNTIME = ROOT / 'server'
BINARY = ROOT / 'build-isolated/worldserver-candidate'
OLD_SHA = 'de9f14523f7b933b714904f05cb0d4bb6d65f6592c9cd4d2aaeb0de125bfe315'
NEW_SHA = '428e92b4498312593ea44f6c0d2870519a43af65acf68be3567890d178cd87d6'
MIN_FREE = 8 * 1024**3
MAX_DEPLOY_BYTES = 3 * 1024**3
ACORE_GID = None

def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()

def run(args, **kw):
    return subprocess.run(args, check=True, text=True, capture_output=True, **kw)

def digest(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(4 * 1024**2), b''):
            h.update(b)
    return h.hexdigest()

def save(path, data):
    with open(path, 'x', encoding='utf-8') as f:
        json.dump(data, f, indent=2)
        f.write('\n')
        f.flush()
        os.fsync(f.fileno())

def show(unit=UNIT):
    keys = 'Id,ActiveState,SubState,MainPID,NRestarts,ExecStart,WorkingDirectory,Environment,DropInPaths,ControlGroup'
    result = run(['systemctl', 'show', unit, '-p', keys]).stdout
    return dict(line.split('=', 1) for line in result.splitlines() if '=' in line)

def assert_disk(extra=0):
    free = shutil.disk_usage(ROOT).free
    assert free >= MIN_FREE + extra, ('Insufficient disk reserve', free, MIN_FREE + extra)
    return free

def assert_old_running():
    state = show()
    assert state['ActiveState'] == 'active' and int(state['MainPID']) > 0, state['ActiveState']
    assert str(OLD / 'bin/worldserver') in state['ExecStart']
    assert str(OLD / 'etc/worldserver.conf') in state['ExecStart']
    assert state['WorkingDirectory'] == WD
    assert 'AC_UPDATES_ENABLE_DATABASES=0' in state['Environment']
    assert 'AC_PLAYERBOTS_UPDATES_ENABLE_DATABASES=0' in state['Environment']
    assert not DROP.exists(), 'New drop-in unexpectedly exists'
    actual_exe = Path('/proc') / state['MainPID'] / 'exe'
    assert actual_exe.resolve() == OLD / 'bin/worldserver'
    assert digest(actual_exe) == OLD_SHA
    return state

def assert_stopped():
    state = show()
    assert int(state['MainPID']) == 0 and state['ActiveState'] in ('inactive', 'failed'), ('World is not stopped', state['MainPID'], state['ActiveState'])
    # The cgroup can already have disappeared after a clean stop. Never inspect its parent.
    groups = {Path('/sys/fs/cgroup/system.slice') / UNIT}
    if state['ControlGroup']:
        assert state['ControlGroup'].startswith('/system.slice/' + UNIT)
        groups.add(Path('/sys/fs/cgroup') / state['ControlGroup'].lstrip('/'))
    for group in groups:
        if group.exists():
            for procs in group.rglob('cgroup.procs'):
                assert not procs.read_text().strip(), ('World cgroup still populated', str(procs))
    return state

def configs():
    return [OLD / 'etc/worldserver.conf'] + sorted(p for p in MODULES.iterdir()
             if p.name.endswith(('.conf', '.conf.dist')) and p.is_file())

def verify_seal():
    seal = json.loads((DEPLOY / 'prepared.json').read_text())
    assert digest(RUNTIME / 'bin/worldserver') == NEW_SHA
    assert digest(RUNTIME / 'etc/worldserver.conf') == seal['configs'][str(OLD / 'etc/worldserver.conf')]
    for name, expected in seal['configs'].items():
        assert digest(name) == expected, ('Live config changed after preparation', name)
    for name, expected in seal['unitFiles'].items():
        assert digest(name) == expected, ('Original unit changed after preparation', name)
    assert digest(DEPLOY / DROP.name) == seal['dropInSha256']
    assert digest(OLD / 'bin/worldserver') == OLD_SHA
    return seal

def prepare():
    initial = assert_old_running()
    assert not DEPLOY.exists() and not RUNTIME.exists(), 'Preparation already exists; inspect, do not overwrite'
    assert_disk(3 * 1024**3)
    assert digest(BINARY) == NEW_SHA
    assert digest(OLD / 'bin/worldserver') == OLD_SHA
    state = json.loads((ROOT / 'build-isolated/state.json').read_text())
    assert state['phases']['baseline']['passed'] and state['phases']['compile']['passed'] and state['phases']['link']['passed']
    DEPLOY.mkdir(mode=0o700)
    (DEPLOY / 'backup').mkdir(mode=0o700)
    cfg_hashes = {}
    for p in configs():
        assert not p.is_symlink()
        target = DEPLOY / 'backup' / ('worldserver.conf' if p.parent != MODULES else 'modules/' + p.name)
        target.parent.mkdir(mode=0o700, exist_ok=True)
        shutil.copyfile(p, target)
        os.chmod(target, 0o600)
        cfg_hashes[str(p)] = digest(p)
        assert digest(target) == cfg_hashes[str(p)]
    files = [Path('/etc/systemd/system') / UNIT]
    files += [Path(p) for p in initial['DropInPaths'].split()]
    units = {}
    for i, p in enumerate(files):
        assert p.is_file() and not p.is_symlink()
        target = DEPLOY / 'backup' / ('unit-%02d-%s' % (i, p.name))
        shutil.copyfile(p, target)
        os.chmod(target, 0o600)
        units[str(p)] = digest(p)
        assert digest(target) == units[str(p)]
    # Only the candidate root gains traversal. All existing private descendants stay unchanged.
    os.chmod(ROOT, 0o711)
    for p in (RUNTIME, RUNTIME / 'bin', RUNTIME / 'etc'):
        p.mkdir(mode=0o750)
        os.chown(p, 0, ACORE_GID)
        os.chmod(p, 0o750)
    for src, dst, mode in [(BINARY, RUNTIME / 'bin/worldserver', 0o550),
                           (OLD / 'etc/worldserver.conf', RUNTIME / 'etc/worldserver.conf', 0o640)]:
        shutil.copyfile(src, dst)
        os.chown(dst, 0, ACORE_GID)
        os.chmod(dst, mode)
        assert digest(src) == digest(dst)
        assert src.stat().st_ino != dst.stat().st_ino
    drop_text = '[Service]\nExecStart=\nExecStart=' + str(RUNTIME / 'bin/worldserver') + ' --config ' + str(RUNTIME / 'etc/worldserver.conf') + '\n'
    (DEPLOY / DROP.name).write_text(drop_text)
    seal = {'preparedAt': now(), 'oldBinarySha256': OLD_SHA, 'newBinarySha256': NEW_SHA,
            'configs': cfg_hashes, 'unitFiles': units, 'initialService': initial,
            'dropInSha256': digest(DEPLOY / DROP.name), 'oldRuntimeDependenciesPreserved': [WD, str(MODULES)],
            'diskFree': assert_disk(), 'dbRestoreAutomatic': False}
    save(DEPLOY / 'prepared.json', seal)
    print(json.dumps({'prepared': True, 'configFiles': len(cfg_hashes), 'unitFiles': len(units), 'binarySha256': NEW_SHA}), flush=True)

def db_options():
    entries = {}
    for line in (OLD / 'etc/worldserver.conf').read_text().splitlines():
        if '=' in line and not line.lstrip().startswith('#'):
            key, value = line.split('=', 1)
            entries[key.strip()] = value.strip().strip('"')
    host, port, user, password, db = entries['CharacterDatabaseInfo'].split(';')
    assert host == '127.0.0.1' and port == '3306' and db == 'arthas_chars'
    assert '\n' not in password and '\r' not in password
    escaped = password.replace('\\', '\\\\').replace('"', '\\"')
    return '[client]\nhost=' + host + '\nport=' + port + '\nuser=' + user + '\npassword="' + escaped + '"\nprotocol=TCP\n'

def check_database_grants():
    # No table locks or data dump here; fail before downtime if the known grants have drifted.
    grants = run(['mysql', '--defaults-extra-file=/dev/stdin', '--batch', '--skip-column-names',
                  '-e', 'SHOW GRANTS FOR CURRENT_USER'], input=db_options()).stdout
    for schema in ('arthas_chars', 'arthas_playerbots'):
        assert 'GRANT ALL PRIVILEGES ON `' + schema + '`.* TO ' in grants, ('Required schema privileges missing', schema)
    args = ['mysqldump', '--defaults-extra-file=/dev/stdin', '--skip-lock-tables', '--no-data',
            '--no-tablespaces', '--set-gtid-purged=OFF', '--routines', '--events', '--triggers',
            '--databases', 'arthas_chars', 'arthas_playerbots']
    with open(DEPLOY / 'backup/database-schema-preflight.sql', 'x') as dest, open(DEPLOY / 'backup/database-schema-preflight.stderr', 'x') as err:
        subprocess.run(args, input=db_options(), text=True, stdout=dest, stderr=err, check=True, timeout=120)

def deployment_bytes():
    return sum(p.stat().st_size for base in (DEPLOY, RUNTIME) for p in base.rglob('*') if p.is_file())

def stop_backup(expected_hermes):
    assert expected_hermes and expected_hermes.startswith('/opt/hermesproxy-wotlk/releases/')
    hermes = show('hermesproxy-wotlk.service')
    assert hermes['ActiveState'] == 'active' and expected_hermes in hermes['ExecStart'], 'Hermes cutover not verified'
    verify_seal()
    assert_old_running()
    assert not (DEPLOY / 'database-backup.json').exists()
    assert_disk(512 * 1024**2)
    check_database_grants()
    assert deployment_bytes() < MAX_DEPLOY_BYTES
    print('Stopping only World; graceful systemd timeout remains 300 seconds.', flush=True)
    run(['systemctl', 'stop', UNIT])
    stopped = assert_stopped()
    print('World stopped. Dumping only arthas_chars and arthas_playerbots with per-database table locks.', flush=True)
    partial = DEPLOY / 'backup/characters-playerbots.sql.gz.partial'
    final = DEPLOY / 'backup/characters-playerbots.sql.gz'
    assert not partial.exists() and not final.exists()
    args = ['mysqldump', '--defaults-extra-file=/dev/stdin', '--lock-tables', '--no-tablespaces',
            '--set-gtid-purged=OFF', '--routines', '--events', '--triggers', '--hex-blob',
            '--databases', 'arthas_chars', 'arthas_playerbots']
    with open(DEPLOY / 'backup/mysqldump.stderr', 'xb') as err, open(partial, 'xb') as dest:
        os.chmod(partial, 0o600)
        with gzip.GzipFile(filename='', fileobj=dest, mode='wb', compresslevel=1) as gz:
            p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=err)
            deadline = threading.Timer(900, p.kill)
            deadline.daemon = True
            deadline.start()
            try:
                p.stdin.write(db_options().encode())
                p.stdin.close()
                for block in iter(lambda: p.stdout.read(1024**2), b''):
                    gz.write(block)
                    if shutil.disk_usage(ROOT).free < MIN_FREE or deployment_bytes() > MAX_DEPLOY_BYTES:
                        raise RuntimeError('Dump stopped: 8 GiB reserve or 3 GiB deployment budget reached')
                assert p.wait() == 0, 'mysqldump failed/timed out; private stderr retained; World stays stopped'
            finally:
                deadline.cancel()
                if p.poll() is None:
                    p.kill()
                p.wait(timeout=30)
                if not p.stdin.closed:
                    p.stdin.close()
                p.stdout.close()
        dest.flush()
        os.fsync(dest.fileno())
    # Full decompression verifies CRC and completion footer without printing database data.
    tail = b''
    raw_size = 0
    with gzip.open(partial, 'rb') as f:
        for block in iter(lambda: f.read(1024**2), b''):
            raw_size += len(block)
            tail = (tail + block)[-4096:]
    assert raw_size > 1024**2 and partial.stat().st_size > 1024**2
    assert b'-- Dump completed on ' in tail, 'Missing mysqldump completion footer'
    partial.rename(final)
    backup = {'completedAt': now(), 'databases': ['arthas_chars', 'arthas_playerbots'],
              'worldStopped': stopped['MainPID'] == '0', 'gzipCrcVerified': True,
              'completionFooterVerified': True, 'bytesCompressed': final.stat().st_size,
              'bytesUncompressed': raw_size, 'sha256': digest(final), 'diskFree': assert_disk(),
              'deploymentBytes': deployment_bytes(), 'maximumDeploymentBytes': MAX_DEPLOY_BYTES,
              'restoreRequiresSeparateApproval': True}
    save(DEPLOY / 'database-backup.json', backup)
    print(json.dumps(backup), flush=True)

def start():
    seal = verify_seal()
    assert_stopped()
    backup = json.loads((DEPLOY / 'database-backup.json').read_text())
    assert digest(DEPLOY / 'backup/characters-playerbots.sql.gz') == backup['sha256']
    assert not DROP.exists()
    assert_disk()
    shutil.copyfile(DEPLOY / DROP.name, DROP)
    os.chmod(DROP, 0o644)
    assert digest(DROP) == seal['dropInSha256']
    run(['systemctl', 'daemon-reload'])
    prospective = show()
    assert str(RUNTIME / 'bin/worldserver') in prospective['ExecStart']
    assert prospective['WorkingDirectory'] == seal['initialService']['WorkingDirectory']
    assert prospective['Environment'] == seal['initialService']['Environment']
    save(DEPLOY / 'activation-started.json', {'at': now(), 'expectedBinarySha256': NEW_SHA})
    run(['systemctl', 'start', UNIT])
    current = show()
    print(json.dumps({k: v for k, v in current.items() if k != 'Environment'}), flush=True)
    print('World start requested; runtime readiness and bot/custom-function checks are still required.', flush=True)

def rollback_binary():
    seal = verify_seal()
    if DROP.exists():
        assert digest(DROP) == seal['dropInSha256'], 'Foreign change in drop-in; stop and review'
    current = show()
    if current['MainPID'] != '0':
        run(['systemctl', 'stop', UNIT])
    assert_stopped()
    if DROP.exists():
        disabled = DEPLOY / ('disabled-' + now().replace(':', '-') + '-' + DROP.name)
        shutil.move(str(DROP), disabled)
    run(['systemctl', 'daemon-reload'])
    current = show()
    assert str(OLD / 'bin/worldserver') in current['ExecStart']
    assert current['WorkingDirectory'] == seal['initialService']['WorkingDirectory']
    assert current['Environment'] == seal['initialService']['Environment']
    run(['systemctl', 'start', UNIT])
    print('Previous binary/config started. No database restored; character progress is preserved.', flush=True)

def main():
    global ACORE_GID
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--phase', choices=['prepare', 'stop-backup', 'start', 'rollback-binary'], required=True)
    parser.add_argument('--confirm-root-reviewed', action='store_true', required=True)
    parser.add_argument('--verified-hermes-executable')
    args = parser.parse_args()
    if sys.flags.optimize or not sys.flags.isolated or not sys.dont_write_bytecode:
        raise RuntimeError('Invoke using /usr/bin/python3 -I -B without optimization')
    assert os.geteuid() == 0 and args.confirm_root_reviewed
    assert ROOT.resolve() == ROOT and ROOT.is_dir()
    ACORE_GID = grp.getgrnam('acore').gr_gid
    os.umask(0o077)
    if args.phase == 'stop-backup':
        stop_backup(args.verified_hermes_executable)
    else:
        {'prepare': prepare, 'start': start, 'rollback-binary': rollback_binary}[args.phase]()

if __name__ == '__main__':
    main()
