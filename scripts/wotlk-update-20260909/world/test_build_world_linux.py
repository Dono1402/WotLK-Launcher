"""Portable builder contract tests. Never compiles, connects, or runs a world."""
import json
import os
from pathlib import Path, PurePosixPath
import shlex
import tempfile
import unittest
from unittest.mock import patch

import build_world_linux as b


ROOT = Path(os.environ.get('ATLAS_WORLD_PREPARATION_ROOT', Path(__file__).resolve().parent)).resolve()


class BuilderContracts(unittest.TestCase):
    def test_plan_pinned_full_sources(self):
        result = b.plan(ROOT)
        self.assertEqual((result['dcTranslationUnits'], result['fullTestTranslationUnits']), (173, 75))
        self.assertEqual(len(result['preservedChatObjects']), 3)
        self.assertFalse(result['worldserverWillRun'])

    def test_flags_reject_output(self):
        with self.assertRaises(RuntimeError):
            b.parse_flags('CXX_DEFINES =\nCXX_INCLUDES =\nCXX_FLAGS = -o\n')

    def test_flags_remap(self):
        result = b.remap_flags(['-I' + str(b.BASE_CORE / 'src'), '-D_CONF_DIR="/prod"', '-O2'],
            PurePosixPath('/candidate/core'), PurePosixPath('/candidate/output'))
        self.assertEqual(result, ['-I/candidate/core/src', '-D_CONF_DIR="/candidate/output/no-runtime-config"', '-O2'])

    def test_all_test_objects_replaced_in_link_order(self):
        original = shlex.split((ROOT / 'evidence/tests-link.txt').read_text())
        tests = [Path('/new/t' + str(i) + '.o') for i in range(75)]
        dc = [Path('/new/d' + str(i) + '.o') for i in range(173)]
        chat = [Path('/chat/c' + str(i) + '.o') for i in range(3)]
        command = b.tests_link_command(original, tests, dc, chat, Path('/new/tests'), Path('/new/tests.map'))
        self.assertFalse(any('CMakeFiles/dungeon_clear_tests.dir' in item for item in command))
        self.assertEqual(command.count(str(b.ACTIVE_ARCHIVE)), 1)
        first_library = min(i for i, item in enumerate(command) if item.endswith('.a'))
        for item in [*tests, *dc, *chat]:
            self.assertEqual(command.count(str(item)), 1)
            self.assertLess(command.index(str(item)), first_library)

    def test_outputs_redirected(self):
        result = b.replace_link_outputs(['/usr/bin/c++', '-Wl,--dependency-file=/old/link.d', '/old/a.o', '-o', '/old/bin'],
            PurePosixPath('/new/bin'), PurePosixPath('/new/link.map'))
        self.assertIn('-Wl,--dependency-file=/new/bin.link.d', result)
        self.assertIn('-Wl,-Map=/new/link.map', result)
        self.assertIn('-Wl,--no-keep-memory', result)
        self.assertEqual(result[result.index('-o') + 1], '/new/bin')

    def test_unknown_link_output_rejected(self):
        with self.assertRaises(RuntimeError):
            b.replace_link_outputs(['c++', '-Wl,-Map,/old/map', '-o', 'x'], Path('/new/bin'), Path('/new/map'))

    def test_stale_archive_map(self):
        text = str(b.ACTIVE_ARCHIVE) + '(DcDungeon.cpp.o)\n' + str(b.ACTIVE_ARCHIVE) + '(Other.cpp.o)\n'
        self.assertEqual(b.stale_members(text, {'DcDungeon.cpp.o'}), ['DcDungeon.cpp.o'])
        self.assertEqual(b.stale_members('/other/lib.a(DcDungeon.cpp.o)', {'DcDungeon.cpp.o'}), [])

    def test_source_seal(self):
        builder = object.__new__(b.Builder)
        builder.root, builder.core = ROOT, ROOT / 'candidate/core'
        builder.capture = json.loads((ROOT / 'capture-manifest.json').read_text())
        builder.manifest = json.loads((ROOT / 'candidate-manifest.json').read_text())
        builder.verify_sources()

    def test_postcheck_failure_cannot_retain_pass(self):
        builder = object.__new__(b.Builder)
        builder.phase = 'tests'
        builder.jobs = 1
        builder.check_cancelled = lambda: None
        builder.state = {'phases': {'baseline': {'passed': True}, 'compile': {'passed': True},
            'link': {'passed': True}}, 'outputs': {}}
        builder.initialise = builder.save = builder.stop_child = lambda: None
        builder.verify_sources = lambda: (_ for _ in ()).throw(RuntimeError('synthetic postcheck'))
        builder.tests_phase = lambda: builder.state['phases'].update(tests={'passed': True})
        with tempfile.TemporaryDirectory(dir=ROOT / 'validation') as folder, patch.object(b.signal, 'signal'):
            builder.out, builder.root = Path(folder) / 'out', Path(folder)
            with self.assertRaisesRegex(RuntimeError, 'synthetic postcheck'):
                builder.execute()
        self.assertFalse(builder.state['phases']['tests']['passed'])
        self.assertEqual(builder.state['lastFailure']['phase'], 'tests')

    def test_sealed_output_changed_rejected(self):
        builder = object.__new__(b.Builder)
        builder.state = {'phases': {'compile': {'passed': True, 'outputKeys': ['missing.o']}},
            'outputs': {'missing.o': {'sha256': 'not-a-hash'}}}
        with tempfile.TemporaryDirectory(dir=ROOT / 'validation') as folder:
            builder.out, builder.root = Path(folder) / 'out', Path(folder)
            with self.assertRaisesRegex(RuntimeError, 'Sealed phase output changed'):
                builder.require_phase('compile')


if __name__ == '__main__':
    unittest.main(verbosity=2)
