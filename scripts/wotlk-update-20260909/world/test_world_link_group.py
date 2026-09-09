#!/usr/bin/env python3
"""Test-link-only regression checks; no compiler, linker, or world is executed."""
import ast
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import shlex
import sys
import threading
import unittest

sys.dont_write_bytecode = True
CODE_ROOT = Path(__file__).resolve().parent
ROOT = Path(os.environ.get('ATLAS_WORLD_PREPARATION_ROOT', CODE_ROOT)).resolve()
OLD_PATH = CODE_ROOT / 'fixtures/build_world_linux.parallel-sealed.py'
if not OLD_PATH.is_file():
    OLD_PATH = CODE_ROOT / 'build_world_linux.parallel-sealed.py'
OLD_SHA256 = '2b2e6e5bf46ccfc7f699eb05d4abc0322f3517f56569c9b32be36fda5261f82e'


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


OLD = load('sealed_parallel_builder', OLD_PATH)
NEW = load('test_link_group_builder', CODE_ROOT / 'build_world_linux.py')


class CapturedCommand(Exception):
    pass


class NoWritePath(PurePosixPath):
    """Keep real Linux argv spelling without creating a compiler output directory."""
    def mkdir(self, *args, **kwargs):
        pass


def inventory(module):
    local_core = ROOT / 'candidate/core'
    core = NoWritePath(str(module.EXPECTED_ROOT / 'candidate/core'))
    out = NoWritePath(str(module.EXPECTED_ROOT / 'build-isolated'))
    local_dc = sorted((local_core / 'modules/mod-dungeon-clear/src').rglob('*.cpp'))
    local_tests = module.test_sources(local_core)
    dc_sources = [core / path.relative_to(local_core).as_posix() for path in local_dc]
    tests = [core / path.relative_to(local_core).as_posix() for path in local_tests]
    dc_objects = [out / 'objects/dc' / (path.relative_to(core / 'modules/mod-dungeon-clear').as_posix() + '.o')
        for path in dc_sources]
    test_objects = [out / 'objects/tests' / (path.relative_to(core).as_posix() + '.o') for path in tests]
    chats = [NoWritePath(path) for path in module.plan(ROOT)['preservedChatObjects']]
    return core, out, dc_sources, dc_objects, tests, test_objects, chats


def capture_builder(module, commands):
    builder = object.__new__(module.Builder)
    builder.safe = lambda path: path
    builder.check_cancelled = lambda: None
    builder.require_phase = lambda phase: None
    builder.state = {'objects': {}}
    builder._state_lock = threading.RLock()

    def run(name, command, **kwargs):
        commands.append(command.copy())
        raise CapturedCommand()

    builder.run = run
    return builder


