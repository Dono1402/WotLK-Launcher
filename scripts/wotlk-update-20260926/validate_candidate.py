#!/usr/bin/env python3
"""Stage and validate only the isolated September 26 candidate."""
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

ROOT=Path('/opt/arthas-next/candidates/atlas-all-update-20260926')
FIXTURE=Path('/opt/atlas-shop-tests/rename-20260926')
DOTNET=Path('/opt/hermesproxy-candidates/hermes-2e84f0b-custom-20260901/.dotnet/dotnet')

def digest(path):
    with path.open('rb') as f:
        return hashlib.file_digest(f,'sha256').hexdigest()

def run(args,**kw):
    return subprocess.run([str(x) for x in args],check=True,**kw)

def private_network():
    if os.readlink('/proc/self/ns/net')==os.readlink('/proc/1/ns/net'):
        raise RuntimeError('PrivateNetwork=yes is required for tests.')

def stage():
    if not json.loads((ROOT/'evidence/build-success.json').read_text()).get('passed'):
        raise RuntimeError('Successful full build is required.')
    cache=(ROOT/'build/CMakeCache.txt').read_text()
    if 'CMAKE_INSTALL_PREFIX:PATH='+str(ROOT/'server')+'\n' not in cache:
        raise RuntimeError('Install prefix is not the candidate.')
    if (ROOT/'server/etc').is_symlink() or (ROOT/'build/manifest.json').exists():
        raise RuntimeError('Already staged; inspect before any repetition.')
    if not (FIXTURE/'etc').is_dir():
        raise RuntimeError('Prepare fixture configuration directory first.')
    private_network()
    run(['cmake','--install',ROOT/'build','--strip'])
    world=ROOT/'server/bin/worldserver'
    shutil.copy2(world,ROOT/'build/worldserver')
    if digest(world)!=digest(ROOT/'build/worldserver'):
        raise RuntimeError('Staged test binary checksum mismatch.')
    (ROOT/'server/etc').rename(ROOT/'server/etc-distribution')
    (ROOT/'server/etc').symlink_to(FIXTURE/'etc',target_is_directory=True)
    components={}
    for name,repo in [('core',ROOT/'core'),('playerbots',ROOT/'core/modules/mod-playerbots'),
                       ('dungeon-clear',ROOT/'core/modules/mod-dungeon-clear'),('ah-bot',ROOT/'core/modules/mod-ah-bot')]:
        components[name]=subprocess.check_output(['git','-C',str(repo),'rev-parse','HEAD'],text=True).strip()
    overlay=[]
    for relative in ['Entities/Player/PlayerStorage.cpp','Handlers/CharacterHandler.cpp','Server/WorldSession.cpp']:
        overlay.append({'relative':relative,'overlaySha256':digest(ROOT/'core/src/server/game'/relative)})
    manifest={'releaseCandidate':True,'validationState':'awaiting isolated runtime checks',
        'worldserverSha256':digest(world),'authserverSha256':digest(ROOT/'server/bin/authserver'),
        'configurationDirectory':str(ROOT/'server/etc'),'coreDirectory':str(ROOT/'core'),
        'configurationMode':'isolated fixture symlink','components':components,'nativeCoreOverlay':overlay,
        'productionActivationPerformed':False}
    (ROOT/'build/manifest.json').write_text(json.dumps(manifest,indent=2))
    print('PASS installed into candidate and bound to synthetic fixture only',flush=True)

def cpp():
    private_network()
    spec=importlib.util.spec_from_file_location('prior_cpp',ROOT/'inputs/run_cpp_suites_20260912.py')
    module=importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.ROOT=ROOT
    import sys
    sys.argv=[sys.argv[0]]
    module.main()

def after_build():
    private_network()
    deadline=time.monotonic()+5400
    while not (ROOT/'evidence/build-success.json').exists():
        state=subprocess.check_output(['systemctl','show','atlas-update-build-20260926','-p','ActiveState','--value'],text=True).strip()
        if state=='failed' or time.monotonic()>deadline:
            raise RuntimeError('Main compilation failed or exceeded the preparation window.')
        time.sleep(15)
    stage()
    cpp()

