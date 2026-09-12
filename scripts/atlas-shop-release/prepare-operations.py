#!/usr/bin/env python3
"""Render exact service overrides and rollback instructions without installing them."""
import json
import os
from pathlib import Path
import subprocess
from prepare_backend import ROOT, API, HERMES, WORLD, digest

SUFFIX = 'zzzzzz-atlas-shop-native-170.conf'

def main():
    os.umask(0o077)
    if os.geteuid() != 0 or ROOT.resolve(strict=True) != ROOT: raise RuntimeError('Unexpected release root')
    plan = json.loads((ROOT / 'plan.json').read_text())
    proof = json.loads((ROOT / 'final-candidate-test.json').read_text())
    if not proof['passed'] or proof['worldSha256'] != digest(WORLD / 'build/worldserver'):
        raise RuntimeError('Final candidate test is absent or stale')
    stage = ROOT / 'operations'
    stage.mkdir(mode=0o700)
    world_service = next(x for x in plan['activeBefore']['services'] if x.startswith('arthas-worldserver'))
    (stage / 'api.env').write_text('WOTLK_LAUNCHER_MAX_SCHEMA_VERSION=13\nAtlasShop__Purchases__AccountServicesEnabled=true\nAtlasShop__Purchases__RenameEnabled=false\nAtlasShop__Purchases__RealmId=1\n')
    (stage / 'api-enabled.env').write_text((stage / 'api.env').read_text().replace('RenameEnabled=false', 'RenameEnabled=true'))
    overrides = {
        world_service: '[Service]\nExecStart=\nExecStart=' + str(WORLD / 'server/bin/worldserver') + ' --config ' + str(WORLD / 'server/etc/worldserver.conf') + '\n',
        'hermesproxy-wotlk': '[Service]\nWorkingDirectory=' + str(HERMES) + '\nExecStart=\nExecStart=' + str(HERMES / 'HermesProxy') + ' --config /opt/hermesproxy-wotlk/appsettings.atlas.json\n',
        'wotlk-launcher-api': '[Service]\nWorkingDirectory=' + str(API) + '\nExecStart=\nExecStart=' + str(API / 'WotLK.Launcher.Server') + '\nEnvironmentFile=' + str(stage / 'api.env') + '\n',
    }
    destinations = {}
    for name, content in overrides.items():
        target = Path('/etc/systemd/system') / (name + '.service.d') / SUFFIX
        if target.exists() or target.is_symlink(): raise RuntimeError('Activation override already exists')
        current = plan['activeBefore']['services'][name]
        if any(Path(path).name >= SUFFIX for path in current['DropInPaths'].split()): raise RuntimeError('A later override needs review')
        prepared = stage / (name + '.conf')
        prepared.write_text(content)
        rendered = stage / 'rendered' / (name + '.service')
        rendered.parent.mkdir(exist_ok=True)
        existing = subprocess.check_output(['systemctl', 'cat', name], text=True)
        rendered.write_text(existing + '\n' + content)
        subprocess.run(['systemd-analyze', 'verify', str(rendered)], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        destinations[str(prepared)] = {'destination': str(target), 'sha256': digest(prepared)}
    report = {'activated': False, 'serviceOverridesVerified': True, 'destinations': destinations,
        'environmentHashes': {str(stage / name): digest(stage / name) for name in ('api.env', 'api-enabled.env')},
        'worldConfigLinkBefore': str((WORLD / 'server/etc').resolve(strict=True)),
        'worldConfigLinkAfter': str(WORLD / 'server/etc-production'),
        'worldWorkingDirectoryPreserved': plan['activeBefore']['services'][world_service]['WorkingDirectory'],
        'authServicePreserved': True, 'characterDatabaseWorkers': 4,
        'purchasesInitiallyEnabled': False,
        'rollback': 'Keep API 1.7.0 and schema 13 with RenameEnabled=false. Remove only these World/Hermes overrides after verifying their hashes. Do not restore the old schema or erase orders.'}
    (stage / 'operations.json').write_text(json.dumps(report, indent=2) + '\n')
    print('PASS: all three rendered systemd units verified; exact overrides remain private and uninstalled.')

if __name__ == '__main__': main()
