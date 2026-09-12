#!/usr/bin/env python3
"""Stage, test, then activate an explicitly authorized Hermes-only fix."""
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tarfile
import time

ROOT = Path('/opt/atlas-shop-releases/vas-selector-20260912')
UPLOAD = Path('/tmp/atlas-hermes-vas-selector-20260912')
HERMES = Path('/opt/hermesproxy-wotlk/releases/hermes-vas-selector-20260912')
FIXTURE = Path('/opt/atlas-shop-tests/rename-20260911')
CONFIG = Path('/opt/hermesproxy-wotlk/appsettings.atlas.json')
OVERRIDE = Path('/etc/systemd/system/hermesproxy-wotlk.service.d/zzzzzz-atlas-shop-vas-selector-20260912.conf')
SERVICE = 'hermesproxy-wotlk'
RELEASE = 'vas-selector-20260912'

def configure_release(release):
    global ROOT, UPLOAD, HERMES, OVERRIDE, RELEASE
    if release not in ('vas-selector-20260912', 'vas-wire-type-20260912', 'name-response-20260912'):
        raise ValueError('Unknown Hermes release')
    RELEASE = release
    ROOT = Path('/opt/atlas-shop-releases') / release
    UPLOAD = Path('/tmp') / ('atlas-hermes-' + release)
    HERMES = Path('/opt/hermesproxy-wotlk/releases') / ('hermes-' + release)
    OVERRIDE = Path('/etc/systemd/system/hermesproxy-wotlk.service.d') / ('zzzzzz-atlas-shop-' + release + '.conf')
    if release == 'name-response-20260912':
        UPLOAD = Path('/tmp/atlas-name-response-20260912')
        OVERRIDE = OVERRIDE.with_name('zzzzzzzz-atlas-shop-' + release + '.conf')

def run(args, **kwargs):
    return subprocess.check_output(args, text=True, timeout=60, **kwargs).strip()

def sha(path):
    with Path(path).open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()

def state(name):
    return dict(line.split('=',1) for line in run(['systemctl','show',name,'-p',
        'MainPID,ActiveState,NRestarts,WorkingDirectory,DropInPaths,ExecMainStartTimestampMonotonic']).splitlines())

def write(path, data):
    path.write_text(json.dumps(data, indent=2)+'\n')

def verify_baseline(before, include_hermes=True):
    for name, expected in before['services'].items():
        if name == SERVICE and not include_hermes: continue
        current = state(name)
        if current['ActiveState'] != 'active' or current['MainPID'] != expected['MainPID']:
            raise RuntimeError('Active process changed: '+name)
        if sha('/proc/'+current['MainPID']+'/exe') != expected['sha256']:
            raise RuntimeError('Executable changed: '+name)
        if current['DropInPaths'] != expected['DropInPaths']:
            raise RuntimeError('Service override inventory changed: '+name)
        for path, checksum in expected['unitFiles'].items():
            if sha(path) != checksum: raise RuntimeError('Service definition changed')
    if sha(CONFIG) != before['services'][SERVICE]['configurationHash']:
        raise RuntimeError('Hermes configuration changed')

