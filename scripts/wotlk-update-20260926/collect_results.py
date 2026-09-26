#!/usr/bin/env python3
"""Export only aggregate validation evidence; never export private account data."""
import datetime
import argparse
import collections
import json
from pathlib import Path
import re
import subprocess
import shutil
import xml.etree.ElementTree as ET
import prepare_candidate
from prepare_databases import check_owned

ROOT=prepare_candidate.ROOT
FIXTURE=prepare_candidate.FIXTURE

def read(path):
    return json.loads(path.read_text()) if path.exists() else None

def collect(finalize=False):
    prepare_candidate.verify()
    cleanup={'units':{},'databases':{},'candidateProcesses':[]}
    for name in ['atlas-module-validation-20260926','atlas-basic-validation-20260926',
            'atlas-dungeon-retry-20260926','atlas-protocol-clock-rebuild-20260926'] + [
            'atlas-all-update-realm-'+phase+'-20260926'
            for phase in ['gold','identity','services','native','modules','guild']]:
        state=subprocess.check_output(['systemctl','show',name,'-p','ActiveState','--value'],text=True).strip()
        if state not in ['inactive','failed']:
            raise RuntimeError('Wait for owned test cleanup: '+name+' '+state)
        cleanup['units'][name]=state
    for label in ['production-copy','fixture']:
        _,info=check_owned(label)
        if info['State']['Running']:
            raise RuntimeError('Owned test database is still running: '+label)
        cleanup['databases'][label]='stopped'
    for process in Path('/proc').glob('[0-9]*'):
        try:
            executable=(process/'exe').resolve(strict=True)
        except (FileNotFoundError,PermissionError,ProcessLookupError):
            continue
        if executable.is_relative_to(ROOT) or executable.is_relative_to(FIXTURE):
            cleanup['candidateProcesses'].append(int(process.name))
    if cleanup['candidateProcesses']:
        raise RuntimeError('Candidate executable still running: '+repr(cleanup['candidateProcesses']))
    manifest=read(ROOT/'build/manifest.json') or {}
    baseline=read(ROOT/'private/baseline.json') or {}
    result={'collectedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'productionActivated':False,'productionFilesUnchanged':len(baseline),
        'graphicalClientTested':False,'fullThousandBotLoadTested':False,
        'productionServicesStopped':True,'productionMySqlStopped':True,
        'privateTestCleanup':cleanup,
        'build':{k:manifest.get(k) for k in ['worldserverSha256','authserverSha256','components','configurationMode']},
        'migrations':{},'cpp':{},'hermesTests':{},'realm':{},'protocol':{}}
    for label,path in [('production-copy',ROOT/'private/migration-copy'),('synthetic-copy',FIXTURE)]:
        data=read(path/'migration-result.json') or {}
        copy=read(path/'copy-manifest.json') or {}
        result['migrations'][label]={'passed':data.get('passed',False),
            'applied':len(data.get('pending',[])),'alreadyApplied':len(data.get('alreadyApplied',[])),
            'coldCopyChecksumVerified':copy.get('coldCopyChecksumVerified',False)}
    for suite in ['core','dungeon-clear']:
        data=read(ROOT/'evidence/cpp-tests'/(suite+'.json')) or {}
        result['cpp'][suite]={k:data.get(k) for k in ['exit','totalTests','passedTests','failedTests']}
        result['cpp'][suite]['skippedTests']=len(data.get('skippedTests',[]))
    result['additionalNavigationCheck']=read(ROOT/'evidence/nav-slices/summary.json')
    previous=read(prepare_candidate.OLD/'evidence/cpp-tests/dungeon-clear.json')
    current=result['cpp'].get('dungeon-clear',{})
    result['navigationFailuresMatchPrevious']=None if previous is None or current.get('failedTests') is None else (
        sorted(previous['failedTests'])==sorted(current['failedTests']))
    for label in ['default','wotlk-343']:
        reports=list((ROOT/'evidence/hermes-tests'/label).glob('*.trx'))
        if len(reports)==1:
            trx=ET.parse(reports[0]).getroot()
            counters=trx.find('.//{*}Counters')
            result['hermesTests'][label]={k:int(counters.attrib.get(k,0)) for k in ['total','executed','passed','failed','error','timeout']}
            # xUnit's TRX summary reports notExecuted=0 even for skipped
            # results. Count their individual outcomes, not that summary field.
            result['hermesTests'][label]['skippedTests']=sum(
                row.attrib.get('outcome')=='NotExecuted' for row in trx.findall('.//{*}UnitTestResult'))
    package=read(ROOT/'hermes/package-manifest.json') or {}
    result['hermesPackage']={'source':package.get('candidateSourceCommit'),'format':package.get('format'),
        'executableSha256':package.get('files',{}).get('HermesProxy'),'files':len(package.get('files',{}))}
    result['apiRuntimeEquivalence']=read(ROOT/'evidence/api-runtime-equivalence.json')
    result['finalPatchEquivalence']=read(ROOT/'evidence/final-patch-equivalence.json')
    result['syntheticAuthReferences']=read(ROOT/'evidence/synthetic-auth-reference.json')
    result['productionCopyAuthReferences']=read(ROOT/'evidence/production-copy-auth-reference.json')
    result['protocolClockRegression']=read(ROOT/'evidence/protocol-clock-regression.json')
    for label in ['gold','identity','services','native','modules-protocol','modules','guild']:
        result['realm'][label]=read(ROOT/'evidence'/('realm-'+label)/'summary.json')
    result['functionalChecks']={}
    for label,name in [('gold','gold-conversion-result.json'),('identity','hermes-identity-result.json'),
            ('services','hermes-account-services-result.json'),('native','native-account-services-result.json')]:
        data=read(ROOT/'evidence'/('realm-'+label)/name)
        if data is not None:
            result['functionalChecks'][label]={'passed':data.get('passed',False),'checks':len(data.get('checks',[]))}
    for label,name in [('social','custom-social-result.json'),('guild','guild-reservation-result.json')]:
        data=read(FIXTURE/name)
        if data is not None:
            result['functionalChecks'][label]={'passed':data.get('passed',False),'checks':len(data.get('checks',[]))}
    for label in ['smoke','group','trade','session','death','ulduar','stratholme','atlas-guild','atlas-dc']:
        data=read(ROOT/'evidence/protocol'/(label+'.json'))
        if data is not None:
            result['protocol'][label]={k:v for k,v in data.items() if k!='log'}
    dungeon=read(ROOT/'evidence/dungeon-clear-result.json')
    result['autonomousDungeon']=None if dungeon is None else {
        k:dungeon.get(k) for k in ['dungeon','level','result','failReason','bossesTotal','bossesKilled','durationS']}
    if dungeon is not None:
        result['autonomousDungeon']['partySize']=len(dungeon.get('comp',[]))
        result['autonomousDungeon']['deaths']=len(dungeon.get('deaths',[]))
    first_dungeon=read(ROOT/'evidence/dungeon-clear-first-server-result.json')
    result['firstAutonomousDungeon']=None if first_dungeon is None else {
        k:first_dungeon.get(k) for k in ['dungeon','level','result','bossesTotal','bossesKilled','durationS']}
    if first_dungeon is not None:
        result['firstAutonomousDungeon']['deaths']=len(first_dungeon.get('deaths',[]))
    if dungeon is not None and first_dungeon is not None:
        result['autonomousDungeon']['distinctFromFirstRun']=dungeon['runId']!=first_dungeon['runId']
    result['basicSummary']=read(ROOT/'evidence/realm-basic-summary.json')
    result['modulesSummary']=read(ROOT/'evidence/realm-modules-summary.json')
    result['runtimeLogSignals']={}
    # Counts only: never copy SQL statements, account names, chat or tokens.
    # These are diagnostic signals, not an assertion that every log is clean.
    for phase in ['gold','identity','services','native','modules-protocol','modules','guild']:
        directory=ROOT/'evidence'/('realm-'+phase)
        signals=collections.Counter()
        logs=list(directory.glob('*console.log'))
        for path in logs:
            text=path.read_text(errors='replace')
            signals.update('mysqlError'+code for code in re.findall(r'\[ERROR\]: \[(\d+)\]',text))
            for name,pattern in [('fatalSqlDeadlock',r'Fatal deadlocked SQL Transaction'),
                    ('segmentationFault',r'Segmentation fault'),
                    ('unhandledException',r'Unhandled exception'),
                    ('nullOpcode',r'send NULL_OPCODE')]:
                signals[name]+=len(re.findall(pattern,text))
        result['runtimeLogSignals'][phase]={'filesChecked':len(logs),'counts':dict(signals)}
    (ROOT/'evidence/public-summary.json').write_text(json.dumps(result,indent=2)+'\n')
    print('Wrote aggregate evidence only; missing phases remain null.')
    if finalize:
        basic=result['basicSummary'] or []
        modules=result['modulesSummary'] or []
        if (len(basic)!=4 or len(modules)!=5 or any(row['exit'] for row in basic+modules)
                or len(result['protocol'])!=9 or any(row['exit'] for row in result['protocol'].values())
                or result['cpp']['core']['failedTests']!=[]
                or len(result['cpp']['dungeon-clear']['failedTests'])!=5
                or not result['navigationFailuresMatchPrevious']
                or not result['additionalNavigationCheck']['passed']
                or len(result['hermesTests'])!=2 or any(row['failed'] or row['error'] or row['timeout']
                    or row['passed']<=0 for row in result['hermesTests'].values())
                or not all(row['passed'] for row in result['migrations'].values())
                or not result['productionCopyAuthReferences']['passed']
                or not result['protocolClockRegression']['passed']
                or not result['autonomousDungeon']['distinctFromFirstRun']
                or not result['apiRuntimeEquivalence']['passed']):
            raise RuntimeError('Finalization prerequisites not met; aggregate evidence is preserved.')
        for binary in ['worldserver','authserver']:
            if prepare_candidate.digest(ROOT/'server/bin'/binary)!=manifest[binary+'Sha256']:
                raise RuntimeError('Candidate binary changed since staging: '+binary)
        archive=ROOT/'evidence/build-manifest-pre-validation.json'
        if archive.exists():
            raise RuntimeError('Finalization already attempted; review the retained manifest first.')
        shutil.copy2(ROOT/'build/manifest.json',archive)
        manifest['validationState']='isolated checks complete with five known navigation failures; not activated'
        manifest['validationCompletedAt']=result['collectedAt']
        manifest['validationReport']=str(ROOT/'evidence/public-summary.json')
        manifest['productionActivationPerformed']=False
        (ROOT/'build/manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
        print('Finalized candidate metadata only; no production activation.')

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--finalize-manifest',action='store_true')
    collect(parser.parse_args().finalize_manifest)
