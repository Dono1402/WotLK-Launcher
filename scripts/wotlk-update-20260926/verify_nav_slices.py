#!/usr/bin/env python3
"""Materialize private nav fixtures and run the previously skipped geometry case."""
import json
import os
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

ROOT=Path('/opt/arthas-next/candidates/atlas-all-update-20260926')
MODULE=ROOT/'core/modules/mod-dungeon-clear'
DATA=Path('/opt/arthas-next/candidates/dungeon-clear-8224099-20260903T062903Z/server/data')

if __name__=='__main__':
    if os.readlink('/proc/self/ns/net')==os.readlink('/proc/1/ns/net'):
        raise RuntimeError('Private test network required.')
    target=MODULE/'t/fixtures/mapdata'
    if (target/'mmaps').exists() or target.is_symlink() or MODULE.resolve(strict=True)!=MODULE:
        raise RuntimeError('Refuse overwriting private navigation fixtures.')
    scenarios=[]
    for path in (MODULE/'t/fixtures/nav').glob('*.json'):
        for line in path.read_text().splitlines():
            if line.lstrip().startswith('{'):
                scenarios.append(json.loads(line))
    maps=sorted({row['map'] for row in scenarios})
    if not maps or any(not isinstance(m,int) or m<=0 for m in maps):
        raise RuntimeError('Unexpected map scenarios.')
    output=ROOT/'evidence/nav-slices'
    output.mkdir(exist_ok=False)
    with (output/'slicing.log').open('w') as log:
        for map_id in maps:
            subprocess.run(['python3',str(MODULE/'tools/slice_mapdata.py'),
                '--datadir',str(DATA),'--map',str(map_id),'--out',str(target)],
                check=True,stdout=log,stderr=subprocess.STDOUT,timeout=60)
    report=output/'geometry.xml'
    with (output/'geometry.log').open('w') as log:
        result=subprocess.run([str(ROOT/'build/dungeon_clear_tests'),
            '--gtest_filter=DcNavGeometry.ScenariosRouteAsExpected','--gtest_output=xml:'+str(report)],
            cwd=ROOT/'build',stdout=log,stderr=subprocess.STDOUT,timeout=120)
    cases=ET.parse(report).getroot().findall('.//testcase')
    passed=result.returncode==0 and len(cases)==1 and not any(
        c.find('failure') is not None or c.find('skipped') is not None for c in cases)
    (output/'summary.json').write_text(json.dumps({'passed':passed,'exit':result.returncode,
        'maps':maps,'scenarioCount':len(scenarios),'clientDerivedDataCommitted':False},indent=2))
    print('Navigation slice check:',passed,'maps',maps,'scenarios',len(scenarios),flush=True)
    raise SystemExit(0 if passed else 1)
