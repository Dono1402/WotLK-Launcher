#!/usr/bin/env python3
"""Compare the prepared sources and private backups with their recorded baseline."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260912')


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def show(unit, properties):
    return dict(line.split('=', 1) for line in subprocess.check_output(
        ['systemctl', 'show', unit, '--property=' + ','.join(properties)], text=True).splitlines())


def main():
    if ROOT.resolve(strict=True) != ROOT:
        raise RuntimeError('Unexpected candidate path.')
    evidence = ROOT / 'evidence'
    baseline = json.loads((evidence / 'runtime-before.json').read_text())
    report = {'checkedAt': datetime.now(timezone.utc).isoformat(), 'failures': [],
              'services': {}, 'configurations': {}, 'modules': {}, 'sources': {}}

    def check(condition, description):
        if not condition:
            report['failures'].append(description)
        return bool(condition)

    for unit, before in baseline['services'].items():
        actual = show(unit, ['ActiveState', 'MainPID', 'NRestarts', 'FragmentPath'])
        stable = all(actual[key] == before[key] for key in actual)
        if actual['MainPID'] != '0':
            binary = Path('/proc/' + actual['MainPID'] + '/exe')
            stable = stable and str(binary.resolve(strict=True)) == before['executable']
            stable = stable and digest(binary) == before['sha256']
        else:
            stable = False
        stable = stable and digest(Path(before['FragmentPath'])) == before['unitSha256']
        report['services'][unit] = {'unchanged': check(stable, 'Active service changed: ' + unit),
                                    'pid': int(actual['MainPID'])}

    world = baseline['services']['arthas-worldserver.dungeon-clear-8224099']
    arguments = Path('/proc/' + world['MainPID'] + '/cmdline').read_bytes().decode().split('\0')
    configuration = Path(arguments[arguments.index('--config') + 1]).resolve(strict=True).parent
    for name, expected in baseline['configurations'].items():
        live = configuration / name
        backup = ROOT / 'private/production-etc' / name
        report['configurations'][name] = check(digest(live) == expected and digest(backup) == expected,
                                                'World configuration changed: ' + name)

    service_files = json.loads((evidence / 'service-configuration-files.json').read_text())
    checked_files = 0
    for unit, recorded in service_files.items():
        fields = [key for key in ('FragmentPath', 'DropInPaths', 'WorkingDirectory', 'User', 'Group', 'EnvironmentFiles')
                  if key in recorded]
        actual = show(unit, fields)
        check(all(actual[key] == recorded[key] for key in fields), 'Effective service settings changed: ' + unit)
        for row in recorded['preservedFiles']:
            stable = digest(Path(row['path'])) == row['sha256'] and digest(ROOT / row['backupRelative']) == row['sha256']
            check(stable, 'Preserved service file changed: ' + unit + '/' + Path(row['path']).name)
            checked_files += 1
    report['preservedServiceFileCount'] = checked_files

    for name, row in json.loads((evidence / 'preserved-modules.json').read_text()).items():
        source = Path(row['source'])
        candidate = ROOT / 'core/modules' / name
        for relative, expected in row['files'].items():
            check(digest(source / relative) == expected and digest(candidate / relative) == expected,
                  'Preserved module file changed: ' + name + '/' + relative)
        report['modules'][name] = {'comparedFiles': len(row['files'])}

    for name, row in json.loads((evidence / 'source-reproduction.json').read_text()).items():
        repo = ROOT / ('core' if name == 'core' else 'core/modules/mod-playerbots')
        tree = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD^{tree}'], text=True).strip()
        changes = subprocess.check_output(['git', '-C', str(repo), 'status', '--porcelain', '--untracked-files=no'], text=True)
        report['sources'][name] = check(tree == row['tree'] and not changes, 'Reproduced source changed: ' + name)
    report['passed'] = not report['failures']
    (evidence / 'preservation-check.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report), flush=True)
    raise SystemExit(0 if report['passed'] else 1)


if __name__ == '__main__':
    main()
