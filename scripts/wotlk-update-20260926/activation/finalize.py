#!/usr/bin/env python3
"""Publish redacted deployment evidence, preserving explicit known limitations."""
from datetime import datetime
import importlib.util
import json
from pathlib import Path
import re
import socket
import ssl
import urllib.request

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260926')


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    d = load('deployment', ROOT / 'inputs/deploy-20260926.py')
    if (d.DEPLOY / 'deployment-result.json').exists():
        raise RuntimeError('Preserve the previous final result.')
    d.verify()
    load('observation', ROOT / 'inputs/observe-20260926.py').observe()
    observation = d.read(sorted((d.DEPLOY / 'observations').glob('*.json'))[-1])
    runtime = d.read(d.DEPLOY / 'runtime-result.json')
    prepared = d.read(d.DEPLOY / 'prepared.json')
    backup = d.read(d.DEPLOY / 'backup-result.json')
    migrations = d.read(d.DEPLOY / 'migration-result.json')
    if (observation['onlineByType'] != [['1', '1000']] or observation['realGuildBotsOnline'] != 3
            or observation['realGuildBotsPinned'] != 3 or not runtime['passed']
            or any(s['restarts'] != 0 for s in runtime['services'].values())
            or any(e['fatal'] or e['permissionDenied'] for e in observation['errors'].values())):
        raise RuntimeError('Deployment readiness is incomplete.')
    external = {}
    for port in (8081, 1119):
        with socket.create_connection(('127.0.0.1', port), timeout=5) as tcp:
            with ssl.create_default_context().wrap_socket(tcp, server_hostname='animeclub.fr') as conn:
                external[str(port)] = {'trustedCertificate': True, 'protocol': conn.version(),
                    'expires': conn.getpeercert().get('notAfter')}
    with urllib.request.urlopen('https://animeclub.fr/wotlk/health', timeout=15) as response:
        if response.status != 200 or json.load(response).get('status') != 'ok':
            raise RuntimeError('Public API health failed.')
    external['publicApiHealth'] = True
    text = Path('/opt/arthas-next/server/logs/Server.log').read_text(errors='replace')
    pets = sorted(set(int(x) for x in re.findall(r"Duplicate entry '(\d+)-\d+' for key 'pet_spell.PRIMARY'", text)))
    if not pets:
        raise RuntimeError('Expected the known incidents to remain available in the startup log.')
    owners = d.mysql('SELECT COALESCE(a.account_type,-1),COUNT(*) FROM arthas_chars.character_pet p '
        'LEFT JOIN arthas_chars.characters c ON c.guid=p.owner '
        'LEFT JOIN arthas_playerbots.playerbots_account_type a ON a.account_id=c.account '
        'WHERE p.id IN (' + ','.join(map(str, pets)) + ') GROUP BY a.account_type')
    for name in ('atlas-update-fixture-20260926-mysql', 'atlas-update-migration-20260926-mysql'):
        if d.read_json_command(['docker', 'inspect', name])[0]['State']['Running']:
            raise RuntimeError('A test database is still running.')
    activated = d.read(d.DEPLOY / 'activation-started.json')['at']
    result = {'completedAt': d.now(), 'activatedAt': activated,
        'observationSeconds': int((datetime.fromisoformat(observation['at']) - datetime.fromisoformat(activated)).total_seconds()),
        'productionActivationPerformed': True, 'operationalChecksPassed': True,
        'validationStatus': 'operational_with_known_issues', 'allKnownIssuesResolved': False,
        'runtime': runtime, 'latestObservation': observation, 'externalChecks': external,
        'backup': backup, 'worldMigrationsApplied': len(migrations['applied']),
        'worldMigrationsAlreadyApplied': len(migrations['alreadyApplied']),
        'authMigrationsPendingOrDifferent': d.read(d.DEPLOY / 'auth-sql-audit.json')['pendingOrDifferent'],
        'characterChecksumsUnchangedDuringMigration': migrations['characterChecksumsUnchanged'],
        'originalFilesStillIdentical': len(prepared['originalHashes']),
        'productionConfigFilesCopied': len([p for p in (ROOT / 'server/etc-production').rglob('*') if p.is_file()]),
        'settingsChanged': False, 'testDatabasesStopped': True, 'guiClientTestPerformed': False,
        'knownIssues': {'navigation': {'unchangedFailingTests': 5, 'details': 'See isolated preparation report.'},
            'petPersistence': {'affectedPetIdsCount': len(pets), 'ownerAccountTypesAndCounts': owners,
                'abortedTransactions': text.count('Transaction aborted.'),
                'syntheticRaceReproduction': d.read(ROOT / 'evidence/pet-spell-race-reproduction.json'),
                'fixed': False, 'productionDataManuallyRepaired': False,
                'scope': 'Observed bot pet saves; does not prove human-player pets cannot be affected.'}}}
    d.save('deployment-result.json', result)
    d.save('build-manifest-before-activation.json', d.read(ROOT / 'build/manifest.json'))
    build = d.read(ROOT / 'build/manifest.json')
    build.update(productionActivationPerformed=True, activationCompletedAt=result['completedAt'],
        validationState='Production operational with five known navigation test failures and reproduced pet persistence race.',
        deploymentReport=str(d.DEPLOY / 'deployment-result.json'))
    (ROOT / 'build/manifest.json').write_text(json.dumps(build, indent=2) + '\n')
    print('FINAL deployment evidence written, with explicit unresolved pet/navigation limitations.', flush=True)


if __name__ == '__main__':
    main()
