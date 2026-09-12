#!/usr/bin/env python3
"""Run both complete C++ suites inside a private, resource-limited test unit."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time
import xml.etree.ElementTree as ET

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260912')
MAP_DATA = Path('/opt/arthas-next/candidates/dungeon-clear-8224099-20260903T062903Z/server/data')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--suite', action='append', choices=('core', 'dungeon-clear'))
    args = parser.parse_args()
    if ROOT.resolve(strict=True) != ROOT:
        raise RuntimeError('Unexpected candidate path.')
    if os.readlink('/proc/self/ns/net') == os.readlink('/proc/1/ns/net'):
        raise RuntimeError('The C++ suites require a private test network.')
    if not (MAP_DATA / 'mmaps').is_dir():
        raise RuntimeError('The existing navigation dataset must be available read-only.')
    output = ROOT / 'evidence/cpp-tests'
    output.mkdir(exist_ok=True)
    temporary = ROOT / 'build/test-tmp'
    temporary.mkdir(exist_ok=True)
    rows = []
    for suite, command in [
        ('core', ['/usr/bin/ctest', '--test-dir', str(ROOT / 'build'),
                  '--output-on-failure', '--timeout', '1200']),
        ('dungeon-clear', [str(ROOT / 'build/dungeon_clear_tests')]),
    ]:
        if args.suite and suite not in args.suite:
            continue
        xml_path = output / (suite + '.xml')
        if xml_path.exists():
            raise RuntimeError('Preserve and inspect existing test evidence before rerunning: ' + suite)
        env = os.environ.copy()
        env.update(GTEST_OUTPUT='xml:' + str(xml_path), DC_PROBE_MMAPS=str(MAP_DATA), TMPDIR=str(temporary))
        started = time.time()
        print('START', suite, flush=True)
        with (output / (suite + '.log')).open('w') as log:
            try:
                result = subprocess.run(command, cwd=ROOT / 'build', env=env,
                                        stdout=log, stderr=subprocess.STDOUT, timeout=1230)
                code = result.returncode
            except subprocess.TimeoutExpired:
                code = 124
        row = {'suite': suite, 'exit': code, 'elapsedSeconds': round(time.time() - started, 2)}
        if xml_path.exists():
            root = ET.parse(xml_path).getroot()
            cases = root.findall('.//testcase')
            failures = [case.attrib.get('classname', '') + '.' + case.attrib['name']
                        for case in cases if case.find('failure') is not None or case.find('error') is not None]
            skipped = [case.attrib.get('classname', '') + '.' + case.attrib['name']
                       for case in cases if case.find('skipped') is not None or case.attrib.get('status') == 'notrun']
            row.update(totalTests=len(cases), failedTests=failures, skippedTests=skipped,
                       passedTests=len(cases) - len(failures) - len(skipped))
        else:
            row['missingReport'] = True
        rows.append(row)
        (output / (suite + '.json')).write_text(json.dumps(row, indent=2) + '\n')
        compact = {key: (len(value) if key == 'skippedTests' else value) for key, value in row.items()}
        print(json.dumps(compact), flush=True)
    passed = all(row['exit'] == 0 and not row.get('missingReport') and not row.get('failedTests') for row in rows)
    (output / 'summary.json').write_text(json.dumps({'passed': passed,
        'selectedSuites': [row['suite'] for row in rows], 'suites': rows}, indent=2) + '\n')
    raise SystemExit(0 if passed else 1)


if __name__ == '__main__':
    main()
