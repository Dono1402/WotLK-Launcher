#!/usr/bin/env python3
"""Adapt test paths and timestamp precision, retaining functional pass criteria."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
from patch_protocol_clock import adapt as adapt_protocol_clock

ROOT=Path('/opt/arthas-next/candidates/atlas-all-update-20260926')
FIXTURE=Path('/opt/atlas-shop-tests/rename-20260926')
OLD=Path('/opt/arthas-next/candidates/atlas-all-update-20260912')

def main():
    tests=ROOT/'tests'
    tests.mkdir(exist_ok=False)
    hashes={}
    for source in (ROOT/'inputs/prior-tests').glob('*'):
        if not source.is_file():
            continue
        text=source.read_text()
        text=text.replace('atlas-all-update-20260912','atlas-all-update-20260926')
        text=text.replace('rename-20260911','rename-20260926')
        text=text.replace('atlas-shop-rename-20260926-mysql','atlas-update-fixture-20260926-mysql')
        if source.name=='atlas_custom_e2e_test.go':
            text=adapt_protocol_clock(text)
        if source.name=='start_candidate_fixture.py':
            text=text.replace("'-20260912'","'-20260926'")
            text=text.replace('required_gib = 7 if all_modules else 6','required_gib = 14 if all_modules else 6')
            text=text.replace("('4800M' if all_modules else '4G')","('10G' if all_modules else '4G')")
            text=text.replace("'--property=MemoryMax=5G'","'--property=MemoryMax=' + ('12G' if all_modules else '5G')")
            text=text.replace('Keep the full module\n    # fixture capped at 5 GiB','Keep the full module\n    # fixture capped at 12 GiB')
        target=tests/source.name
        target.write_text(text)
        hashes[source.name]=hashlib.sha256(target.read_bytes()).hexdigest()
    helper=FIXTURE/'mod-atlas-shop/tests/hermes_realm_fixture.py'
    text=helper.read_text()
    old="AUTH = Path('/opt/arthas-next/candidates/dungeon-clear-20260830T183457Z/server/bin/authserver')"
    if text.count(old)!=1:
        raise RuntimeError('Unexpected auth helper layout.')
    helper.write_text(text.replace(old,"AUTH = Path('"+str(ROOT/'server/bin/authserver')+"')"))
    adapt_fixture_guards()
    (ROOT/'evidence/test-runner-inputs.json').write_text(json.dumps(hashes,indent=2))
    local=ROOT/'core/e2e/local/atlas'
    local.mkdir(parents=True,exist_ok=False)
    shutil.copy2(tests/'atlas_custom_e2e_test.go',local/'atlas_custom_e2e_test.go')
    build_go()

def adapt_fixture_guards():
    # Preserve exact-path safety checks while moving the reused tests to this
    # new disposable fixture. No assertion or production path is relaxed.
    directory=FIXTURE/'mod-atlas-shop/tests'
    for name in ['test_account_services_realm.py','test_hermes_realm.py','test_gold_conversion_realm.py']:
        path=directory/name
        text=path.read_text()
        text=text.replace('/opt/atlas-shop-tests/rename-20260911',str(FIXTURE))
        text=text.replace('atlas-all-update-20260912',ROOT.name)
        path.write_text(text)
    # The readiness oracle reads stdout. Fully buffered stdout can withhold the
    # ready line until shutdown even though the private world accepts clients.
    # Line-buffer only the owned fixture world's output, including its restart.
    path=directory/'run_realm_fixture.py'
    text=path.read_text()
    command="[str(world_binary), '--config', str(etc / 'worldserver.conf')]"
    buffered="['/usr/bin/stdbuf', '-oL', '-eL', str(world_binary), '--config', str(etc / 'worldserver.conf')]"
    if text.count(command)==2:
        path.write_text(text.replace(command,buffered))
    elif text.count(buffered)!=2:
        raise RuntimeError('Unexpected fixture world startup commands.')

def build_go(selection=None):
    output=ROOT/'e2e-bins'
    output.mkdir(exist_ok=True)
    go=OLD/'tools/go/bin/go'
    env={**os.environ,'HOME':str(ROOT/'tools/build-home'),'GOCACHE':str(ROOT/'tools/go-cache'),
         'GOMODCACHE':str(ROOT/'tools/go-mod-cache'),'GOMAXPROCS':'2',
         'PATH':str(go.parent)+':'+os.environ.get('PATH','/usr/bin:/bin')}
    packages={'smoke':'./smoke','group':'./suites/social/group','trade':'./suites/social/trade',
              'session':'./suites/protocol/session','death':'./suites/combat/death',
              'ulduar':'./suites/instances/northrend/ulduar','stratholme':'./suites/instances/classic/stratholme',
              'atlas-custom':'./local/atlas'}
    for name,package in packages.items():
        if selection is not None and name not in selection:
            continue
        print('BUILD protocol suite',name,flush=True)
        with (ROOT/'evidence'/('go-build-'+name+'.log')).open('w') as log:
            subprocess.run([str(go),'test','-tags=e2e','-c','-o',str(output/(name+'.test')),package],
                cwd=ROOT/'core/e2e',env=env,stdout=log,stderr=subprocess.STDOUT,check=True,timeout=600)
    print('PASS rebuilt existing protocol test suites',flush=True)

def clock_test():
    build_go({'atlas-custom'})
    with (ROOT/'evidence/protocol-clock-regression.log').open('w') as log:
        result=subprocess.run([str(ROOT/'e2e-bins/atlas-custom.test'),'-test.v',
            '-test.run=^TestAtlas_DungeonRecordFreshness$','-test.count=1','-test.timeout=30s'],
            stdout=log,stderr=subprocess.STDOUT,timeout=40)
    (ROOT/'evidence/protocol-clock-regression.json').write_text(json.dumps({
        'passed':result.returncode==0,'exit':result.returncode,'freshnessCases':5,
        'sourceSha256':hashlib.sha256((ROOT/'tests/atlas_custom_e2e_test.go').read_bytes()).hexdigest()},indent=2))
    if result.returncode:
        raise RuntimeError('Timestamp freshness regression failed.')
    print('PASS five timestamp and preexisting-run identity cases',flush=True)

if __name__=='__main__':
    import sys
    if sys.argv[1:]==['fixture-guards']:
        adapt_fixture_guards()
    elif sys.argv[1:]==['build']:
        build_go()
    elif sys.argv[1:]==['clock-test']:
        clock_test()
    else:
        main()