def prepare():
    if ROOT.exists() or HERMES.exists() or OVERRIDE.exists(): raise RuntimeError('Release destination already exists')
    manifest=json.loads((UPLOAD/'candidate.json').read_text())
    before=json.loads((UPLOAD/'before.json').read_text(encoding='utf-8-sig'))
    verify_baseline(before)
    if any(Path(path).name >= OVERRIDE.name for path in before['services'][SERVICE]['DropInPaths'].split()):
        raise RuntimeError('A later Hermes override already exists')
    if sha(UPLOAD/'candidate.tar.gz') != manifest['archiveSha256']: raise RuntimeError('Archive hash mismatch')
    ROOT.mkdir(mode=0o700); HERMES.mkdir(mode=0o755)
    HERMES.chmod(0o755)
    (ROOT/'tests').mkdir(); backup=ROOT/'backup'; backup.mkdir()
    shutil.copy2(UPLOAD/'candidate.json',ROOT/'candidate.json')
    shutil.copy2(UPLOAD/'before.json',ROOT/'before.json')
    shutil.copy2(Path(__file__).resolve(),ROOT/'deploy.py')
    with tarfile.open(UPLOAD/'candidate.tar.gz') as archive:
        for item in archive.getmembers():
            if not item.isfile() or item.issym() or item.islnk(): raise RuntimeError('Unexpected archive member')
            prefix, relative = item.name.split('/',1)
            target=(HERMES if prefix=='hermes' else ROOT/'tests')/relative
            parent=HERMES if prefix=='hermes' else ROOT/'tests'
            if prefix not in ('hermes','tests') or parent not in target.resolve().parents:
                raise RuntimeError('Unsafe archive target')
            if prefix=='hermes' and relative not in manifest['files']: raise RuntimeError('Unlisted candidate file')
            target.parent.mkdir(parents=True,exist_ok=True)
            target.write_bytes(archive.extractfile(item).read())
            target.chmod(0o555 if relative=='HermesProxy' else 0o444)
    for path in HERMES.rglob('*'):
        if path.is_dir(): path.chmod(0o755)
    for name, checksum in manifest['files'].items():
        if sha(HERMES/name)!=checksum: raise RuntimeError('Candidate hash mismatch')
    old=before['services'][SERVICE]
    for path in [Path(old['executable']), CONFIG, *(Path(p) for p in old['unitFiles'])]:
        destination=backup/path.relative_to('/')
        destination.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(path,destination)
        if sha(path)!=sha(destination): raise RuntimeError('Backup hash mismatch')
    for name in ('AccountData','Logs','PacketsLog'):
        active=Path(old['WorkingDirectory'])/name
        shared=Path('/opt/hermesproxy-wotlk')/name
        if not active.is_symlink() or active.resolve(strict=True)!=shared or shared.resolve()!=shared:
            raise RuntimeError('Unexpected shared runtime directory')
        (HERMES/name).symlink_to(shared,target_is_directory=True)
    run(['runuser','-u','hermesproxy','--','test','-x',str(HERMES/'HermesProxy')])
    run(['runuser','-u','hermesproxy','--','test','-r',str(CONFIG)])
    override='[Service]\nWorkingDirectory='+str(HERMES)+'\nExecStart=\nExecStart='+str(HERMES/'HermesProxy')+' --config '+str(CONFIG)+'\n'
    (ROOT/'planned-override.conf').write_text(override)
    rendered=ROOT/'hermesproxy-wotlk.service'
    rendered.write_text(run(['systemctl','cat',SERVICE])+'\n'+override)
    run(['systemd-analyze','verify',str(rendered)],stderr=subprocess.PIPE)
    write(ROOT/'prepared.json',{'prepared':True,'hermesSha256':manifest['files']['HermesProxy'],
        'sourceCommit':manifest['sourceCommit'],'backupVerified':True,'publicProcessesUnchanged':True})
    print('PASS: candidate staged; previous executable and configuration backed up; public processes unchanged.',flush=True)

def test():
    manifest=json.loads((ROOT/'candidate.json').read_text())
    before=json.loads((ROOT/'before.json').read_text(encoding='utf-8-sig'))
    verify_baseline(before)
    for proc in Path('/proc').glob('[0-9]*/exe'):
        try:
            if str(proc.resolve()).startswith(str(FIXTURE)+'/'): raise RuntimeError('Fixture is still active')
        except (FileNotFoundError,PermissionError): pass
    prior=FIXTURE/('hermes-native-before-' + RELEASE)
    retry = prior.exists()
    if not retry: (FIXTURE/'hermes-native').rename(prior)
    for name, checksum in manifest['files'].items():
        source=HERMES/name
        if sha(source)!=checksum: raise RuntimeError('Candidate changed')
        target=FIXTURE/'hermes-native'/name
        if retry:
            if sha(target)!=checksum: raise RuntimeError('Previously staged fixture candidate changed')
        else:
            target.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(source,target)
    for path in (ROOT/'tests').glob('*.py'): shutil.copy2(path,FIXTURE/'mod-atlas-shop/tests'/path.name)
    container=(FIXTURE/'container-id').read_text().strip()
    info=json.loads(run(['docker','inspect',container]))[0]
    if info['Name']!='/atlas-shop-rename-20260911-mysql' or info['HostConfig']['NetworkMode']!='none':
        raise RuntimeError('Wrong fixture container')
    if {p['Destination']:p['Source'] for p in info['Mounts']}.get('/var/lib/mysql')!=str(FIXTURE/'mysql-data'):
        raise RuntimeError('Wrong fixture mount')
    if not info['State']['Running']: run(['docker','start',container])
    log=FIXTURE/'logs'/(RELEASE + '.log')
    try:
        suites = [('services', [], 'hermes-account-services-result.json')]
        if RELEASE == 'name-response-20260912':
            suites.insert(0, ('identity', ['--rename-identity'], 'hermes-identity-result.json'))
        proofs = {}
        for suite, extra, result_file in suites:
            result=subprocess.run(['systemd-run','--unit=atlas-shop-' + RELEASE + '-' + suite + '-test','--wait','--collect',
            '--property=MemoryMax=3G','--property=MemorySwapMax=0','--property=CPUQuota=150%',
            '--property=Nice=10','--property=IOWeight=25','--property=PrivateNetwork=yes',
            '--property=ProtectSystem=strict','--property=ReadWritePaths='+str(FIXTURE),
            '--property=NoNewPrivileges=yes','--property=RuntimeMaxSec=600',
            '--property=StandardOutput=append:'+str(log),'--property=StandardError=append:'+str(log),
            '/usr/bin/python3',str(FIXTURE/'mod-atlas-shop/tests/run_realm_fixture.py'),'--root',str(FIXTURE),
            '--account-services','--api-package','api-gold','--with-hermes','--hermes-package','hermes-native',*extra],timeout=660)
            proof=json.loads((FIXTURE/result_file).read_text())
            if result.returncode or not proof.get('passed'): raise RuntimeError('Candidate '+suite+' test failed; inspect private fixture log')
            proof['hermesSha256']=manifest['files']['HermesProxy']
            proof['worldSha256']=sha(FIXTURE/'build-native/worldserver')
            proofs[suite]=proof
            write(ROOT/('tested-'+suite+'.json'),proof)
            print('PASS: isolated '+suite+' suite, '+str(len(proof['checks']))+' checks.',flush=True)
        write(ROOT/'tested.json',{'passed':True,'hermesSha256':manifest['files']['HermesProxy'],'suites':proofs})
    finally:
        run(['python3',str(FIXTURE/'mod-atlas-shop/tests/prepare_realm_fixture.py'),'--root',str(FIXTURE),'--stop'])
        verify_baseline(before)

