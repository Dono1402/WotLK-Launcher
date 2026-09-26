#!/usr/bin/env python3
"""Cold-copy stopped databases into networkless test containers. No production start."""
import argparse
import configparser
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260926')
FIXTURE = Path('/opt/atlas-shop-tests/rename-20260926')
OLD_FIXTURE = Path('/opt/atlas-shop-tests/rename-20260911')
TARGETS = {
    'production-copy': ('arthas-mysql', Path('/opt/arthas/mysql'), ROOT/'private/migration-copy',
                        'atlas-update-migration-20260926-mysql', 'arthas_world'),
    'fixture': ('atlas-shop-rename-20260911-mysql', OLD_FIXTURE/'mysql-data', FIXTURE,
                'atlas-update-fixture-20260926-mysql', 'shop_test_world'),
}

def inspect(name):
    return json.loads(subprocess.check_output(['docker','inspect',name],text=True))[0]

def write(path, data):
    path.write_text(data)
    path.chmod(0o600)

def mysql(root, sql):
    return subprocess.check_output(['mysql','--defaults-extra-file='+str(root/'mysql-client.cnf'),
                                   '-NBe',sql],text=True,stderr=subprocess.PIPE,timeout=180)

def check_owned(kind):
    _,_,target,name,_=TARGETS[kind]
    info=inspect(name)
    mounts={m['Destination']:m['Source'] for m in info['Mounts']}
    if info['HostConfig']['NetworkMode'] != 'none' or mounts.get('/var/lib/mysql') != str(target/'mysql-data'):
        raise RuntimeError('Unexpected test container identity.')
    return target,info

def prepare(kind):
    source_name,source,target,name,_=TARGETS[kind]
    os.umask(0o077)
    if target.exists() or source.resolve(strict=True) != source:
        raise RuntimeError('Refuse overwrite or unexpected source path.')
    info=inspect(source_name)
    if info['State']['Running'] or inspect('arthas-mysql')['State']['Running']:
        raise RuntimeError('Production and cold-copy source must remain stopped.')
    if not any(m['Source']==str(source) and m['Destination']=='/var/lib/mysql' for m in info['Mounts']):
        raise RuntimeError('Source mount mismatch.')
    target.mkdir(parents=True,mode=0o700)
    for rel in ['mysql-data','socket','logs']:
        (target/rel).mkdir()
    (target/'socket').chmod(0o777)
    print('COPY',kind,flush=True)
    subprocess.run(['cp','-a','--reflink=auto',str(source)+'/.',target/'mysql-data'],check=True)
    # Verify every copied file while BOTH databases are still stopped.
    difference=subprocess.check_output(['rsync','-aHnci','--numeric-ids',str(source)+'/',str(target/'mysql-data')+'/'],text=True)
    if difference.strip() or inspect(source_name)['State']['Running']:
        raise RuntimeError('Cold-copy checksum verification failed or source restarted.')
    if kind=='fixture':
        config=configparser.ConfigParser()
        config.read(OLD_FIXTURE/'mysql-client.cnf')
        password=config['client']['password']
        for rel in ['api-gold','mod-atlas-shop','seed']:
            shutil.copytree(OLD_FIXTURE/rel,target/rel,ignore=shutil.ignore_patterns('__pycache__'))
        for rel in ['etc/modules','media','empty-source']:
            (target/rel).mkdir(parents=True)
    else:
        env=dict(x.split('=',1) for x in info['Config']['Env'] if '=' in x)
        password=env['MYSQL_ROOT_PASSWORD']
    write(target/'mysql.env','MYSQL_ROOT_PASSWORD='+password+'\n')
    write(target/'mysql-client.cnf','[client]\nuser=root\npassword='+password+'\nprotocol=SOCKET\nsocket='+str(target/'socket/mysql.sock')+'\n')
    command=['docker','run','-d','--name',name,'--network=none','--cpus=1','--memory=2g','--memory-swap=2g',
             '--pids-limit=180','--env-file',target/'mysql.env',
             '--mount','type=bind,src='+str(target/'mysql-data')+',dst=/var/lib/mysql',
             '--mount','type=bind,src='+str(target/'socket')+',dst=/socket',info['Image'],
             '--socket=/socket/mysql.sock','--skip-networking','--mysqlx=OFF','--skip-log-bin',
             '--innodb-buffer-pool-size=512M','--performance-schema=OFF','--max-connections=120']
    container_id=subprocess.check_output([str(x) for x in command],text=True).strip()
    write(target/'container-id',container_id+'\n')
    for _ in range(120):
        try:
            mysql(target,'SELECT 1;')
            break
        except (subprocess.CalledProcessError,subprocess.TimeoutExpired):
            time.sleep(1)
    else:
        raise RuntimeError('Isolated MySQL startup timed out.')
    report={'kind':kind,'sourceContainer':source_name,'sourceImage':info['Image'],'sourceStopped':True,
            'coldCopyChecksumVerified':True,'network':'none','containerId':container_id,'productionStartPerformed':False}
    write(target/'copy-manifest.json',json.dumps(report,indent=2))
    print('PASS cold-copy checksum and isolated SQL readiness:',kind,flush=True)

