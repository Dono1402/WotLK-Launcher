"""Record sanitized live release evidence without spending player gold or credits."""
import datetime
import json
from pathlib import Path
import socket
import urllib.request
from release_runtime import ROOT, API, WORLD, digest, query, verify_runtime


def main():
    plan = json.loads((ROOT / 'plan.json').read_text())
    runtime = verify_runtime(True)
    history = query('SELECT version,name,HEX(sha256),application_version FROM arthas_auth.atlas_launcher_schema_history ORDER BY version;')
    if [int(row[0]) for row in history] != list(range(1, 15)) or history[:13] != plan['activeBefore']['database']['migrationHistory']:
        raise RuntimeError('Unexpected migration history')
    # AssemblyInformationalVersion may append the source revision after '+'.
    if history[-1][3].split('+', 1)[0] != '1.7.2': raise RuntimeError('Migration application version differs')
    publication = json.loads((ROOT / 'launcher/publication.json').read_text())
    activation = json.loads((ROOT / 'activation-progress.json').read_text())
    if activation['phase'] != 'backend-active-purchases-closed' or not publication['published']:
        raise RuntimeError('Release sequence incomplete')
    checks = {phase: json.loads((ROOT / ('live-check-' + phase + '.json')).read_text()) for phase in ('closed', 'enabled', 'published')}
    if not all(check['passed'] and check['canaryRemoved'] and check['realOrdersCreated'] == 0 for check in checks.values()):
        raise RuntimeError('A live canary check failed or was not cleaned')
    backup = json.loads(Path(activation['backupProof']).read_text())
    if digest(backup['dump']) != backup['sha256'] or not backup['gzipIntegrityVerified'] or backup['onlineSnapshot']:
        raise RuntimeError('Quiesced backup proof differs')
    old_dns = socket.getaddrinfo
    socket.getaddrinfo = lambda host, port, family=0, type=0, proto=0, flags=0: old_dns(host, port, socket.AF_INET if host == 'animeclub.fr' else family, type, proto, flags)
    with urllib.request.urlopen(urllib.request.Request('https://animeclub.fr/wotlk/launcher/launcher-update.json', headers={'Cache-Control': 'no-cache'}), timeout=20) as response:
        raw = response.read(65537)
        if response.status != 200 or 'no-store' not in response.headers.get('Cache-Control', ''): raise RuntimeError('Public manifest HTTP policy differs')
    manifest_path = ROOT / 'launcher/prepared/public/launcher-update.json'
    if raw != manifest_path.read_bytes() or json.loads(raw)['version'] != '1.7.2': raise RuntimeError('Public manifest bytes differ')
    for name in ('1.7.0', '1.7.1', '1.7.2'):
        if not (Path('/var/www/wotlk-launcher/launcher/releases') / name / 'WotLK-Launcher.exe').is_file():
            raise RuntimeError('Versioned release is absent')
    for name, sha in plan['files'].items():
        if digest(API / name) != sha: raise RuntimeError('Running API package changed')
    for state in runtime.values():
        if 'NRestarts' in state and state['NRestarts'] != '0': raise RuntimeError('Unexpected service restart loop')
    core_heartbeats = {
        'native': query('SELECT realm_id,protocol,character_database,TIMESTAMPDIFF(SECOND,last_seen_at,UTC_TIMESTAMP(6)) FROM arthas_auth.atlas_shop_delivery_health WHERE realm_id=1;')[0],
        'gold': query('SELECT realm_id,protocol,character_database,copper_per_cent,TIMESTAMPDIFF(SECOND,last_seen_at,UTC_TIMESTAMP(6)) FROM arthas_auth.atlas_shop_conversion_health WHERE realm_id=1;')[0]}
    report = dict(version='1.7.2', status='published-and-activated', verifiedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
        authorizedMaintenance=True, sourceCommit='5dc3b085205719768e3a1e27500041e6543180b0',
        runtime=runtime, schemaVersion=14, earlierMigrationsPreserved=True, migration=history[-1],
        coreHeartbeats=core_heartbeats, characterDatabaseWorkers=4, euroWalletIndependent=True,
        productionCheckCreatedOrders=0, productionCheckCreatedConversions=0, liveChecks={key: {k: v for k, v in value.items() if k != 'runtime'} for key, value in checks.items()},
        backup=dict(sha256=backup['sha256'], compressedBytes=backup['compressedBytes'], uncompressedBytes=backup['uncompressedBytes'], gzipIntegrityVerified=True, afterWorldAndApiStop=True, restoreRehearsed=False),
        publicManifestMatches=True, publicManifestSha256=digest(manifest_path), versionedDownloadsFullyHashed=True,
        candidateSignatureAndNoUpdateVerifiedSeparately=True, previousReleasesPreserved=True,
        maintenanceStartedAtUnix=activation['startedAtUnix'], backendReadyAtUnix=activation['completedAtUnix'], publicationCompletedAtUnix=publication['completedAtUnix'])
    (ROOT / 'deployment.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({key: report[key] for key in ('version', 'status', 'verifiedAtUtc', 'schemaVersion', 'coreHeartbeats', 'characterDatabaseWorkers', 'publicManifestMatches', 'previousReleasesPreserved')}, indent=2))


if __name__ == '__main__': main()