def activate():
    before=json.loads((ROOT/'before.json').read_text(encoding='utf-8-sig'))
    manifest=json.loads((ROOT/'candidate.json').read_text())
    proof=json.loads((ROOT/'tested.json').read_text())
    verify_baseline(before)
    if not proof.get('passed') or proof['hermesSha256']!=manifest['files']['HermesProxy']:
        raise RuntimeError('Candidate has not passed the isolated network test')
    if RELEASE == 'name-response-20260912' and set(proof.get('suites',{})) != {'identity','services'}:
        raise RuntimeError('Both name-timing and native-service regression suites are required')
    for name, checksum in manifest['files'].items():
        if sha(HERMES/name)!=checksum: raise RuntimeError('Tested bytes changed')
    if OVERRIDE.exists(): raise RuntimeError('Activation override already exists')
    override='[Service]\nWorkingDirectory='+str(HERMES)+'\nExecStart=\nExecStart='+str(HERMES/'HermesProxy')+' --config '+str(CONFIG)+'\n'
    planned=ROOT/'planned-override.conf'
    if planned.exists() and planned.read_text()!=override: raise RuntimeError('Prepared override changed')
    started=time.time()
    config=json.loads(CONFIG.read_text())
    network=config.get('ProxyNetworkOptions',{})
    ports=[network.get(key,default) for key,default in [('RestPort',8081),('BNetPort',1119),('RealmPort',8084),('InstancePort',8086)]]
    def ready():
        s=state(SERVICE)
        if s['ActiveState']!='active' or s['MainPID']==before['services'][SERVICE]['MainPID'] or s['MainPID']=='0': return False
        if str((Path('/proc')/s['MainPID']/'exe').resolve())!=str(HERMES/'HermesProxy'): return False
        for port in ports:
            try:
                with socket.create_connection(('127.0.0.1',int(port)),timeout=1): pass
            except OSError: return False
        return True
    try:
        OVERRIDE.write_text(override); OVERRIDE.chmod(0o644)
        run(['systemctl','daemon-reload'])
        run(['systemctl','restart',SERVICE])
        deadline=time.monotonic()+90
        while time.monotonic()<deadline:
            if ready(): break
            time.sleep(2)
        else: raise RuntimeError('New Hermes listener startup timed out')
        verify_baseline(before,include_hermes=False)
        current=state(SERVICE)
        if sha('/proc/'+current['MainPID']+'/exe')!=manifest['files']['HermesProxy']: raise RuntimeError('Active binary differs')
        write(ROOT/'activated.json',{'activated':True,'service':SERVICE,'state':current,
            'hermesSha256':manifest['files']['HermesProxy'],'sourceCommit':manifest['sourceCommit'],
            'onlyHermesRestarted':True,'preservedProcesses':{k:v['MainPID'] for k,v in before['services'].items() if k!=SERVICE},
            'configurationSha256':sha(CONFIG),'backupVerified':True,'ports':ports,
            'activatedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'durationSeconds':round(time.time()-started,2)})
        print((ROOT/'activated.json').read_text(),flush=True)
    except BaseException:
        if OVERRIDE.is_file() and OVERRIDE.read_text()==override:
            OVERRIDE.unlink(); run(['systemctl','daemon-reload']); run(['systemctl','restart',SERVICE])
            old=state(SERVICE)
            if old['ActiveState']!='active' or sha('/proc/'+old['MainPID']+'/exe')!=before['services'][SERVICE]['sha256']:
                raise RuntimeError('Activation failed and automatic Hermes rollback needs attention')
            print('Activation failed; the prior Hermes executable has been restored.',flush=True)
        raise

if __name__=='__main__':
    import argparse
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase',choices=('prepare','test','activate'))
    parser.add_argument('--release',default=RELEASE,choices=('vas-selector-20260912','vas-wire-type-20260912','name-response-20260912'))
    args=parser.parse_args()
    os.umask(0o077)
    if os.geteuid()!=0: raise SystemExit('Expected root')
    configure_release(args.release)
    globals()[args.phase]()
