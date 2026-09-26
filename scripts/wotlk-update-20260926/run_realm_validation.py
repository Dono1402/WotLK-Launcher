#!/usr/bin/env python3
"""Run bounded end-to-end phases exclusively against the synthetic private realm."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import os

ROOT=Path('/opt/arthas-next/candidates/atlas-all-update-20260926')
FIXTURE=Path('/opt/atlas-shop-tests/rename-20260926')

def fixture_command(pid,script,*args):
    if os.readlink('/proc/'+str(pid)+'/ns/net')==os.readlink('/proc/1/ns/net'):
        raise RuntimeError('Refuse tests in the host network namespace.')
    return ['nsenter','--target',str(pid),'--net','python3',str(script),*args]

def held(phase,label,checks):
    marker=FIXTURE/'stop-fixture'
    output=ROOT/'evidence'/('held-'+label+'.log')
    if output.exists():
        raise RuntimeError('Preserve existing held-phase evidence before any retry.')
    started=time.time()
    with output.open('w') as log:
        process=subprocess.Popen(['python3',str(ROOT/'tests/start_candidate_fixture.py'),phase],stdout=log,stderr=subprocess.STDOUT)
        try:
            deadline=time.monotonic()+900
            while time.monotonic()<deadline:
                if process.poll() is not None:
                    raise RuntimeError('Held fixture exited during startup: '+label)
                pidfile=FIXTURE/'fixture.pid'
                worldlog=FIXTURE/'logs/world-console.log'
                if (pidfile.exists() and pidfile.stat().st_mtime>=started and worldlog.exists()
                        and '(worldserver-daemon) ready...' in worldlog.read_text(errors='replace')):
                    pid=int(pidfile.read_text())
                    worldpid=int((FIXTURE/'world.pid').read_text())
                    if Path('/proc/'+str(worldpid)+'/exe').resolve()!=(ROOT/'build/worldserver').resolve():
                        raise RuntimeError('Unexpected fixture world executable.')
                    break
                time.sleep(2)
            else:
                raise RuntimeError('Held fixture startup exceeded 15 minutes.')
            rows=[]
            for name,script,args in checks:
                print('START held check',label,name,flush=True)
                with (ROOT/'evidence'/(label+'-'+name+'.log')).open('w') as checklog:
                    result=subprocess.run(fixture_command(pid,script,*args),stdout=checklog,stderr=subprocess.STDOUT,timeout=1700)
                rows.append({'name':name,'exit':result.returncode})
                (ROOT/'evidence'/(label+'-summary.json')).write_text(json.dumps(rows,indent=2))
                print('FINISH held check',label,name,'exit',result.returncode,flush=True)
            return rows
        finally:
            marker.write_text('Completed bounded candidate validation.\n')
            try:
                code=process.wait(timeout=180)
            except subprocess.TimeoutExpired:
                # Only the owned transient test unit; production unit names differ.
                subprocess.run(['systemctl','stop','atlas-all-update-realm-'+phase+'-20260926'],check=True,timeout=90)
                code=process.wait(timeout=90)
            if code:
                raise RuntimeError('Held fixture or its cleanup failed: '+label+' exit '+str(code))

def modules():
    if (ROOT/'evidence/held-protocol.log').exists() or (ROOT/'evidence/realm-modules-summary.json').exists():
        raise RuntimeError('Preserve and review earlier module results before a new run.')
    prerequisite=json.loads((ROOT/'evidence/realm-basic-summary.json').read_text())
    if len(prerequisite)!=4 or any(row['exit'] for row in prerequisite):
        raise RuntimeError('Basic candidate integration must pass before the combined module tests.')
    # This is a cold copy of the previous synthetic fixture. Remove only its old
    # synthetic bot memberships so two new reservation slots have one oracle.
    container='atlas-update-fixture-20260926-mysql'
    info=json.loads(subprocess.check_output(['docker','inspect',container],text=True))[0]
    if (info['HostConfig']['NetworkMode']!='none' or not any(m['Source']==str(FIXTURE/'mysql-data')
            and m['Destination']=='/var/lib/mysql' for m in info['Mounts']) or info['State']['Running']):
        raise RuntimeError('Expected the stopped synthetic fixture database.')
    subprocess.run(['docker','start',info['Id']],check=True,stdout=subprocess.DEVNULL)
    client=['mysql','--defaults-extra-file='+str(FIXTURE/'mysql-client.cnf'),
            '--database=shop_test_chars','-NBe']
    try:
        for _ in range(60):
            if subprocess.run(client+['SELECT 1'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode==0:break
            time.sleep(1)
        else:raise RuntimeError('Fixture MySQL readiness failed.')
        sql="""DELETE gm FROM shop_test_chars.guild_member gm
        JOIN shop_test_chars.characters c ON c.guid=gm.guid
        JOIN shop_test_auth.account a ON a.id=c.account
        JOIN shop_test_playerbots.playerbots_account_type p ON p.account_id=a.id
        WHERE a.username LIKE 'ATLASFIXTUREBOT%' AND p.account_type=1;
        SELECT ROW_COUNT();"""
        count=int(subprocess.check_output(client+[sql],text=True).strip())
        (ROOT/'evidence/synthetic-membership-reset.json').write_text(json.dumps({'rows':count,'fixture':str(FIXTURE)}))
    finally:
        subprocess.run(['docker','stop','--time','30',info['Id']],check=True,stdout=subprocess.DEVNULL)
    all_rows=[]
    all_rows+=held('modules','protocol',[
        ('upstream',ROOT/'tests/run_go_suites.py',['smoke','group','trade','session','death','ulduar','stratholme']),
        ('social',ROOT/'tests/test_custom_social_realm.py',[])])
    previous=ROOT/'evidence/realm-modules'
    previous.rename(ROOT/'evidence/realm-modules-protocol')
    all_rows+=held('modules','bots',[
        ('guild-setup',ROOT/'tests/run_go_suites.py',['atlas-guild']),
        ('dungeon-clear',ROOT/'tests/run_go_suites.py',['atlas-dc'])])
    all_rows+=held('guild','reservation',[
        ('priority-login',ROOT/'tests/verify_guild_reservation.py',[])])
    (ROOT/'evidence/realm-modules-summary.json').write_text(json.dumps(all_rows,indent=2))
    if any(row['exit'] for row in all_rows):
        raise RuntimeError('One or more combined module checks failed; preserve evidence.')
    print('PASS combined module and protocol phases',flush=True)

def basic():
    if (ROOT/'evidence/realm-basic-summary.json').exists():
        raise RuntimeError('Preserve and review earlier basic results before a new run.')
    deadline=time.monotonic()+5400
    required=[ROOT/'build/manifest.json',ROOT/'hermes/package-manifest.json',ROOT/'evidence/hermes-tests/success.json']
    while not all(p.exists() for p in required):
        if time.monotonic()>deadline:
            raise RuntimeError('Candidate staging or Hermes validation did not finish in the test window.')
        for unit in ['atlas-update-build-20260926','atlas-hermes-publish-20260926']:
            state=subprocess.check_output(['systemctl','show',unit,'-p','ActiveState','--value'],text=True).strip()
            if state=='failed':
                raise RuntimeError('Required candidate preparation failed: '+unit)
        time.sleep(15)
    results=[]
    for phase in ['gold','identity','services','native']:
        print('START realm integration',phase,flush=True)
        result=subprocess.run(['python3',str(ROOT/'tests/start_candidate_fixture.py'),phase],timeout=3000)
        results.append({'phase':phase,'exit':result.returncode})
        (ROOT/'evidence/realm-basic-summary.json').write_text(json.dumps(results,indent=2))
        if result.returncode:
            raise RuntimeError('Preserved failed realm phase for diagnosis: '+phase)
    print('PASS all basic realm integration phases',flush=True)

def dungeon_retry():
    evidence=ROOT/'evidence'
    rows=json.loads((evidence/'realm-modules-summary.json').read_text())
    if (len(rows)!=5 or [row['name'] for row in rows if row['exit']]!=['dungeon-clear']
            or not json.loads((evidence/'protocol-clock-regression.json').read_text())['passed']):
        raise RuntimeError('Only retry the reviewed dungeon result-clock failure after its regression test passes.')
    for unit in ['atlas-module-validation-20260926','atlas-all-update-realm-modules-20260926',
            'atlas-all-update-realm-guild-20260926']:
        state=subprocess.check_output(['systemctl','show',unit,'-p','ActiveState','--value'],text=True).strip()
        if state not in ['inactive','failed']:
            raise RuntimeError('Previous fixture still active: '+unit)
    archive=evidence/'module-before-clock-fix'
    archive.mkdir(exist_ok=False)
    for name in ['realm-modules-summary.json','realm-modules','held-bots.log','bots-summary.json',
            'bots-dungeon-clear.log','dungeon-clear-start.json','protocol/atlas-dc.log','protocol/atlas-dc.json']:
        path=evidence/name
        if path.exists():
            destination=archive/name
            destination.parent.mkdir(parents=True,exist_ok=True)
            path.rename(destination)
    retry=held('modules','dungeon-retry',[
        ('dungeon-clear',ROOT/'tests/run_go_suites.py',['atlas-dc'])])
    if len(retry)!=1:
        raise RuntimeError('Unexpected retry result.')
    for index,row in enumerate(rows):
        if row['name']=='dungeon-clear':
            rows[index]={**retry[0],'previousAttempt':'module-before-clock-fix/realm-modules-summary.json'}
    (evidence/'realm-modules-summary.json').write_text(json.dumps(rows,indent=2))
    if retry[0]['exit']:
        raise RuntimeError('Dungeon retry failed; all attempts are preserved.')
    print('PASS autonomous dungeon replay with corrected freshness oracle',flush=True)

def after_basic():
    deadline=time.monotonic()+3600
    while True:
        path=ROOT/'evidence/realm-basic-summary.json'
        rows=json.loads(path.read_text()) if path.exists() else []
        if any(row['exit'] for row in rows):
            raise RuntimeError('Basic validation failed; combined module tests were not started.')
        if len(rows)==4:
            break
        state=subprocess.check_output(['systemctl','show','atlas-basic-validation-20260926',
            '-p','ActiveState','--value'],text=True).strip()
        if state=='failed' or time.monotonic()>deadline:
            raise RuntimeError('Basic validation failed or did not finish within one hour.')
        time.sleep(15)
    # The final basic phase writes its summary just before stopping its MySQL.
    # Wait for the owner to finish cleanup before starting the next fixture.
    while subprocess.check_output(['systemctl','show','atlas-basic-validation-20260926',
            '-p','ActiveState','--value'],text=True).strip()=='active':
        if time.monotonic()>deadline:raise RuntimeError('Basic cleanup timed out.')
        time.sleep(2)
    modules()

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('phase',choices=['basic','modules','after-basic','dungeon-retry'])
    globals()[p.parse_args().phase.replace('-','_')]()