class TestLinkGroupContracts(unittest.TestCase):
    def test_sealed_parallel_fixture_and_only_test_recipe_changed(self):
        self.assertEqual(OLD.sha(OLD_PATH), OLD_SHA256)
        trees = []
        for path in [OLD_PATH, CODE_ROOT / 'build_world_linux.py']:
            tree = ast.parse(path.read_text())
            removed = [node for node in tree.body if isinstance(node, ast.FunctionDef)
                and node.name == 'tests_link_command']
            self.assertEqual(len(removed), 1)
            tree.body.remove(removed[0])
            trees.append(ast.dump(tree, include_attributes=False))
        self.assertEqual(*trees, 'Code outside tests_link_command must remain identical.')

    def test_actual_test_argv_changes_by_exactly_two_group_tokens(self):
        original = shlex.split((ROOT / 'evidence/tests-link.txt').read_text())
        _, out, _, dc, _, tests, chats = inventory(NEW)
        self.assertEqual((len(tests), len(dc), len(chats)), (75, 173, 3))
        before = OLD.tests_link_command(original, tests, dc, chats, out / 'dungeon_clear_tests', out / 'dungeon_clear_tests.link.map')
        after = NEW.tests_link_command(original, tests, dc, chats, out / 'dungeon_clear_tests', out / 'dungeon_clear_tests.link.map')
        start, end = '-Wl,--start-group', '-Wl,--end-group'
        self.assertEqual(after.count(start), 1)
        self.assertEqual(after.count(end), 1)
        self.assertEqual(len(after), len(before) + 2)
        self.assertEqual([token for token in after if token not in [start, end]], before)
        game = str(NEW.BASE_BUILD / 'src/server/game/libgame.a')
        scripts = str(NEW.BASE_BUILD / 'src/server/scripts/libscripts.a')
        expected = before.copy()
        expected.insert(expected.index(scripts) + 1, end)
        expected.insert(expected.index(game), start)
        self.assertEqual(after, expected)
        self.assertEqual(after[after.index(start) + 1:after.index(end)], [game,
            str(NEW.BASE_BUILD / 'lib/libgtest_main.a'), str(NEW.BASE_BUILD / 'lib/libgmock_main.a'),
            str(NEW.ACTIVE_ARCHIVE), scripts])
        self.assertEqual(sum(token.endswith('.o') for token in after), 251)
        self.assertFalse(any('CMakeFiles/dungeon_clear_tests.dir' in token for token in after))
        for obj in [*tests, *dc, *chats]:
            self.assertEqual(after.count(str(obj)), 1)
            self.assertLess(after.index(str(obj)), after.index(start))

    def test_unreviewed_archive_counts_order_and_groups_rejected(self):
        original = NEW.absolute_inputs(shlex.split((ROOT / 'evidence/tests-link.txt').read_text()), NEW.BASE_BUILD)
        game = str(NEW.BASE_BUILD / 'src/server/game/libgame.a')
        scripts = str(NEW.BASE_BUILD / 'src/server/scripts/libscripts.a')
        swapped = original.copy()
        first, last = swapped.index(game), swapped.index(scripts)
        swapped[first], swapped[last] = swapped[last], swapped[first]
        cases = [original + [game], original + [scripts], [token for token in original if token != game],
            [token for token in original if token != scripts], swapped]
        cases.extend(original + [token] for token in ['-Wl,--start-group', '-Wl,--end-group', '-Wl,-(', '-Wl,-)'])
        for malformed in cases:
            with self.subTest(command=malformed), self.assertRaises(RuntimeError):
                NEW.tests_link_command(malformed, [], [], [], NoWritePath('/new/test'), NoWritePath('/new/map'))

    def test_all_248_actual_compile_argv_are_unchanged(self):
        captured = []
        for module in [OLD, NEW]:
            commands = []
            builder = capture_builder(module, commands)
            core, builder.out, dc_sources, dc_objects, tests, test_objects, _ = inventory(module)
            for sources, objects, recipe, group in [(dc_sources, dc_objects, 'module-flags.make', 'dc'),
                    (tests, test_objects, 'tests-flags.make', 'tests')]:
                flags = module.remap_flags(module.parse_flags((ROOT / 'evidence' / recipe).read_text()), core, builder.out)
                for source, obj in zip(sources, objects):
                    with self.assertRaises(CapturedCommand):
                        builder.compile_one(source, obj, flags, group)
            self.assertEqual(len(commands), 248)
            captured.append(commands)
        self.assertEqual(*captured)

    def test_baseline_and_world_candidate_link_argv_are_unchanged(self):
        for phase in ['baseline', 'link']:
            captured = []
            for module in [OLD, NEW]:
                commands = []
                builder = capture_builder(module, commands)
                _, builder.out, _, builder.dc_objects, _, _, _ = inventory(module)
                builder.active_command = module.absolute_inputs(
                    json.loads((ROOT / 'evidence/active-link-command.json').read_text()), module.BASE_BUILD / 'src/server/apps')
                with self.assertRaises(CapturedCommand):
                    getattr(builder, phase)()
                self.assertEqual(len(commands), 1)
                captured.append(commands[0])
            self.assertEqual(*captured, phase)


if __name__ == '__main__':
    unittest.main(verbosity=2)
