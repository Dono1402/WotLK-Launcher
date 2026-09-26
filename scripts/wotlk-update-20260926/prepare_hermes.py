#!/usr/bin/env python3
"""Isolated Hermes build; never touches or starts the deployed release."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260926')
SOURCE = ROOT / 'hermes'
DOTNET = Path('/opt/hermesproxy-candidates/hermes-2e84f0b-custom-20260901/.dotnet/dotnet')
PIN = '5ea8767f0edbd1496511053d7a30136f7963b0a5'

def run(args, **kw):
    return subprocess.run([str(x) for x in args], check=True, **kw)

def prepare():
    if SOURCE.exists():
        raise RuntimeError('Do not overwrite the existing integration.')
    run(['git','clone','--depth=1','https://github.com/Xian55/HermesProxy',SOURCE])
    run(['git','-C',SOURCE,'fetch','--depth=1','origin',PIN])
    run(['git','-C',SOURCE,'switch','--detach',PIN])
    # GitVersion needs the actual history/tags to describe the pinned candidate.
    run(['git','-C',SOURCE,'fetch','--unshallow','--tags','origin'])
    run(['git','-C',SOURCE,'apply','--check',ROOT/'inputs/hermes-atlas.patch'])
    run(['git','-C',SOURCE,'apply',ROOT/'inputs/hermes-atlas.patch'])
    run(['git','-C',SOURCE,'config','user.name','Atlas Integration'])
    run(['git','-C',SOURCE,'config','user.email','atlas-integration@localhost'])
    run(['git','-C',SOURCE,'add','-A'])
    run(['git','-C',SOURCE,'commit','-m','custom(Atlas): Port integration onto September 26 Hermes architecture'])

def build():
    # A previous successful compilation must never authorize tests after this
    # build failed on newer sources.
    (ROOT/'evidence/hermes-build-success.json').unlink(missing_ok=True)
    env = {**os.environ, 'HOME':str(ROOT/'tools/hermes-home'), 'DOTNET_CLI_HOME':str(ROOT/'tools/hermes-home'),
           'PATH':str(DOTNET.parent)+os.pathsep+os.environ.get('PATH','/usr/bin:/bin'),
           'DOTNET_ROOT':str(DOTNET.parent),'DOTNET_CLI_TELEMETRY_OPTOUT':'1',
           'DOTNET_NOLOGO':'1', 'NUGET_PACKAGES':str(ROOT/'tools/nuget-packages')}
    Path(env['HOME']).mkdir(exist_ok=True)
    log_path=ROOT/'evidence/hermes-build.log'
    if log_path.exists():
        log_path.rename(log_path.with_name('hermes-build-'+str(time.time_ns())+'.log'))
    with log_path.open('w') as log:
        run([DOTNET,'build','HermesProxy.Tests/HermesProxy.Tests.csproj','-c','Release','-m:2'],
            cwd=SOURCE, env=env, stdout=log, stderr=subprocess.STDOUT)
    (ROOT/'evidence/hermes-build-success.json').write_text(json.dumps({'passed':True}))

if __name__ == '__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('phase',choices=['prepare','build'])
    globals()[p.parse_args().phase]()
