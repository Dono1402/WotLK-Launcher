#!/usr/bin/env python3
"""Summarize the deployed release without account data or credentials."""
import datetime
import json
import os
from pathlib import Path
from release_runtime import *

def main():
    if os.geteuid() != 0: raise RuntimeError('Expected root')
    publication = json.loads((ROOT / 'publication-result.json').read_text())
    if not publication['published']: raise RuntimeError('Client publication did not succeed')
    activation = json.loads((ROOT / 'activation-progress.json').read_text())
    runtime = verify_runtime(True)
    publication_check = json.loads((ROOT / 'live-check-published.json').read_text())
    if not publication_check['passed'] or not publication_check['canaryRemoved']: raise RuntimeError('Live HTTPS check failed')
    backup = json.loads(Path(activation['backupProof']).read_text())
    if backup['onlineSnapshot'] or not backup['gzipIntegrityVerified'] or digest(backup['dump']) != backup['sha256']:
        raise RuntimeError('Pre-migration backup proof differs')
    history = query('SELECT version,name,HEX(sha256),application_version FROM arthas_auth.atlas_launcher_schema_history ORDER BY version;')
    if history != activation['migrationHistory']: raise RuntimeError('Applied migration history changed')
    for phase in ('closed', 'enabled', 'published'):
        canary = json.loads((ROOT / ('canary-' + phase + '.json')).read_text())
        if not canary['cleaned']: raise RuntimeError('A canary remains')
        if query("SELECT COUNT(*) FROM arthas_auth.account WHERE username='" + canary['username'] + "';") != [['0']]:
            raise RuntimeError('Canary cleanup not confirmed')
    plan = json.loads((ROOT / 'plan.json').read_text())
    for relative, sha in plan['worldProductionConfig'].items():
        if digest(WORLD / 'server/etc-production' / relative) != sha: raise RuntimeError('World configuration changed')
    if (WORLD / 'server/etc').resolve(strict=True) != WORLD / 'server/etc-production': raise RuntimeError('World configuration target differs')
    listeners = run(['ss', '-ltnpH']).splitlines()
    listening = {}
    for name in (WORLD_SERVICE, 'hermesproxy-wotlk', 'wotlk-launcher-api'):
        pid = runtime[name]['MainPID']
        listening[name] = sorted({line.split()[3] for line in listeners if 'pid=' + pid + ',' in line})
        if not listening[name]: raise RuntimeError('No TCP listener for ' + name)
    report = {'version': '1.7.0', 'deployed': True, 'checkedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'purchasesEnabled': True, 'accountServicesEnabled': True, 'schemaVersion': 13,
        'characterDatabaseWorkers': 4, 'authProcessPreserved': True, 'runtime': runtime, 'listeners': listening,
        'heartbeat': query("SELECT realm_id,protocol,character_database,TIMESTAMPDIFF(SECOND,last_seen_at,UTC_TIMESTAMP(6)) "
            "FROM arthas_auth.atlas_shop_delivery_health WHERE realm_id=1 AND protocol=2;"),
        'publication': publication, 'preMigrationBackup': {key: backup[key] for key in ('dump','sha256','compressedBytes','gzipIntegrityVerified','onlineSnapshot','restoreRehearsed')},
        'authenticatedShopAndNotesChecked': True, 'syntheticAccountsRemoved': True, 'realOrdersCreatedByVerification': 0,
        'gameGraphicalObservation': False}
    (ROOT / 'deployment-result.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'deployed': True, 'version': '1.7.0', 'purchasesEnabled': True, 'schemaVersion': 13,
        'listeners': listening, 'checkedAtUtc': report['checkedAtUtc']}), flush=True)

if __name__ == '__main__': main()
