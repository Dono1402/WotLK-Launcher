#!/usr/bin/env python3
"""Read-only online database snapshot and server-only private configuration backup."""
import gzip
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
from release_runtime import ROOT, digest

def main():
    os.umask(0o077)
    if os.geteuid() != 0 or ROOT.resolve(strict=True) != ROOT: raise RuntimeError('Unexpected release root')
    backup = ROOT / 'backups' / time.strftime('%Y%m%dT%H%M%SZ', time.gmtime())
    backup.mkdir(parents=True, mode=0o700)
    plan = json.loads((ROOT / 'plan.json').read_text())
    preparation = sys.argv[1:] == ['--preparation-snapshot']
    if not preparation:
        if sys.argv[1:] != ['--after-stop']: raise RuntimeError('Choose --preparation-snapshot or --after-stop')
        for service in plan['restartServices']:
            pid = subprocess.check_output(['systemctl', 'show', service, '-p', 'MainPID', '--value'], text=True).strip()
            state = subprocess.check_output(['systemctl', 'show', service, '-p', 'ActiveState', '--value'], text=True).strip()
            if pid != '0' or state not in ('inactive', 'failed'): raise RuntimeError('A gameplay/API writer is still running')
    container = json.loads(subprocess.check_output(['docker', 'inspect', 'arthas-mysql'], text=True))[0]
    if container['Name'] != '/arthas-mysql' or not container['State']['Running']: raise RuntimeError('Wrong database container')
    environment = dict(x.split('=', 1) for x in container['Config']['Env'] if '=' in x)
    password = environment['MYSQL_ROOT_PASSWORD'].replace('\\', '\\\\').replace('"', '\\"')
    if '\n' in password or '\r' in password: raise RuntimeError('Invalid database credential format')
    defaults = '[client]\nuser=root\npassword="' + password + '"\n'
    prefix = ['docker', 'exec', '-i', container['Id']]
    engines = subprocess.check_output([*prefix, 'mysql', '--defaults-extra-file=/dev/stdin', '-NBe',
        "SELECT TABLE_SCHEMA,TABLE_NAME,ENGINE FROM information_schema.tables WHERE TABLE_SCHEMA IN "
        "('arthas_auth','arthas_chars','arthas_playerbots') AND TABLE_TYPE='BASE TABLE' AND ENGINE<>'InnoDB';"], input=defaults, text=True)
    nontransactional = [line.split('\t') for line in engines.strip().splitlines()]
    if not preparation and any(row[0] == 'arthas_auth' for row in nontransactional):
        raise RuntimeError('An auth table requires stopping its remaining writer')
    dump = backup / 'arthas-auth-chars-playerbots.sql.gz'
    command = [*prefix, 'mysqldump', '--defaults-extra-file=/dev/stdin', '--single-transaction',
        '--quick', '--skip-lock-tables', '--no-tablespaces', '--set-gtid-purged=OFF', '--hex-blob',
        '--routines', '--events', '--triggers', '--databases', 'arthas_auth', 'arthas_chars', 'arthas_playerbots']
    with (backup / 'mysqldump.stderr').open('wb') as errors:
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
        try:
            process.stdin.write(defaults.encode()); process.stdin.close()
            with gzip.open(dump, 'wb', compresslevel=1) as compressed:
                shutil.copyfileobj(process.stdout, compressed, 1024 * 1024)
            if process.wait(timeout=30) != 0: raise RuntimeError('Snapshot failed; inspect protected stderr')
        finally:
            if process.poll() is None: process.kill(); process.wait()
            process.stdout.close()
    with gzip.open(dump, 'rb') as stream:
        size = 0
        for block in iter(lambda: stream.read(1024 * 1024), b''): size += len(block)
    if size < 100_000: raise RuntimeError('Unexpectedly small database snapshot')
    configs = set()
    for service in plan['activeBefore']['services'].values(): configs.update(service['UnitFileSha256'])
    configs.update(('/opt/wotlk-launcher-api/appsettings.json', '/opt/hermesproxy-wotlk/appsettings.atlas.json',
        '/etc/hermesproxy-wotlk.env', '/etc/wotlk/launcher-api.env', '/etc/wotlk/launcher-api-social.env',
        '/etc/wotlk/launcher-api-chat.env', '/etc/wotlk/launcher-api-presence.env',
        plan['activeBefore']['world']['configPath']))
    configs.update(plan['activeBefore']['api']['configurationFiles'])
    configs.update(str(x) for x in (Path(plan['activeBefore']['world']['configPath']).parent.resolve(strict=True) / 'modules').glob('*') if x.is_file())
    hashes = {}
    for value in sorted(configs):
        source = Path(value)
        target = backup / 'configuration' / source.relative_to('/')
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target); target.chmod(0o600)
        hashes[value] = digest(source)
        if digest(target) != hashes[value]: raise RuntimeError('Configuration backup hash differs')
    report = {'createdAtUnix': int(time.time()), 'dump': str(dump), 'sha256': digest(dump),
        'compressedBytes': dump.stat().st_size, 'uncompressedBytes': size, 'gzipIntegrityVerified': True,
        'allTablesInnoDb': not nontransactional, 'nontransactionalTables': nontransactional,
        'singleTransaction': True, 'onlineSnapshot': preparation,
        'configurationHashes': hashes, 'restoreRehearsed': False,
        'warning': 'World and API stopped; preserved Auth/Hermes auth writes use InnoDB. Nontransactional character/playerbot writers stopped. Never restore over later purchases or gameplay.'}
    (backup / 'backup-proof.json').write_text(json.dumps(report, indent=2) + '\n')
    (ROOT / 'latest-backup.json').write_text(json.dumps({'proof': str(backup / 'backup-proof.json')}) + '\n')
    print(json.dumps({key: report[key] for key in ('dump', 'sha256', 'compressedBytes', 'uncompressedBytes', 'gzipIntegrityVerified', 'singleTransaction', 'restoreRehearsed')}))

if __name__ == '__main__': main()
