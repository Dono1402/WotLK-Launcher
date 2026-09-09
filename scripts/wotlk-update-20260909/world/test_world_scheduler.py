#!/usr/bin/env python3
"""Local scheduler checks using fake compiler processes; no Linux/SSH execution."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
CODE_ROOT = Path(__file__).resolve().parent
ROOT = Path(os.environ.get('ATLAS_WORLD_PREPARATION_ROOT', CODE_ROOT)).resolve()


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, CODE_ROOT / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


NEW = load('parallel_builder', 'build_world_linux.py')
OLD = load('sealed_serial_builder', 'fixtures/build_world_linux.serial-sealed.py')


class FakeCompilers:
    def __init__(self, builder, failure=None, delay=0.025):
        self.builder = builder
        self.failure = failure
        self.delay = delay
        self.lock = threading.Lock()
        self.processes = {}
        self.active = 0
        self.peak = 0
        self.late_spawns = 0
        self.commands = []

    def popen(self, command, **kwargs):
        with self.lock:
            if self.builder._stop_requested.is_set():
                self.late_spawns += 1
            self.commands.append(command)
            pid = 900000 + len(self.commands)
            process = FakeProcess(self, pid, command, kwargs['stdout'])
            self.processes[pid] = process
            self.active += 1
            self.peak = max(self.peak, self.active)
        process.start()
        return process

    def killpg(self, pid, signum):
        self.processes[pid].finish(-signum)

    def join(self):
        for process in self.processes.values():
            process.timer.join()


class FakeProcess:
    def __init__(self, owner, pid, command, stream):
        self.owner, self.pid, self.command = owner, pid, command
        # Popen gives the child its own descriptor; closing the parent's stream
        # during exception unwinding must not close the child's log descriptor.
        self.stream = os.fdopen(os.dup(stream.fileno()), 'wb')
        self.returncode = None
        self.done = threading.Event()
        source = command[command.index('-c') + 1] if '-c' in command else ''
        self.failed = Path(source).name == owner.failure
        self.timer = threading.Timer(0.015 if self.failed else owner.delay, self.finish, args=(1 if self.failed else 0,))

    def start(self):
        self.timer.start()

    def finish(self, code):
        with self.owner.lock:
            if self.returncode is not None:
                return
            if code >= 0 and '-o' in self.command:
                path = Path(self.command[self.command.index('-o') + 1])
                path.write_bytes(b'partial failed object' if code else b'valid fake object')
            self.stream.write(('fake compiler exit ' + str(code) + '\n').encode())
            self.stream.flush()
            self.stream.close()
            self.returncode = code
            self.owner.active -= 1
            self.done.set()
            self.timer.cancel()

    def poll(self):
        return self.returncode

    def wait(self, timeout=None):
        if not self.done.wait(timeout):
            raise subprocess.TimeoutExpired(self.command, timeout)
        return self.returncode


class SchedulerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='scheduler-', dir=ROOT / 'validation')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def builder(self, module=NEW, jobs=3):
        builder = module.Builder.__new__(module.Builder)
        builder.root = self.root
        builder.out = self.root / 'build-isolated'
        builder.out.mkdir(exist_ok=True)
        (builder.out / 'logs').mkdir(exist_ok=True)
        builder.jobs = jobs
        builder.env = os.environ.copy()
        builder.state = {'objects': {}, 'outputs': {}, 'phases': {}, 'baselineInputSignatures': {}}
        builder.children = set()
        builder.child = None
        builder._children_lock = threading.RLock()
        builder._termination_lock = threading.RLock()
        builder._state_lock = threading.RLock()
        builder._stop_requested = threading.Event()
        builder._first_failure = None
        builder.disk_guard = lambda: None
        builder.verify_inputs = lambda: None
        builder.save()
        return builder

    def sources(self, count):
        directory = self.root / 'sources'
        directory.mkdir(exist_ok=True)
        sources = [directory / ('unit' + str(index) + '.cpp') for index in range(count)]
        for source in sources:
            source.write_text('int ' + source.stem + ' = 1;\n')
        return sources, [self.root / 'build-isolated/objects' / (source.name + '.o') for source in sources]

    @contextlib.contextmanager
    def compilers(self, builder, **kwargs):
        fake = FakeCompilers(builder, **kwargs)
        with patch.object(NEW.subprocess, 'Popen', side_effect=fake.popen), \
                patch.object(NEW.os, 'killpg', side_effect=fake.killpg, create=True), \
                patch.object(NEW.signal, 'SIGKILL', 9, create=True), \
                contextlib.redirect_stdout(io.StringIO()):
            try:
                yield fake
            finally:
                fake.join()

    def test_parallelism_is_real_and_bounded_and_checkpoints_are_atomic(self):
        builder = self.builder(jobs=3)
        sources, objects = self.sources(7)
        stopped = threading.Event()
        errors = []

        def reader():
            while not stopped.wait(0.002):
                try:
                    state = json.loads((builder.out / 'state.json').read_text())
                    if not isinstance(state['objects'], dict):
                        errors.append('invalid checkpoint')
                except Exception as exc:
                    errors.append(str(exc))

        watcher = threading.Thread(target=reader)
        watcher.start()
        try:
            with self.compilers(builder) as fake:
                builder.compile_many(sources, objects, ['-O2'], 'dc')
                self.assertEqual(fake.peak, 3)
                self.assertEqual(len(fake.commands), 7)
                self.assertEqual(fake.active, 0)
                self.assertEqual(fake.late_spawns, 0)
        finally:
            stopped.set()
            watcher.join()
        self.assertFalse(errors, errors)
        self.assertEqual(len(builder.state['objects']), 7)
        self.assertEqual(len(json.loads((builder.out / 'state.json').read_text())['objects']), 7)
        self.assertFalse(builder.children)

    def test_failure_kills_all_children_and_never_seals_failed_objects(self):
        builder = self.builder(jobs=3)
        sources, objects = self.sources(12)
        with self.compilers(builder, failure='unit0.cpp', delay=5) as fake:
            with self.assertRaises(RuntimeError):
                builder.compile_many(sources, objects, ['-O2'], 'dc')
            self.assertLessEqual(len(fake.commands), 3)
            self.assertGreaterEqual(len(fake.commands), 1)
            self.assertEqual(fake.active, 0)
            self.assertEqual(fake.late_spawns, 0)
            self.assertTrue(all(process.poll() is not None for process in fake.processes.values()))
            count = len(fake.commands)
            with self.assertRaises(RuntimeError):
                builder.compile_one(sources[4], objects[4], ['-O2'], 'dc')
            self.assertEqual(len(fake.commands), count)
        self.assertFalse(builder.state['objects'])
        self.assertFalse(builder.children)
        self.assertTrue(list((builder.out / 'logs').glob('*.log')))

    def test_cancellation_stops_active_jobs_and_does_not_start_pending_jobs(self):
        builder = self.builder(jobs=3)
        sources, objects = self.sources(12)
        with self.compilers(builder, delay=5) as fake:
            timer = threading.Timer(0.08, builder.cancel, args=(RuntimeError('synthetic interruption'),))
            timer.start()
            try:
                with self.assertRaises(RuntimeError):
                    builder.compile_many(sources, objects, [], 'tests')
            finally:
                timer.join()
            self.assertLessEqual(len(fake.commands), 3)
            self.assertEqual(fake.active, 0)
            self.assertEqual(fake.late_spawns, 0)
        self.assertFalse(builder.children)
        self.assertFalse(builder.state['objects'])

    def test_jobs_one_remains_serial(self):
        builder = self.builder(jobs=1)
        sources, objects = self.sources(3)
        with self.compilers(builder) as fake:
            builder.compile_many(sources, objects, [], 'dc')
            self.assertEqual(fake.peak, 1)

    def test_retry_logs_are_preserved(self):
        builder = self.builder()
        with self.compilers(builder):
            first = builder.run('same-name', ['fake-command'])
            second = builder.run('same-name', ['fake-command'])
        self.assertNotEqual(first, second)
        self.assertEqual(first.read_text(), second.read_text())
        self.assertEqual(len(list((builder.out / 'logs').glob('same-name*.command.json'))), 2)

    def test_actual_compile_one_argv_and_reuse_match_sealed_builder(self):
        sources, objects = self.sources(2)
        for source, obj, filename, group in zip(sources, objects,
                ['module-flags.make', 'tests-flags.make'], ['dc', 'tests']):
            flags = OLD.remap_flags(OLD.parse_flags((ROOT / 'evidence' / filename).read_text()),
                self.root / 'candidate/core', self.root / 'build-isolated')
            commands = []
            for module in [OLD, NEW]:
                builder = self.builder(module)
                emitted = []

                def run(name, command, **kwargs):
                    emitted.append(command.copy())
                    Path(command[command.index('-o') + 1]).write_bytes(b'captured compiler object')

                builder.run = run
                builder.compile_one(source, obj, flags, group)
                builder.compile_one(source, obj, flags, group)
                self.assertEqual(len(emitted), 1, 'Verified reuse must emit no compiler command.')
                commands.append(emitted[0])
            self.assertEqual(commands[0], commands[1], filename)
        self.assertEqual(OLD.MAX_ADDRESS_SPACE, NEW.MAX_ADDRESS_SPACE)

    def test_jobs_range_and_plan(self):
        for jobs in [0, 9]:
            with self.assertRaises(ValueError):
                NEW.plan(ROOT, jobs)
        self.assertEqual(NEW.plan(ROOT)['jobs'], 1)
        self.assertEqual(NEW.plan(ROOT, 6)['jobs'], 6)
        self.assertEqual(NEW.plan(ROOT, 8)['cpuAffinityPolicy'], 'all initially allowed CPUs')


if __name__ == '__main__':
    unittest.main(verbosity=2)
