"""Migrate only the reviewed test archive grouping; preserve all 248 objects."""
import argparse
from datetime import datetime, timezone
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

if not sys.dont_write_bytecode:
    raise RuntimeError('Invoke with python3 -B; imports must not create bytecode caches.')

ROOT = Path('/opt/arthas-next/candidates/modules-update-20260909T1035Z')
OLD_SHA = '2b2e6e5bf46ccfc7f699eb05d4abc0322f3517f56569c9b32be36fda5261f82e'
SHARED_SHA = 'a6d4e8169777bf1b5dd0aa0e675d78038e4e89d6171da44e4bce5f99b7a442d7'
if hashlib.sha256(Path(__file__).with_name('migrate_world_linux_scheduler.py').read_bytes()).hexdigest() != SHARED_SHA:
    raise RuntimeError('Shared validation helper digest mismatch before import.')
import migrate_world_linux_scheduler as shared


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--new-builder-sha256', required=True)
    parser.add_argument('--expected-state-sha256', required=True)
    parser.add_argument('--apply-reviewed-migration', action='store_true')
    args = parser.parse_args()
    if shared.sha(Path(shared.__file__)) != SHARED_SHA:
        raise RuntimeError('Shared read-only validation helpers differ from reviewed SHA.')
    if ROOT.resolve() != ROOT or any(path.is_symlink() for path in [ROOT, *ROOT.parents]):
        raise RuntimeError('Unexpected candidate root/symlink ancestry.')
    old_path, new_path = ROOT / 'build_world_linux.py', ROOT / 'build_world_linux.grouped.py'
    out = ROOT / 'build-isolated'
    state_path = out / 'state.json'
    for path in [old_path, new_path, out, state_path, out / 'build.lock']:
        if path.is_symlink() or not path.resolve().is_relative_to(ROOT):
            raise RuntimeError('Unsafe migration path: ' + str(path))
    with (out / 'build.lock').open('r+') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if shared.sha(old_path) != OLD_SHA or shared.sha(new_path) != args.new_builder_sha256:
            raise RuntimeError('Unexpected old/new builder digest.')
        if shared.sha(state_path) != args.expected_state_sha256:
            raise RuntimeError('State changed after the reviewed test-link failure.')
        for phase in ['compile', 'link', 'tests']:
            pid = subprocess.check_output(['systemctl', 'show', 'atlas-dc-' + phase + '-20260909-r2.service',
                '-p', 'MainPID', '--value'], text=True).strip()
            if pid not in ['0', '']:
                raise RuntimeError('A native build unit remains active.')
        state = json.loads(state_path.read_text())
        if state.get('builderSha256') != OLD_SHA or state.get('root') != str(ROOT):
            raise RuntimeError('State belongs to another builder/root.')
        if state['manifestSha256'] != shared.sha(ROOT / 'candidate-manifest.json') or state['captureManifestSha256'] != shared.sha(ROOT / 'capture-manifest.json'):
            raise RuntimeError('A source manifest changed.')
        old, new = shared.load_module('pre_group_builder', old_path), shared.load_module('grouped_builder', new_path)
        old_builder, new_builder = old.Builder(ROOT, 'tests'), new.Builder(ROOT, 'tests')
        new_builder.state = state
        new_builder.verify_sources()
        new_builder.verify_inputs()
        if new_builder.auxiliary_hashes() != state['auxiliaryInputSha256']:
            raise RuntimeError('Recipes/compiler/generated headers changed.')
        for name in ['active_command', 'test_command', 'chat_objects', 'old_members', 'dc_sources', 'dc_objects', 'tests', 'test_objects', 'env']:
            if getattr(old_builder, name) != getattr(new_builder, name):
                raise RuntimeError('Non-grouping input changed: ' + name)
        before, after = shared.commands(old, old_builder), shared.commands(new, new_builder)
        if before != after or len(after) != 248 or len(state['objects']) != 248:
            raise RuntimeError('Expected exactly 248 unchanged compile commands and completed objects.')
        for name, record in state['objects'].items():
            path = new_builder.safe(out / name)
            expected = after.get(name)
            if (expected is None or record['commandSha256'] != expected['commandSha256']
                    or record['sourceSha256'] != expected['sourceSha256'] or shared.sha(path) != record['sha256']
                    or path.stat().st_size != record['size']):
                raise RuntimeError('Existing object cannot be reused: ' + name)
        for phase in ['baseline', 'compile', 'link']:
            new_builder.require_phase(phase)
        arguments = (old_builder.test_command, old_builder.test_objects, old_builder.dc_objects,
            old_builder.chat_objects, out / 'dungeon_clear_tests', out / 'dungeon_clear_tests.link.map')
        old_command = old.tests_link_command(*arguments)
        new_command = new.tests_link_command(*arguments)
        expected_command = old_command.copy()
        game = str(old.BASE_BUILD / 'src/server/game/libgame.a')
        scripts = str(old.BASE_BUILD / 'src/server/scripts/libscripts.a')
        if expected_command.count(game) != 1 or expected_command.count(scripts) != 1:
            raise RuntimeError('Unexpected game/scripts archive multiplicity.')
        expected_command.insert(expected_command.index(game), '-Wl,--start-group')
        expected_command.insert(expected_command.index(scripts) + 1, '-Wl,--end-group')
        if new_command != expected_command:
            raise RuntimeError('Test linker argv differs by more than the two reviewed group wrappers.')
        old_world = old.replace_link_outputs(old_builder.active_command, out / 'worldserver-candidate', out / 'worldserver-candidate.link.map')
        new_world = new.replace_link_outputs(new_builder.active_command, out / 'worldserver-candidate', out / 'worldserver-candidate.link.map')
        if old_world != new_world:
            raise RuntimeError('World linker output handling changed.')
        stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
        audit = {'atUtc': stamp, 'purpose': 'Test linker archive rescan grouping only; no C++ or compile flag changes',
            'oldBuilderSha256': OLD_SHA, 'newBuilderSha256': args.new_builder_sha256,
            'oldStateSha256': args.expected_state_sha256, 'applyRequested': args.apply_reviewed_migration,
            'compileCommandsUnchanged': 248, 'objectsIndependentlyHashVerified': 248,
            'testLinkerOnlyAddsTwoGroupWrappers': True, 'groupFirstArchive': game, 'groupLastArchive': scripts,
            'worldLinkerUnchanged': True, 'sourceManifestsRecipesCompilerHeadersUnchanged': True,
            'priorBaselineCompileWorldLinkPassedAndHashVerified': True, 'compilationStarted': False,
            'productionWrites': False, 'previousFailure': state.get('lastFailure')}
        print(json.dumps(audit, indent=2), flush=True)
        if not args.apply_reviewed_migration:
            return
        old_backup = out / ('builder-before-test-group-' + stamp + '.py')
        state_backup = out / ('state-before-test-group-' + stamp + '.json')
        audit_path = out / ('test-link-migration-' + stamp + '.json')
        if any(path.exists() for path in [old_backup, state_backup, audit_path]):
            raise RuntimeError('Refusing to replace existing audit/backup.')
        shutil.copy2(old_path, old_backup)
        shutil.copy2(state_path, state_backup)
        if shared.sha(old_backup) != OLD_SHA or shared.sha(state_backup) != args.expected_state_sha256:
            raise RuntimeError('Backup verification failed; originals unchanged.')
        state['builderSha256'] = args.new_builder_sha256
        state.setdefault('testLinkMigrations', []).append(audit)
        staged_state = out / ('state-after-test-group-' + stamp + '.json')
        shared.exclusive_json(staged_state, state)
        audit['newStateSha256'] = shared.sha(staged_state)
        audit['oldBuilderBackup'], audit['oldStateBackup'] = str(old_backup), str(state_backup)
        shared.exclusive_json(audit_path, audit)
        os.replace(new_path, old_path)
        os.replace(staged_state, state_path)
        old_path.chmod(0o600)
        state_path.chmod(0o600)
        shared.exclusive_json(out / ('test-link-migration-complete-' + stamp + '.json'),
            {'applied': True, 'auditRecord': str(audit_path), 'builderSha256': shared.sha(old_path), 'stateSha256': shared.sha(state_path)})
        print('APPLIED', str(audit_path), flush=True)


if __name__ == '__main__':
    main()
