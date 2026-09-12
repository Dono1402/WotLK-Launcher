#!/usr/bin/env python3
"""Stage the API/Hermes releases and public configuration without activation."""
import hashlib
import json
import os
from pathlib import Path
import pwd
import shutil
import subprocess
import tarfile

ROOT=Path('/opt/atlas-shop-releases/native-1.7.0-20260912')
WORLD=Path('/opt/arthas-next/candidates/atlas-shop-rename-native-20260912')
API=Path('/opt/wotlk-launcher-api-releases/shop-native-1.7.0-20260912')
HERMES=Path('/opt/hermesproxy-wotlk/releases/hermes-shop-native-1.7.0-20260912')

def digest(path):
    with Path(path).open('rb') as stream: return hashlib.file_digest(stream,'sha256').hexdigest()

def main():
    os.umask(0o077)
    if os.geteuid()!=0: raise RuntimeError('Expected root.')
    if any(path.exists() or path.is_symlink() for path in (ROOT,API,HERMES,WORLD/'server/etc-production')):
        raise RuntimeError('A prepared release path exists; inspect before any retry.')
    for path in (WORLD,API.parent,HERMES.parent):
        if path.resolve(strict=True)!=path: raise RuntimeError('Unexpected release path')
    expected=json.loads(Path('/tmp/atlas-shop-170-live.json').read_text())
    for name,service in expected['services'].items():
        pid=subprocess.check_output(['systemctl','show',name,'-p','MainPID','--value'],text=True).strip()
        if pid!=service['MainPID']: raise RuntimeError('Service identity changed')
        if str((Path('/proc')/pid/'exe').resolve())!=service['Executable'] or digest(service['Executable'])!=service['ExecutableSha256']:
            raise RuntimeError('Active executable changed: '+name)
        for config,sha in service['UnitFileSha256'].items():
            if digest(config)!=sha: raise RuntimeError('Service definition changed')
    source_world=Path(expected['world']['configPath'])
    if digest(source_world)!=expected['world']['configSha256'] or expected['world']['options']['CharacterDatabase.WorkerThreads']!='4':
        raise RuntimeError('World configuration differs from the reviewed four-worker setup.')
    api_config=Path('/opt/wotlk-launcher-api/appsettings.json')
    if digest(api_config)!=expected['api']['configurationFiles'][str(api_config)]: raise RuntimeError('API config changed')
    if digest('/opt/hermesproxy-wotlk/appsettings.atlas.json')!=expected['hermes']['configSha256']: raise RuntimeError('Hermes config changed')
    files=json.loads(Path('/tmp/atlas-shop-170-backend-files.json').read_text())
    with tarfile.open('/tmp/atlas-shop-170-backend.tar.gz') as archive:
        members=archive.getmembers()
        if set(item.name for item in members)!=set(files): raise RuntimeError('Archive inventory differs')
        for item in members:
            if not item.isfile() or item.name.startswith('/') or '..' in Path(item.name).parts:
                raise RuntimeError('Unsafe archive entry')
            if item.name.split('/')[0] not in ('api','hermes'): raise RuntimeError('Unknown package')
            if digest_bytes(archive.extractfile(item).read())!=files[item.name]: raise RuntimeError('Archive digest mismatch')
        ROOT.mkdir(parents=True,mode=0o700)
        API.mkdir(mode=0o755); HERMES.mkdir(mode=0o755)
        for item in members:
            prefix,relative=item.name.split('/',1)
            target=(API if prefix=='api' else HERMES)/relative
            target.parent.mkdir(parents=True,exist_ok=True)
            target.write_bytes(archive.extractfile(item).read())
            target.chmod(0o555 if target.name in ('WotLK.Launcher.Server','HermesProxy') else 0o444)
    shutil.copy2(api_config,API/'appsettings.json')
    os.chown(API/'appsettings.json',0,pwd.getpwnam('wotlklauncher').pw_gid)
    (API/'appsettings.json').chmod(0o640)
    active_hermes=Path(expected['services']['hermesproxy-wotlk']['WorkingDirectory'])
    for name in ('AccountData','Logs','PacketsLog'):
        source=active_hermes/name
        destination=Path('/opt/hermesproxy-wotlk')/name
        if not source.is_symlink() or source.resolve(strict=True)!=destination or destination.resolve()!=destination:
            raise RuntimeError('Shared Hermes directory differs')
        (HERMES/name).symlink_to(destination,target_is_directory=True)
    production=WORLD/'server/etc-production'
    production.mkdir(mode=0o750)
    shutil.copy2(source_world,production/'worldserver.conf')
    (production/'modules').mkdir(mode=0o750)
    original_modules=Path('/opt/arthas-next/candidates/modules-update-20260905T1016Z/server/etc/modules')
    for name,sha in expected['world']['moduleConfigHashes'].items():
        source=original_modules/name
        if source.is_symlink() or not source.is_file() or digest(source)!=sha: raise RuntimeError('Module configuration changed')
        shutil.copy2(source,production/'modules'/name)
    (production/'modules/mod_atlas_shop.conf').write_text('[worldserver]\nAtlasShop.Enable = 1\nAtlasShop.AccountServices = 1\n')
    acore=pwd.getpwnam('acore')
    for directory in (WORLD,WORLD/'server',WORLD/'server/bin',WORLD/'build',production,production/'modules'):
        os.chown(directory,0,acore.pw_gid); directory.chmod(0o750)
    for path in production.rglob('*'):
        if path.is_file(): os.chown(path,0,acore.pw_gid); path.chmod(0o640)
    os.chown(WORLD/'build/worldserver',0,acore.pw_gid); (WORLD/'build/worldserver').chmod(0o550)
    for folder in (API,HERMES):
        folder.chmod(0o755)
        for path in folder.rglob('*'):
            if path.is_dir() and not path.is_symlink(): path.chmod(0o755)
    plan={'version':'1.7.0','state':'prepared-not-activated','world':str(WORLD),'api':str(API),'hermes':str(HERMES),
          'activeBefore':expected,'files':files,'worldSha256':digest(WORLD/'build/worldserver'),
          'apiConfigSha256':digest(API/'appsettings.json'),
          'worldProductionConfig':{str(path.relative_to(production)):digest(path) for path in production.rglob('*') if path.is_file()},
          'requiredSchema':13,'characterDatabaseWorkers':4,'productionActivated':False}
    (ROOT/'plan.json').write_text(json.dumps(plan,indent=2)+'\n')
    (ROOT/'baseline.json').write_text(json.dumps(expected,indent=2)+'\n')
    print(json.dumps({key:plan[key] for key in ('version','state','world','api','hermes','worldSha256','requiredSchema','characterDatabaseWorkers')},indent=2))
    print('PASS: staged releases and production configurations; no service, public pointer or database changed.')

def digest_bytes(data): return hashlib.sha256(data).hexdigest()
if __name__=='__main__': main()