def migrations(kind):
    target,info=check_owned(kind)
    database=TARGETS[kind][4]
    if not info['State']['Running']:
        raise RuntimeError('Test container must be running.')
    if (target/'migration-result.json').exists():
        raise RuntimeError('Preserve and review the previous migration result before any retry.')
    files=json.loads((ROOT/'inputs/world-migrations.json').read_text())
    if len(files)!=62:
        raise RuntimeError('Expected 62 upstream SQL files to reconcile, not blindly reapply.')
    rows=mysql(target,'SELECT name,hash FROM '+database+'.updates;')
    existing=dict(line.split('\t',1) for line in rows.splitlines())
    report={'database':database,'pending':[],'alreadyApplied':[],'passed':False}
    for entry in files:
        path=ROOT/'core'/entry
        if not path.is_file() or path.parent != ROOT/'core/data/sql/updates/db_world':
            raise RuntimeError('Unexpected migration path.')
        digest=hashlib.sha1(path.read_bytes()).hexdigest().upper()
        if path.name in existing:
            if existing[path.name].upper()!=digest:
                raise RuntimeError('Already-recorded migration hash differs: '+path.name)
            report['alreadyApplied'].append(path.name)
            continue
        start=time.monotonic()
        with path.open('rb') as stream, (target/'logs'/('migration-'+path.name+'.log')).open('wb') as log:
            subprocess.run(['mysql','--defaults-extra-file='+str(target/'mysql-client.cnf'),database],
                           stdin=stream,stdout=log,stderr=subprocess.STDOUT,timeout=180,check=True)
        elapsed=int((time.monotonic()-start)*1000)
        mysql(target,"INSERT INTO "+database+".updates (name,hash,state,speed) VALUES ('"+path.name+"','"+digest+"','RELEASED',"+str(elapsed)+");")
        report['pending'].append({'name':path.name,'sha1':digest,'milliseconds':elapsed})
        write(target/'migration-result.json',json.dumps(report,indent=2))
    report['passed']=True
    report['recordedUpdatesAfter']=int(mysql(target,'SELECT COUNT(*) FROM '+database+'.updates;').strip())
    write(target/'migration-result.json',json.dumps(report,indent=2))
    print('PASS SQL:',kind,len(report['pending']),'applied,',len(report['alreadyApplied']),'already applied',flush=True)

def stop(kind):
    target,info=check_owned(kind)
    if info['State']['Running']:
        subprocess.run(['docker','stop','--time','30',info['Id']],check=True,stdout=subprocess.DEVNULL)
    print('STOP isolated database only:',kind,flush=True)

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('phase',choices=['prepare','migrations','stop'])
    p.add_argument('kind',choices=TARGETS)
    args=p.parse_args()
    globals()[args.phase](args.kind)
