#!/usr/bin/env python3
"""Read-only preflight: candidates, tested identities, active baseline and public files."""
import json
from pathlib import Path
import subprocess
from prepare_backend import ROOT, API, HERMES, WORLD, digest

def main():
    plan = json.loads((ROOT / 'plan.json').read_text())
    proof = json.loads((ROOT / 'final-candidate-test.json').read_text())
    if not proof['passed']: raise RuntimeError('Final private scenario has not passed')
    if digest(WORLD / 'build/worldserver') != plan['worldSha256'] or proof['worldSha256'] != plan['worldSha256']:
        raise RuntimeError('World candidate changed')
    for relative, sha in plan['files'].items():
        prefix, name = relative.split('/', 1)
        if digest((API if prefix == 'api' else HERMES) / name) != sha: raise RuntimeError('Backend candidate changed')
    if proof['apiSha256'] != plan['files']['api/WotLK.Launcher.Server'] or proof['hermesSha256'] != plan['files']['hermes/HermesProxy']:
        raise RuntimeError('Tested backend identity differs')
    for relative, sha in plan['worldProductionConfig'].items():
        if digest(WORLD / 'server/etc-production' / relative) != sha: raise RuntimeError('Production configuration candidate changed')
    if digest(API / 'appsettings.json') != plan['apiConfigSha256']: raise RuntimeError('API candidate configuration changed')
    for user, executable, configuration in (
        ('wotlklauncher', API / 'WotLK.Launcher.Server', API / 'appsettings.json'),
        ('hermesproxy', HERMES / 'HermesProxy', Path('/opt/hermesproxy-wotlk/appsettings.atlas.json')),
        ('acore', WORLD / 'server/bin/worldserver', WORLD / 'server/etc-production/worldserver.conf')):
        subprocess.run(['runuser', '-u', user, '--', 'test', '-x', str(executable)], check=True)
        subprocess.run(['runuser', '-u', user, '--', 'test', '-r', str(configuration)], check=True)
    for prefix, user in (('api', 'wotlklauncher'), ('hermes', 'hermesproxy')):
        package = API if prefix == 'api' else HERMES
        for relative in plan['files']:
            if relative.startswith(prefix + '/'):
                subprocess.run(['runuser', '-u', user, '--', 'test', '-r', str(package / relative.split('/', 1)[1])], check=True)
    for relative in plan['worldProductionConfig']:
        subprocess.run(['runuser', '-u', 'acore', '--', 'test', '-r', str(WORLD / 'server/etc-production' / relative)], check=True)
    for name, expected in plan['activeBefore']['services'].items():
        pid = subprocess.check_output(['systemctl', 'show', name, '-p', 'MainPID', '--value'], text=True).strip()
        if pid != expected['MainPID'] or str((Path('/proc') / pid / 'exe').resolve()) != expected['Executable']:
            raise RuntimeError('Active process changed; refresh the baseline')
        if digest(expected['Executable']) != expected['ExecutableSha256']: raise RuntimeError('Active binary changed')
        paths = subprocess.check_output(['systemctl', 'show', name, '-p', 'DropInPaths', '--value'], text=True).strip()
        if paths != expected['DropInPaths']: raise RuntimeError('Active drop-in inventory changed')
        for path, sha in expected['UnitFileSha256'].items():
            if digest(path) != sha: raise RuntimeError('Service definition changed')
    if digest(plan['activeBefore']['world']['configPath']) != plan['activeBefore']['world']['configSha256']:
        raise RuntimeError('Active World config changed')
    if digest('/opt/wotlk-launcher-api/appsettings.json') != plan['apiConfigSha256']: raise RuntimeError('Active API config changed')
    if digest('/opt/hermesproxy-wotlk/appsettings.atlas.json') != plan['activeBefore']['hermes']['configSha256']:
        raise RuntimeError('Active Hermes config changed')
    client = json.loads((ROOT / 'launcher/proof.json').read_text())
    for path, sha in client['publicBefore'].items():
        if digest(path) != sha: raise RuntimeError('Public baseline changed')
    for relative, sha in client['preparedFiles'].items():
        if digest(ROOT / 'launcher/prepared' / relative) != sha: raise RuntimeError('Prepared public file changed')
    operations = json.loads((ROOT / 'operations/operations.json').read_text())
    for path, sha in operations['environmentHashes'].items():
        if digest(path) != sha: raise RuntimeError('Prepared API activation flags changed')
    for path, expected in operations['destinations'].items():
        if digest(path) != expected['sha256'] or Path(expected['destination']).exists(): raise RuntimeError('Override changed or already installed')
    if str((WORLD / 'server/etc').resolve(strict=True)) != operations['worldConfigLinkBefore']:
        raise RuntimeError('Candidate configuration link changed')
    backup = json.loads((ROOT / 'latest-backup.json').read_text())
    backup_proof = json.loads(Path(backup['proof']).read_text())
    if digest(backup_proof['dump']) != backup_proof['sha256']: raise RuntimeError('Backup changed')
    for path, sha in backup_proof['configurationHashes'].items():
        if digest(path) != sha: raise RuntimeError('A private configuration changed after backup')
    print('PASS: prepared candidates, signatures, test identities, backup and all four active production processes match; no public activation.')

if __name__ == '__main__': main()
