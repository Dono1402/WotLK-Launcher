#!/usr/bin/env python3
"""Run the final World/API bytes only in the existing private realm fixture."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
from prepare_backend import API, HERMES, ROOT, WORLD, digest

FIXTURE=Path('/opt/atlas-shop-tests/rename-20260911')

def main():
    os.umask(0o077)
    plan=json.loads((ROOT/'plan.json').read_text())
    if digest(WORLD/'build/worldserver')!=plan['worldSha256']: raise RuntimeError('World artifact changed')
    if (WORLD/'server/etc').resolve()!=FIXTURE/'etc': raise RuntimeError('World no longer points to test configuration')
    for proc in Path('/proc').glob('[0-9]*/exe'):
        try:
            if str(proc.resolve()).startswith(str(FIXTURE)+'/'): raise RuntimeError('A fixture executable is still active')
        except (FileNotFoundError,PermissionError): pass
    for relative,sha in plan['files'].items():
        package,path=relative.split('/',1)
        source=(API if package=='api' else HERMES)/path
        if digest(source)!=sha: raise RuntimeError('Release package changed')
        if package=='api':
            target=FIXTURE/'api-account-services'/path
            target.parent.mkdir(parents=True,exist_ok=True)
            shutil.copy2(source,target)
    container_id=(FIXTURE/'container-id').read_text().strip()
    info=json.loads(subprocess.check_output(['docker','inspect',container_id],text=True))[0]
    if info['Name']!='/atlas-shop-rename-20260911-mysql' or info['HostConfig']['NetworkMode']!='none': raise RuntimeError('Wrong MySQL fixture')
    if {x['Destination']:x['Source'] for x in info['Mounts']}.get('/var/lib/mysql')!=str(FIXTURE/'mysql-data'): raise RuntimeError('Wrong fixture mount')
    if not info['State']['Running']: subprocess.run(['docker','start',container_id],check=True,stdout=subprocess.DEVNULL)
    log=FIXTURE/'logs/native-release-final.log'
    command=['systemd-run','--unit=atlas-shop-final-candidate-20260912','--wait','--collect',
        '--property=MemoryMax=3G','--property=MemorySwapMax=0','--property=CPUQuota=150%',
        '--property=Nice=10','--property=IOWeight=25','--property=PrivateNetwork=yes',
        '--property=ProtectSystem=strict','--property=ReadWritePaths='+str(FIXTURE),
        '--property=NoNewPrivileges=yes','--property=RuntimeMaxSec=600',
        '--property=StandardOutput=append:'+str(log),'--property=StandardError=append:'+str(log),
        '/usr/bin/python3',str(FIXTURE/'mod-atlas-shop/tests/run_realm_fixture.py'),'--root',str(FIXTURE),
        '--account-services','--api-package','api-account-services','--with-hermes','--hermes-package','hermes-native',
        '--world-candidate',str(WORLD)]
    try:
        result=subprocess.run(command,timeout=660)
        print(log.read_text()[-6000:])
        if result.returncode: raise RuntimeError('Final candidate scenario failed')
        proof=json.loads((FIXTURE/'hermes-account-services-result.json').read_text())
        if not proof.get('passed'): raise RuntimeError('Final fixture report did not pass')
        proof.update(worldSha256=plan['worldSha256'],apiSha256=plan['files']['api/WotLK.Launcher.Server'],
                     hermesSha256=plan['files']['hermes/HermesProxy'],completedAtUnix=int(time.time()))
        (ROOT/'final-candidate-test.json').write_text(json.dumps(proof,indent=2)+'\n')
    finally:
        subprocess.run(['python3',str(FIXTURE/'mod-atlas-shop/tests/prepare_realm_fixture.py'),
                        '--root',str(FIXTURE),'--stop'],check=True)

if __name__=='__main__': main()
