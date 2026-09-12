"""Verify tested candidates and the exact live baseline before the approved stop."""
import json
from pathlib import Path
import shutil
import subprocess
from release_runtime import ROOT, API, WORLD, digest, query, service


def verify():
    plan = json.loads((ROOT / 'plan.json').read_text())
    proof = json.loads((ROOT / 'final-candidate-test.json').read_text())
    if not proof['passed'] or proof['worldSha256'] != plan['worldSha256'] or digest(WORLD / 'build/worldserver') != plan['worldSha256']:
        raise RuntimeError('Tested World identity differs')
    for relative, sha in plan['files'].items():
        if digest(API / relative) != sha or proof['apiFiles'].get(relative) != sha:
            raise RuntimeError('Tested API identity differs: ' + relative)
    for path, sha in plan['configurationFiles'].items():
        if digest(path) != sha: raise RuntimeError('Candidate configuration changed')
    for name, expected in plan['activeBefore']['services'].items():
        current = service(name)
        if current['ActiveState'] != 'active' or current['MainPID'] != expected['MainPID'] or digest('/proc/' + current['MainPID'] + '/exe') != expected['ExecutableSha256']:
            raise RuntimeError('Active service changed: ' + name)
        if subprocess.check_output(['systemctl', 'show', name, '-p', 'DropInPaths', '--value'], text=True).strip() != expected['DropInPaths']:
            raise RuntimeError('Drop-in inventory changed')
        for path, sha in expected['UnitFileSha256'].items():
            if digest(path) != sha: raise RuntimeError('Active unit changed')
    before = plan['activeBefore']
    source = Path(before['world']['configPath'])
    if digest(source) != before['world']['configSha256']: raise RuntimeError('Active World config changed')
    for name, sha in before['world']['moduleConfigHashes'].items():
        if digest(source.parent.resolve(strict=True) / 'modules' / name) != sha: raise RuntimeError('Active module configuration changed')
    for path, sha in before['api']['configurationFiles'].items():
        if digest(path) != sha: raise RuntimeError('Active API config changed')
    if digest('/opt/hermesproxy-wotlk/appsettings.atlas.json') != before['hermes']['configSha256']: raise RuntimeError('Hermes config changed')
    if query('SELECT version,name,HEX(sha256),application_version FROM arthas_auth.atlas_launcher_schema_history ORDER BY version;') != before['database']['migrationHistory']:
        raise RuntimeError('Schema baseline changed')
    if str((WORLD / 'server/etc').resolve(strict=True)) != plan['worldConfigLinkBefore']: raise RuntimeError('Candidate link changed')
    for path, expected in plan['destinations'].items():
        if digest(path) != expected['sha256'] or Path(expected['destination']).exists(): raise RuntimeError('Override changed or installed')
    for path, sha in plan['environmentHashes'].items():
        if digest(path) != sha: raise RuntimeError('Prepared flags changed')
    for user, executable, config in [('acore', WORLD / 'server/bin/worldserver', WORLD / 'server/etc-production/worldserver.conf'), ('wotlklauncher', API / 'WotLK.Launcher.Server', API / 'appsettings.json')]:
        subprocess.run(['runuser', '-u', user, '--', 'test', '-x', str(executable)], check=True)
        subprocess.run(['runuser', '-u', user, '--', 'test', '-r', str(config)], check=True)
    client = json.loads((ROOT / 'launcher/preparation.json').read_text())
    for path, item in client['baseline'].items():
        if digest(path) != item['sha256']: raise RuntimeError('Public baseline changed')
    for path, sha in client['files'].items():
        if digest(ROOT / 'launcher/prepared' / path) != sha: raise RuntimeError('Prepared client changed')
    if shutil.disk_usage(ROOT).free < 8_000_000_000: raise RuntimeError('Insufficient backup space')
    print('PASS: tested candidates, current services, configuration, schema and signed client verified.', flush=True)


if __name__ == '__main__': verify()
