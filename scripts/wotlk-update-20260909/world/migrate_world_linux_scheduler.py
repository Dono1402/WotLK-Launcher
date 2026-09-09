"""Explicit, hash-verified migration of a stopped builder to parallel scheduling.

No compilation, old-build writes, service changes, source edits or DB access.
The only writes are a backup/audit record and the reviewed builder/state pair
inside the exact new candidate. A failed preflight writes nothing.
"""
import argparse
from datetime import datetime, timezone
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path('/opt/arthas-next/candidates/modules-update-20260909T1035Z')
OLD_SHA = 'f7bdcfa6663c2b79bf1cd78860d20207250cc2f5cdef8ac07549b1ca18ea5a57'


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def commands(module, builder):
    result = {}
    groups = [(builder.dc_sources, builder.dc_objects, 'module-flags.make'),
        (builder.tests, builder.test_objects, 'tests-flags.make')]
    for sources, objects, recipe in groups:
        flags = module.remap_flags(module.parse_flags((ROOT / 'evidence' / recipe).read_text()), builder.core, builder.out)
        for source, obj in zip(sources, objects):
            command = ['/usr/bin/c++', *flags, '-MMD', '-MF', str(obj) + '.d', '-MT', str(obj),
                '-c', str(source), '-o', str(obj)]
            result[obj.relative_to(builder.out).as_posix()] = {
                'commandSha256': hashlib.sha256(json.dumps(command).encode()).hexdigest(),
                'sourceSha256': sha(source)}
    return result


def exclusive_json(path, value):
    with path.open('x') as stream:
        json.dump(value, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--new-builder-sha256', required=True)
    parser.add_argument('--expected-state-sha256', required=True)
    parser.add_argument('--apply-reviewed-migration', action='store_true')
    args = parser.parse_args()
    if ROOT.resolve() != ROOT or any(path.is_symlink() for path in [ROOT, *ROOT.parents]):
        raise RuntimeError('Unexpected candidate root or symlink ancestry.')
    old_path = ROOT / 'build_world_linux.py'
    new_path = ROOT / 'build_world_linux.parallel.py'
    out = ROOT / 'build-isolated'
    state_path = out / 'state.json'
    for path in [old_path, new_path, out, state_path, out / 'build.lock']:
        if path.is_symlink() or not path.resolve().is_relative_to(ROOT):
            raise RuntimeError('Unsafe migration path: ' + str(path))
    with (out / 'build.lock').open('r+') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if sha(old_path) != OLD_SHA or sha(new_path) != args.new_builder_sha256:
            raise RuntimeError('Builder digest differs from the explicitly reviewed pair.')
        if sha(state_path) != args.expected_state_sha256:
            raise RuntimeError('State changed since the reviewed interruption.')
        for phase in ['baseline', 'compile', 'link', 'tests']:
            main_pid = subprocess.check_output(['systemctl', 'show', 'atlas-dc-' + phase + '-20260909.service',
                '-p', 'MainPID', '--value'], text=True).strip()
            if main_pid not in ['0', '']:
                raise RuntimeError('A build unit remains running: ' + phase)
        state = json.loads(state_path.read_text())
        if state.get('builderSha256') != OLD_SHA or state.get('root') != str(ROOT):
            raise RuntimeError('State does not belong to the reviewed old builder/root.')
        if state['manifestSha256'] != sha(ROOT / 'candidate-manifest.json') or state['captureManifestSha256'] != sha(ROOT / 'capture-manifest.json'):
            raise RuntimeError('A frozen input manifest changed.')
        old = load_module('old_reviewed_world_builder', old_path)
        new = load_module('new_reviewed_world_builder', new_path)
        old_builder = old.Builder(ROOT, 'compile')
        new_builder = new.Builder(ROOT, 'compile')
        new_builder.state = state
        new_builder.verify_sources()
        new_builder.verify_inputs()
        if new_builder.auxiliary_hashes() != state['auxiliaryInputSha256']:
            raise RuntimeError('Recipes, compiler or generated headers changed.')
        for name in ['active_command', 'test_command', 'chat_objects', 'old_members', 'dc_sources', 'dc_objects', 'tests', 'test_objects', 'env']:
            if getattr(old_builder, name) != getattr(new_builder, name):
                raise RuntimeError('Non-scheduling builder input changed: ' + name)
        before, after = commands(old, old_builder), commands(new, new_builder)
        if before != after or len(after) != 248:
            raise RuntimeError('A GCC compile command/source identity changed or inventory differs.')
        for name, record in state['objects'].items():
            path = new_builder.safe(out / name)
            expected = after.get(name)
            if (expected is None or record['commandSha256'] != expected['commandSha256']
                    or record['sourceSha256'] != expected['sourceSha256'] or sha(path) != record['sha256']
                    or path.stat().st_size != record['size']):
                raise RuntimeError('An existing compiled object cannot be reused: ' + name)
        for phase, record in state['phases'].items():
            if record.get('passed'):
                new_builder.require_phase(phase)
        timestamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
        audit = {'atUtc': timestamp, 'purpose': 'Scheduling-only resume migration after explicit user acceleration request',
            'oldBuilderSha256': OLD_SHA, 'newBuilderSha256': args.new_builder_sha256,
            'oldStateSha256': args.expected_state_sha256, 'candidateManifestSha256': state['manifestSha256'],
            'captureManifestSha256': state['captureManifestSha256'], 'compileCommandsComparedUnchanged': len(after),
            'existingObjectsIndependentlyHashVerified': len(state['objects']), 'priorPhasesPreserved': state['phases'],
            'sourcesRecipesCompilerGeneratedHeadersUnchanged': True, 'productionWrites': False,
            'compilationStartedByMigration': False, 'applyRequested': args.apply_reviewed_migration}
        print(json.dumps(audit, indent=2), flush=True)
        if not args.apply_reviewed_migration:
            return
        old_backup = out / ('builder-before-parallel-' + timestamp + '.py')
        state_backup = out / ('state-before-parallel-' + timestamp + '.json')
        audit_path = out / ('scheduler-migration-' + timestamp + '.json')
        if any(path.exists() for path in [old_backup, state_backup, audit_path]):
            raise RuntimeError('Refusing to overwrite migration evidence.')
        shutil.copy2(old_path, old_backup)
        shutil.copy2(state_path, state_backup)
        if sha(old_backup) != OLD_SHA or sha(state_backup) != args.expected_state_sha256:
            raise RuntimeError('Migration backup did not verify; original builder/state untouched.')
        state['builderSha256'] = args.new_builder_sha256
        state.setdefault('schedulerMigrations', []).append({key: value for key, value in audit.items() if key != 'priorPhasesPreserved'})
        new_state = out / ('state-after-parallel-' + timestamp + '.json')
        exclusive_json(new_state, state)
        audit['newStateSha256'] = sha(new_state)
        audit['oldBuilderBackup'] = str(old_backup)
        audit['oldStateBackup'] = str(state_backup)
        exclusive_json(audit_path, audit)
        # A crash between these two replacements fails closed on identity.
        # Both originals have already been copied and SHA256-verified above.
        os.replace(new_path, old_path)
        os.replace(new_state, state_path)
        old_path.chmod(0o600)
        state_path.chmod(0o600)
        exclusive_json(out / ('scheduler-migration-complete-' + timestamp + '.json'),
            {'applied': True, 'auditRecord': str(audit_path), 'builderSha256': sha(old_path), 'stateSha256': sha(state_path)})
        print('APPLIED', str(audit_path), flush=True)


if __name__ == '__main__':
    main()