def dotnet_env():
    return {**os.environ,'HOME':str(ROOT/'tools/hermes-home'),'DOTNET_CLI_HOME':str(ROOT/'tools/hermes-home'),
            'DOTNET_ROOT':str(DOTNET.parent),'PATH':str(DOTNET.parent)+':'+os.environ.get('PATH','/usr/bin:/bin'),
            'NUGET_PACKAGES':str(ROOT/'tools/nuget-packages'),'DOTNET_CLI_TELEMETRY_OPTOUT':'1','DOTNET_NOLOGO':'1'}

def hermes_tests():
    private_network()
    if not json.loads((ROOT/'evidence/hermes-build-success.json').read_text()).get('passed'):
        raise RuntimeError('Never run stale tests following a failed build.')
    env=dotnet_env()
    output=ROOT/'evidence/hermes-tests'
    output.mkdir(exist_ok=True)
    if (output/'runner.log').exists():
        raise RuntimeError('Preserve previous test evidence before rerunning.')
    results=[]
    for label in ['default','wotlk-343']:
        result_dir=output/label
        result_dir.mkdir(exist_ok=False)
        command=[DOTNET,'test','HermesProxy.Tests/HermesProxy.Tests.csproj','-c','Release','--no-build',
                 '--logger','trx','--results-directory',result_dir]
        current_env=dict(env)
        if label=='wotlk-343':
            current_env['HERMES_TEST_MODERN_BUILD']='3.4.3'
            # All Atlas-specific cases plus the shared login/IO and compatibility regressions.
            names=['Atlas','PlayerIdentity','DFProposalResponsePkt','DeathKnightCreationPolicy',
                   'TalentPreviewTranslation','ItemSparseStatWidth','TransportPathRotation343',
                   'Addon','RealmAddress','BnetServer','Framework']
            command+=['--filter','|'.join('FullyQualifiedName~'+name for name in names)]
            current_env['HERMES_NATIVE_PACKET_OUTPUT']=str(result_dir/'native-packets.json')
            current_env['HERMES_IDENTITY_PACKET_OUTPUT']=str(result_dir/'identity-packets.json')
        with (result_dir/'runner.log').open('w') as log:
            result=subprocess.run([str(x) for x in command],cwd=ROOT/'hermes',env=current_env,
                                  stdout=log,stderr=subprocess.STDOUT,timeout=600)
        results.append({'suite':label,'exit':result.returncode})
        print('HERMES TEST',label,'exit',result.returncode,flush=True)
    (output/'runner.log').write_text(json.dumps(results,indent=2))
    if any(row['exit'] for row in results):
        raise RuntimeError('One or more Hermes test suites failed; preserve reports.')
    (output/'success.json').write_text(json.dumps({'passed':True,'completedAt':datetime.datetime.now(datetime.timezone.utc).isoformat()}))

def hermes_publish():
    output=ROOT/'hermes/publish'
    if output.exists() or (FIXTURE/'hermes-all-update').exists():
        raise RuntimeError('Refuse overwriting an existing test publication.')
    env=dotnet_env()
    with (ROOT/'evidence/hermes-publish.log').open('w') as log:
        run([DOTNET,'publish','HermesProxy/HermesProxy.csproj','-c','Release','-r','linux-x64',
             '--self-contained','true','-p:UsePublishBuildSettings=true','-p:UseSharedCompilation=false',
             '-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true',
             '-o',output,'-m:2'],cwd=ROOT/'hermes',env=env,stdout=log,stderr=subprocess.STDOUT)
    files={str(p.relative_to(output)):digest(p) for p in output.rglob('*') if p.is_file()}
    source_commit=subprocess.check_output(['git','-C',str(ROOT/'hermes'),'rev-parse','HEAD'],text=True).strip()
    (ROOT/'hermes/package-manifest.json').write_text(json.dumps({'files':files,'format':'linux-x64 self-contained trimmed single-file (.NET runtime, not NativeAOT)',
        'candidateSourceCommit':source_commit},indent=2))
    shutil.copytree(output,FIXTURE/'hermes-all-update')
    print('PASS Hermes published to isolated fixture only:',len(files),'files',flush=True)

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('phase',choices=['stage','cpp','after-build','hermes-tests','hermes-publish'])
    globals()[p.parse_args().phase.replace('-','_')]()
