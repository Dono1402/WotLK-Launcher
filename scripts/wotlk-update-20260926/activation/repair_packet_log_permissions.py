#!/usr/bin/env python3
"""Repair the missing September 26 Hermes runtime directory, without restart/config changes."""
import importlib.util
import os
from pathlib import Path
import stat
import subprocess

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260926')


def main():
    if os.geteuid() != 0:
        raise RuntimeError('Root required for the narrowly scoped ownership repair.')
    spec = importlib.util.spec_from_file_location('deployment', ROOT / 'inputs/deploy-20260926.py')
    d = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(d)
    d.prepared()
    if (d.DEPLOY / 'packet-log-permissions-repair.json').exists():
        raise RuntimeError('Preserve the previous repair report.')
    before = {unit: d.show(unit) for unit in d.UNITS}
    for unit, state in before.items():
        if state['ActiveState'] != 'active' or state['MainPID'] == '0':
            raise RuntimeError('Unexpected inactive service: ' + unit)
    pid = before[d.PROXY]['MainPID']
    if Path('/proc/' + pid + '/cwd').resolve(strict=True) != d.HERMES:
        raise RuntimeError('Hermes working directory changed.')
    if d.digest('/proc/' + pid + '/exe') != d.EXPECTED['HermesProxy']:
        raise RuntimeError('Hermes executable changed.')
    release_before = d.HERMES.stat()
    directory = d.prepare_hermes_runtime_directory(d.HERMES, 'PacketsLog')
    # Anonymous temporary file: exercise create/write/flush as the real service user,
    # without creating a fake packet capture or reading any actual player packet data.
    probe = 'import os,tempfile; f=tempfile.TemporaryFile(dir=' + repr(str(directory)) + '); f.write(b"atlas-permission-check"); f.flush(); os.fsync(f.fileno()); f.close()'
    subprocess.run(['runuser', '-u', 'hermesproxy', '--', 'python3', '-c', probe], check=True, timeout=10)
    d.prepared()
    after = {unit: d.show(unit) for unit in d.UNITS}
    for unit, state in before.items():
        for key in ('ActiveState', 'MainPID', 'NRestarts'):
            if after[unit][key] != state[key]:
                raise RuntimeError('Service changed during repair: ' + unit)
    release_after = d.HERMES.stat()
    if (release_before.st_mode, release_before.st_uid, release_before.st_gid) != (release_after.st_mode, release_after.st_uid, release_after.st_gid):
        raise RuntimeError('Immutable release permissions changed.')
    result = {'at': d.now(), 'directory': str(directory), 'mode': oct(stat.S_IMODE(directory.stat().st_mode)),
        'owner': 'hermesproxy', 'writeAndFsyncAsServiceUserPassed': True,
        'allServicePidsAndRestartCountsUnchanged': True, 'configurationFilesUnchanged': True,
        'binaryUnchanged': True, 'restartPerformed': False, 'clientReconnectValidated': False,
        'reason': 'First real client login reached packet capture creation, which lacked a writable runtime directory.'}
    d.save('packet-log-permissions-repair.json', result)
    print('PASS: PacketsLog created; service-user write/fsync works; all services and configuration unchanged.')


if __name__ == '__main__':
    main()
