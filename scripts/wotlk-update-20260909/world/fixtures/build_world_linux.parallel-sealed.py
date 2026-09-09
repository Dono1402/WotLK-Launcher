#!/usr/bin/env python3
"""Plan or build the pinned DC candidate in a NEW Linux directory.

Never installs, starts worldserver, imports SQL, edits a baseline, deletes files,
or invokes CMake/Make. Execution requires an explicit phase and confirmation.
Run under the externally reviewed cgroup/network/filesystem sandbox.
"""
import argparse
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import posixpath
import re
import shlex
import shutil
import signal
import struct
import subprocess
import sys
import threading
import time
import xml.etree.ElementTree as ET

BASE = PurePosixPath('/opt/arthas-next/candidates/modules-update-20260905T1016Z')
BASE_CORE, BASE_BUILD = BASE / 'core', BASE / 'build'
ARMORY = PurePosixPath('/opt/arthas-next/candidates/armory-live-20260905T1310Z')
ACTIVE = PurePosixPath('/opt/arthas-next/candidates/atlas-chat-whispers-20260907')
ACTIVE_ARCHIVE = ARMORY / 'build-overlay/libmodules.a'
ACTIVE_BINARY = ACTIVE / 'server/bin/worldserver'
EXPECTED_ROOT = PurePosixPath('/opt/arthas-next/candidates/modules-update-20260909T1035Z')
TARGET_DC = '3dd90f7c1122abc291edcbd9d3dc68f6c8fed0bc'
MIN_FREE = 8 * 1024**3
MAX_ADDRESS_SPACE = 4 * 1024**3
MUTATING_PHASES = ('baseline', 'compile', 'link', 'tests')


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def utc():
    return datetime.now(timezone.utc).isoformat()


def runtime_sections(path):
    """ELF64 little-endian SHF_ALLOC layout+bytes; exclude only GNU build-id."""
    with Path(path).open('rb') as stream:
        raw = stream.read(64)
        if len(raw) != 64:
            raise RuntimeError('Truncated ELF header.')
        header = struct.unpack('<16sHHIQQQIHHHHHH', raw)
        if header[0][:6] != b'\x7fELF\x02\x01' or header[11] != 64:
            raise RuntimeError('Expected ELF64 little endian with 64-byte section headers.')
        if not header[12] or header[13] >= header[12]:
            raise RuntimeError('Unsupported ELF extended or invalid section numbering.')
        if not header[10] or header[9] != 56:
            raise RuntimeError('Expected ordinary ELF64 program headers.')
        stream.seek(header[5])
        program_headers = []
        for _ in range(header[10]):
            raw = stream.read(56)
            if len(raw) != 56:
                raise RuntimeError('Truncated program header table.')
            program_headers.append(list(struct.unpack('<IIQQQQQQ', raw)))
        stream.seek(header[6])
        sections = []
        for _ in range(header[12]):
            raw = stream.read(64)
            if len(raw) != 64:
                raise RuntimeError('Truncated section table.')
            sections.append(struct.unpack('<IIQQQQIIQQ', raw))
        names = sections[header[13]]
        stream.seek(names[4])
        strings = stream.read(names[5])
        result = {'elf-header': [header[index] for index in [1, 2, 3, 4, 7]],
            'program-headers': {'offset': header[5], 'entries': program_headers}}
        for section in sections:
            if not section[2] & 2:
                continue
            start = section[0]
            name = strings[start:strings.index(b'\0', start)].decode('ascii')
            if name == '.note.gnu.build-id':
                continue
            digest = hashlib.sha256()
            if section[1] != 8:  # BSS/NOBITS has a size and layout but no file payload.
                stream.seek(section[4])
                remaining = section[5]
                while remaining:
                    chunk = stream.read(min(remaining, 1024**2))
                    if not chunk:
                        raise RuntimeError('Truncated ELF section ' + name)
                    digest.update(chunk)
                    remaining -= len(chunk)
            result[name] = [section[index] for index in [1, 2, 3, 5, 6, 7, 8, 9]] + [digest.hexdigest()]
        return result


def parse_flags(text):
    result = []
    for key in ['CXX_DEFINES', 'CXX_INCLUDES', 'CXX_FLAGS']:
        match = re.search(r'^' + key + r' = (.*)$', text, re.MULTILINE)
        if not match:
            raise RuntimeError('Missing compile recipe key ' + key)
        result.extend(shlex.split(match[1]))
    forbidden = ('-o', '-MF', '-MT', '-MQ', '-MD', '-MMD', '-MJ', '-save-temps')
    if any(flag == value or flag.startswith(value + '=') for flag in result for value in forbidden):
        raise RuntimeError('Unexpected output/dependency option in compile flags.')
    return result


def artifact_input(token):
    return not token.startswith('-') and (token.endswith(('.o', '.a', '.so')) or '.so.' in token)


def absolute_inputs(command, cwd):
    return [posixpath.normpath(posixpath.join(str(cwd), item)) if artifact_input(item) else item for item in command]


def remap_flags(flags, new_core, output):
    """Read headers from the frozen candidate; generated headers/gtest stay read-only in base."""
    result = []
    for flag in flags:
        flag = flag.replace(str(BASE_CORE), str(new_core))
        # The default config path is irrelevant to offline compile/tests, but must
        # not introduce a new macro referring to a live configuration location.
        if flag.startswith('-D_CONF_DIR='):
            flag = '-D_CONF_DIR="' + str(output / 'no-runtime-config') + '"'
        result.append(flag)
    return result


def replace_link_outputs(command, target, map_path):
    result = command.copy()
    if result.count('-o') != 1:
        raise RuntimeError('Expected exactly one linker output.')
    result[result.index('-o') + 1] = str(target)
    for index, item in enumerate(result):
        if item.startswith('-Wl,--dependency-file='):
            result[index] = '-Wl,--dependency-file=' + str(target) + '.link.d'
        elif item.startswith(('-Wl,-Map', '-Wl,--Map', '-Wl,--dependency-file,')):
            raise RuntimeError('Unreviewed linker output option.')
    if '-Wl,--no-keep-memory' not in result:
        result.insert(1, '-Wl,--no-keep-memory')
    result.insert(1, '-Wl,-Map=' + str(map_path))
    return result


def test_sources(core):
    dc = core / 'modules/mod-dungeon-clear'
    text = (dc / 'mod-dungeon-clear.cmake').read_text()
    match = re.search(r'add_executable\(dungeon_clear_tests\s+(.*?)\n\s*\)', text, re.S)
    if not match:
        raise RuntimeError('Cannot find authoritative DC test target.')
    tokens = shlex.split(match[1], comments=True)
    sources = [Path(item.replace('${MOD_PATH}', str(dc)).replace('${CMAKE_SOURCE_DIR}', str(core))) for item in tokens]
    if not sources or any('$' in str(path) or path.suffix != '.cpp' or not path.is_file() for path in sources):
        raise RuntimeError('Unexpected/missing DC test source.')
    return sources


def legacy_dc_members(archive_recipe):
    tokens = shlex.split(archive_recipe.splitlines()[0])
    members = [PurePosixPath(token).name for token in tokens if '/mod-dungeon-clear/' in token and token.endswith('.o')]
    all_members = [PurePosixPath(token).name for token in tokens if token.endswith('.o')]
    if len(all_members) != len(set(all_members)) or len(members) != 170:
        raise RuntimeError('Original archive member list differs from the reviewed baseline.')
    return set(members)


def stale_members(map_text, old_members):
    used = re.findall(re.escape(str(ACTIVE_ARCHIVE)) + r'\(([^)]+)\)', map_text)
    return sorted(set(used) & set(old_members))


def dependencies_outside_dc(build, changed_headers):
    """Read existing dependency lists; never reconfigure or write the old build."""
    hits = []
    names = [str(BASE_CORE / 'modules/mod-dungeon-clear' / name) for name in changed_headers]
    for path in build.rglob('*.o.d'):
        relative = path.relative_to(build).as_posix()
        if '/mod-dungeon-clear/' in relative and relative.startswith('modules/CMakeFiles/modules.dir/'):
            continue
        if relative.startswith('CMakeFiles/dungeon_clear_tests.dir/'):
            continue  # every test TU is rebuilt too
        text = path.read_text(errors='replace')
        matched = [name for name in names if name in text]
        if matched:
            hits.append({'dependencyFile': str(path), 'headers': matched})
    return hits


def tests_link_command(original, new_tests, dc_objects, chat_objects, target, map_path):
    command = absolute_inputs(original, BASE_BUILD)
    old_objects = [item for item in command if item.endswith('.o') and not item.startswith('-')]
    if len(old_objects) != 73:
        raise RuntimeError('Original test object count differs from the reviewed baseline.')
    # Remove ALL old test/mocks objects. Place replacement objects before game.a,
    # not merely before modules.a, so static-library reference resolution is valid.
    command = [item for item in command if item not in old_objects]
    first_library = next(index for index, item in enumerate(command) if artifact_input(item))
    command[first_library:first_library] = list(map(str, [*new_tests, *dc_objects, *chat_objects]))
    old_archive = str(BASE_BUILD / 'modules/libmodules.a')
    if command.count(old_archive) != 1:
        raise RuntimeError('Unexpected module archive count in test link.')
    command[command.index(old_archive)] = str(ACTIVE_ARCHIVE)
    return replace_link_outputs(command, target, map_path)


def plan(root, jobs=1):
    if not 1 <= jobs <= 8:
        raise ValueError('jobs must be between 1 and 8.')
    core = root / 'candidate/core'
    cpp = sorted((core / 'modules/mod-dungeon-clear/src').rglob('*.cpp'))
    if len(cpp) != 173:
        raise RuntimeError('Expected 173 DC translation units.')
    manifest = json.loads((root / 'candidate-manifest.json').read_text())
    if manifest['targetDungeonClear'] != TARGET_DC:
        raise RuntimeError('Wrong pinned DC candidate.')
    members = legacy_dc_members((root / 'evidence/module-archive-link.txt').read_text())
    tests = test_sources(core)
    if len(tests) != 75:
        raise RuntimeError('Expected the reviewed 75-source full test target.')
    command = json.loads((root / 'evidence/active-link-command.json').read_text())
    chats = [item for item in command if item.startswith(str(ACTIVE / 'build-overlay')) and item.endswith('.o')]
    if len(chats) != 3:
        raise RuntimeError('Expected the active Chat+loader object triplet.')
    return {'mode': 'plan-only', 'sourceRoot': str(root), 'executionRootRequired': str(EXPECTED_ROOT),
        'dcTranslationUnits': len(cpp), 'fullTestTranslationUnits': len(tests), 'oldDCArchiveMembersToExclude': len(members),
        'preservedChatObjects': chats, 'preservedArchive': str(ACTIVE_ARCHIVE),
        'minFreeBytes': MIN_FREE, 'addressSpaceLimitBytes': MAX_ADDRESS_SPACE,
        'jobs': jobs, 'cpuAffinityCount': 1 if jobs == 1 else None,
        'cpuAffinityPolicy': 'single CPU' if jobs == 1 else 'all initially allowed CPUs', 'nice': 19,
        'phases': list(MUTATING_PHASES), 'worldserverWillRun': False, 'oldInputsWillBeModified': False,
        'requiresExternalSandbox': 'PrivateNetwork, reviewed cgroup memory/CPU budget, MemorySwapMax=0, ProtectSystem=strict, only new root writable; DB sockets/config dirs inaccessible'}


class Builder:
    def __init__(self, root, phase, probe_mmaps=None, jobs=1):
        if not 1 <= jobs <= 8:
            raise ValueError('jobs must be between 1 and 8.')
        if sys.platform != 'linux':
            raise RuntimeError('Execution is Linux-only; plan mode is portable.')
        self.root = root.absolute()
        if str(self.root) != str(EXPECTED_ROOT) or self.root.resolve() != self.root:
            raise RuntimeError('Only the reviewed NEW candidate root is accepted, without symlinks.')
        for ancestor in [self.root, *self.root.parents]:
            if ancestor.is_symlink():
                raise RuntimeError('Symlink in candidate root ancestry.')
        self.phase = phase
        self.jobs = jobs
        self.core = self.root / 'candidate/core'
        self.out = self.root / 'build-isolated'
        self.probe_mmaps = probe_mmaps
        self.summary = plan(self.root, jobs)
        self.manifest = json.loads((self.root / 'candidate-manifest.json').read_text())
        self.capture = json.loads((self.root / 'capture-manifest.json').read_text())
        self.active_command = absolute_inputs(json.loads((self.root / 'evidence/active-link-command.json').read_text()), BASE_BUILD / 'src/server/apps')
        self.test_command = shlex.split((self.root / 'evidence/tests-link.txt').read_text())
        self.chat_objects = [Path(item) for item in self.summary['preservedChatObjects']]
        self.old_members = legacy_dc_members((self.root / 'evidence/module-archive-link.txt').read_text())
        self.dc_sources = sorted((self.core / 'modules/mod-dungeon-clear/src').rglob('*.cpp'))
        self.dc_objects = [self.out / 'objects/dc' / (str(path.relative_to(self.core / 'modules/mod-dungeon-clear')) + '.o') for path in self.dc_sources]
        self.tests = test_sources(self.core)
        self.test_objects = [self.out / 'objects/tests' / (str(path.relative_to(self.core)) + '.o') for path in self.tests]
        self.env = os.environ.copy()
        for key in list(self.env):
            if key.startswith('GTEST_'):
                self.env.pop(key)
        for key in ['CCACHE_DIR', 'CCACHE_BASEDIR', 'CCACHE_CONFIGPATH', 'MYSQL_PWD', 'DC_PROBE_MMAPS']:
            self.env.pop(key, None)
        self.env.update(TMPDIR=str(self.out / 'tmp'), TMP=str(self.out / 'tmp'), TEMP=str(self.out / 'tmp'),
            CCACHE_DISABLE='1', GCOV_PREFIX=str(self.out / 'coverage'), GCOV_PREFIX_STRIP='0', GCOV_EXIT_AT_ERROR='1')
        self.children = set()
        self._children_lock = threading.RLock()
        self._termination_lock = threading.RLock()
        self._state_lock = threading.RLock()
        self._stop_requested = threading.Event()
        self._first_failure = None
        self.state = None

    def safe(self, path):
        path = Path(path)
        if not path.resolve().is_relative_to(self.out.resolve()):
            raise RuntimeError('Write would escape the isolated output: ' + str(path))
        for ancestor in [path, *path.parents]:
            if ancestor == self.root:
                break
            if ancestor.is_symlink():
                raise RuntimeError('Refusing output through a symlink: ' + str(path))
        return path

    def disk_guard(self):
        free = shutil.disk_usage(self.root).free
        if free < MIN_FREE:
            raise RuntimeError('Free disk below 8 GiB guard: ' + str(free))

    def write_json(self, path, value):
        with self._state_lock:
            path = self.safe(path)
            path.parent.mkdir(parents=True, exist_ok=True)
            temporary = self.safe(path.with_name(path.name + '.tmp'))
            temporary.write_text(json.dumps(value, indent=2))
            os.replace(temporary, path)

    def save(self):
        with self._state_lock:
            self.state['updatedAtUtc'] = utc()
            self.write_json(self.out / 'state.json', self.state)

    def isolation_guard(self):
        if set(os.listdir('/sys/class/net')) - {'lo'}:
            raise RuntimeError('A private network namespace is required for every executed phase.')
        if self.phase == 'tests':
            forbidden = ['/run/mysqld/mysqld.sock', '/var/run/mysqld/mysqld.sock',
                str(BASE / 'server/etc/worldserver.conf'), str(ACTIVE / 'server/etc/worldserver.conf'),
                '/opt/arthas-next/server/etc/worldserver.conf']
            if any(os.access(path, os.R_OK) for path in forbidden):
                raise RuntimeError('Tests require inaccessible production DB sockets and runtime configs.')

    def verify_sources(self):
        expected = {}
        for name, record in self.capture['files'].items():
            if name.startswith('candidate/'):
                expected[name] = record['sha256']
        for name, digest in self.manifest['candidateDungeonClearFilesSha256'].items():
            expected['candidate/core/modules/mod-dungeon-clear/' + name] = digest
        for name, digest in expected.items():
            path = self.root / name
            if path.is_symlink() or not path.is_file() or sha(path) != digest:
                raise RuntimeError('Frozen candidate source changed: ' + name)
        actual_code = {path.relative_to(self.root).as_posix() for path in self.core.rglob('*')
            if path.is_file() and '.git' not in path.parts and path.suffix in ('.cpp', '.h', '.hpp', '.cc', '.c')}
        if actual_code - set(expected):
            raise RuntimeError('Unrecorded source/header appeared in candidate.')
        for name, digest in json.loads((self.root / 'evidence/tests-recipes-sha256.json').read_text()).items():
            if sha(self.root / 'evidence' / name) != digest:
                raise RuntimeError('Captured test recipe changed: ' + name)

    def signatures(self):
        commands = [self.active_command, absolute_inputs(self.test_command, BASE_BUILD)]
        names = {item for command in commands for item in command if artifact_input(item)}
        # Old test objects are not reused and must never be relied on for a new test run.
        names = {name for name in names if not (name.endswith('.o') and '/CMakeFiles/dungeon_clear_tests.dir/' in name)}
        names |= {str(BASE_BUILD / 'modules/CMakeFiles/modules.dir/flags.make'),
            str(BASE_BUILD / 'CMakeFiles/dungeon_clear_tests.dir/flags.make'), str(ACTIVE_BINARY),
            str(ACTIVE / 'build-manifest.json'), str(ACTIVE / 'build-overlay/worldserver.link-command.json')}
        signatures = {}
        for name in sorted(names):
            info = Path(name).stat()
            signatures[name] = [info.st_size, info.st_mtime_ns, info.st_ino, info.st_dev]
        return signatures

    def auxiliary_hashes(self):
        """Seal recipes, compiler binaries, and generated/gtest build headers used by these flags."""
        files = {Path('/usr/bin/c++'), Path('/usr/bin/ld'), Path('/usr/bin/as'),
            Path(str(BASE_BUILD / 'revision.h')), Path(str(BASE_BUILD / 'jemalloc_internal_defs.h'))}
        compiler_backend = subprocess.check_output(['/usr/bin/c++', '-print-prog-name=cc1plus'], text=True).strip()
        if not Path(compiler_backend).is_absolute():
            raise RuntimeError('Compiler backend did not resolve to an absolute path.')
        files.add(Path(compiler_backend))
        for name in ['module-flags.make', 'tests-flags.make', 'module-archive-link.txt', 'tests-link.txt',
                     'active-link-command.json', 'active-ModulesLoader.cpp']:
            files.add(self.root / 'evidence' / name)
        for name in ['module-flags.make', 'tests-flags.make']:
            flags = parse_flags((self.root / 'evidence' / name).read_text())
            directories = []
            for index, flag in enumerate(flags):
                if flag.startswith('-I') and len(flag) > 2:
                    directories.append(Path(flag[2:]))
                elif flag in ['-I', '-isystem'] and index + 1 < len(flags):
                    directories.append(Path(flags[index + 1]))
            for directory in directories:
                if directory.is_dir() and directory.is_relative_to(Path(str(BASE_BUILD))):
                    candidates = directory.glob('*.h') if directory == Path(str(BASE_BUILD)) else directory.rglob('*.h')
                    files.update(path for path in candidates if path.is_file())
        return {str(path): sha(path) for path in sorted(files)}

    def verify_inputs(self):
        current = self.signatures()
        if current != self.state['baselineInputSignatures']:
            changed = [name for name in current.keys() | self.state['baselineInputSignatures'].keys()
                if current.get(name) != self.state['baselineInputSignatures'].get(name)]
            raise RuntimeError('An immutable baseline input changed: ' + ', '.join(changed))
        active_manifest = json.loads(Path(str(ACTIVE / 'build-manifest.json')).read_text())
        if active_manifest['sha256'] != self.manifest['baseWorldSha256FromBuildManifest']:
            raise RuntimeError('The active manifest no longer identifies the reviewed world.')
        if json.loads(Path(str(ACTIVE / 'build-overlay/worldserver.link-command.json')).read_text()) != json.loads((self.root / 'evidence/active-link-command.json').read_text()):
            raise RuntimeError('The active linker recipe changed.')

    def initialise(self):
        self.isolation_guard()
        self.disk_guard()
        self.verify_sources()
        # Finish all read-only preflights before creating the ownership state.
        current_signatures = self.signatures()
        current_auxiliary = self.auxiliary_hashes()
        changed_headers = [name for name in self.manifest['changedDungeonClearFiles'] if name.endswith(('.h', '.hpp'))]
        outside = dependencies_outside_dc(Path(str(BASE_BUILD)), changed_headers)
        if outside:
            raise RuntimeError('Changed DC headers have external users requiring a wider rebuild: ' + json.dumps(outside))
        if self.out.is_symlink():
            raise RuntimeError('Isolated output is a symlink.')
        if self.out.exists() and not (self.out / 'state.json').exists():
            raise RuntimeError('Existing unowned output directory; refusing to reuse it.')
        self.out.mkdir(mode=0o700, exist_ok=True)
        import fcntl
        self.lock = self.safe(self.out / 'build.lock').open('a')
        fcntl.flock(self.lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        for relative in ['tmp', 'logs', 'coverage']:
            self.safe(self.out / relative).mkdir(mode=0o700, exist_ok=True)
        manifest_hash = sha(self.root / 'candidate-manifest.json')
        if (self.out / 'state.json').exists():
            self.state = json.loads(self.safe(self.out / 'state.json').read_text())
            if (self.state.get('root') != str(self.root) or self.state.get('manifestSha256') != manifest_hash
                    or self.state.get('builderSha256') != sha(Path(__file__))
                    or self.state.get('captureManifestSha256') != sha(self.root / 'capture-manifest.json')):
                raise RuntimeError('Resume identity/source manifest mismatch.')
            if self.state.get('auxiliaryInputSha256') != current_auxiliary:
                raise RuntimeError('Compiler, recipe, generated header or GoogleTest header changed; resume refused.')
        else:
            self.state = {'root': str(self.root), 'createdAtUtc': utc(), 'manifestSha256': manifest_hash,
                'builderSha256': sha(Path(__file__)), 'captureManifestSha256': sha(self.root / 'capture-manifest.json'),
                'baselineInputSignatures': current_signatures, 'auxiliaryInputSha256': current_auxiliary,
                'outsideDCHeaderUsers': [], 'phases': {}, 'objects': {}, 'outputs': {},
                'worldserverStarted': False, 'databaseAccess': False, 'installed': False}
        self.save()
        self.verify_inputs()
        expected = json.loads((self.root / 'evidence/armory-link-inputs.json').read_text())
        for name, signature in expected.items():
            info = Path(name).stat()
            if [info.st_size, info.st_mtime_ns] != signature:
                raise RuntimeError('Earlier baseline inputs no longer match the Armory seal: ' + name)
        if sha(ACTIVE_BINARY) != self.manifest['baseWorldSha256FromBuildManifest']:
            raise RuntimeError('Active world SHA256 differs from the reviewed manifest.')
        import resource
        resource.setrlimit(resource.RLIMIT_AS, (MAX_ADDRESS_SPACE, MAX_ADDRESS_SPACE))
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
        os.nice(max(0, 19 - os.getpriority(os.PRIO_PROCESS, 0)))
        affinity = os.sched_getaffinity(0)
        if self.jobs == 1:
            os.sched_setaffinity(0, {min(affinity)})
        self.allowed_cpus = sorted(os.sched_getaffinity(0))
        os.umask(0o077)

    def stop_child(self):
        """Latch cancellation before taking the child snapshot; no later spawn is allowed."""
        with self._children_lock:
            self._stop_requested.set()
            children = tuple(self.children)
        with self._termination_lock:
            self._terminate_children(children)

    @staticmethod
    def _terminate_children(children):
        def send(child, signum):
            try:
                os.killpg(child.pid, signum)
            except ProcessLookupError:
                pass

        for child in children:
            send(child, signal.SIGTERM)
        deadline = time.monotonic() + 5
        for child in children:
            try:
                child.wait(timeout=max(0, deadline - time.monotonic()))
            except subprocess.TimeoutExpired:
                pass
        # A compiler driver can exit before its descendants. Kill the process
        # group too, rather than relying only on the driver's return code.
        for child in children:
            send(child, signal.SIGKILL)
            child.wait()

    def cancel(self, error):
        with self._children_lock:
            if self._first_failure is None:
                self._first_failure = error
            self._stop_requested.set()
        self.stop_child()

    def check_cancelled(self):
        if self._stop_requested.is_set():
            raise RuntimeError('Build cancelled: ' + str(self._first_failure or 'interrupted'))

    def run(self, name, command, timeout=1800, env_extra=None):
        child = None
        try:
            self.check_cancelled()
            self.disk_guard()
            self.verify_inputs()
            # Preserve all attempts, including interrupted/failed compilations.
            with self._state_lock:
                attempt = 0
                while True:
                    suffix = '' if not attempt else '.attempt-' + str(attempt)
                    log = self.safe(self.out / 'logs' / (name + suffix + '.log'))
                    recipe = self.safe(self.out / 'logs' / (name + suffix + '.command.json'))
                    if not log.exists() and not recipe.exists():
                        break
                    attempt += 1
                self.write_json(recipe, command)
                stream = log.open('xb')
            with stream:
                print('RUN', name, shlex.join(list(map(str, command))), flush=True)
                environment = self.env.copy()
                if env_extra:
                    environment.update(env_extra)
                started = time.monotonic()
                # Cancellation and spawn share this lock. A child is registered
                # before cancellation can take its snapshot.
                with self._children_lock:
                    self.check_cancelled()
                    child = subprocess.Popen(list(map(str, command)), cwd=self.out,
                        stdout=stream, stderr=subprocess.STDOUT, env=environment, start_new_session=True)
                    self.children.add(child)
                while child.poll() is None:
                    self.check_cancelled()
                    self.disk_guard()
                    if time.monotonic() - started > timeout:
                        raise RuntimeError('Command exceeded timeout: ' + name)
                    self._stop_requested.wait(1)
                code = child.returncode
                if code:
                    raise RuntimeError('Command failed (' + str(code) + '): ' + name + '; see ' + str(log))
            self.verify_inputs()
            self.check_cancelled()
            return log
        except BaseException as exc:
            self.cancel(exc)
            raise
        finally:
            if child is not None:
                if child.poll() is None:
                    with self._termination_lock:
                        self._terminate_children((child,))
                with self._children_lock:
                    self.children.discard(child)

    def require_phase(self, phase):
        if not self.state['phases'].get(phase, {}).get('passed'):
            raise RuntimeError('A validated ' + phase + ' phase is required first.')
        for name in self.state['phases'][phase].get('outputKeys', []):
            record = self.state['outputs'][name]
            path = self.safe(self.out / name)
            if not path.is_file() or sha(path) != record['sha256']:
                raise RuntimeError('Sealed phase output changed: ' + name)

    def seal_output(self, path):
        path = self.safe(path)
        name = path.relative_to(self.out).as_posix()
        self.state['outputs'][name] = {'sha256': sha(path), 'size': path.stat().st_size}
        return name

    def baseline(self):
        output = self.safe(self.out / 'worldserver-baseline-reproduced')
        linkmap = self.safe(self.out / 'baseline.link.map')
        command = replace_link_outputs(self.active_command, output, linkmap)
        self.run('baseline-link', command, timeout=1800)
        output.chmod(0o600)
        expected, actual = runtime_sections(ACTIVE_BINARY), runtime_sections(output)
        if expected != actual:
            differences = sorted(name for name in expected.keys() | actual.keys() if expected.get(name) != actual.get(name))
            self.write_json(self.out / 'baseline-differences.json', differences)
            raise RuntimeError('Baseline runtime section mismatch: ' + ', '.join(differences))
        self.write_json(self.out / 'baseline-runtime-sections.json', actual)
        self.state['phases']['baseline'] = {'passed': True, 'atUtc': utc(), 'allAllocatedSectionsEquivalent': True,
            'byteIdentical': sha(output) == self.manifest['baseWorldSha256FromBuildManifest'],
            'outputKeys': [self.seal_output(output), self.seal_output(self.out / 'baseline-runtime-sections.json')]}

    def compile_one(self, source, obj, flags, group):
        self.check_cancelled()
        obj = self.safe(obj)
        obj.parent.mkdir(parents=True, exist_ok=True)
        command = ['/usr/bin/c++', *flags, '-MMD', '-MF', str(obj) + '.d', '-MT', str(obj), '-c', str(source), '-o', str(obj)]
        identity = hashlib.sha256(json.dumps(command).encode()).hexdigest()
        key = obj.relative_to(self.out).as_posix()
        with self._state_lock:
            previous = self.state['objects'].get(key)
        if previous and previous['commandSha256'] == identity and previous['sourceSha256'] == sha(source) and obj.is_file() and sha(obj) == previous['sha256']:
            self.check_cancelled()
            print('REUSE verified', key, flush=True)
            return
        with self._state_lock:
            if key in self.state['objects']:
                del self.state['objects'][key]
                self.save()
        self.run(group + '-' + source.stem, command, timeout=600)
        record = {'commandSha256': identity, 'sourceSha256': sha(source), 'sha256': sha(obj), 'size': obj.stat().st_size}
        with self._state_lock:
            self.check_cancelled()
            self.state['objects'][key] = record
            self.save()

    def compile_many(self, sources, objects, flags, group):
        """Keep at most jobs futures in flight; stop the whole batch on any failure."""
        if len(sources) != len(objects):
            raise RuntimeError('Source/object counts differ.')
        work = iter(zip(sources, objects))
        executor = ThreadPoolExecutor(max_workers=self.jobs, thread_name_prefix=group)
        pending = set()
        try:
            for _ in range(min(self.jobs, len(sources))):
                self.check_cancelled()
                source, obj = next(work)
                pending.add(executor.submit(self._compile_worker, source, obj, flags, group))
            while pending:
                finished, pending = wait(pending, timeout=1, return_when=FIRST_COMPLETED)
                for future in finished:
                    future.result()
                self.check_cancelled()
                for _ in finished:
                    pair = next(work, None)
                    if pair is None:
                        break
                    self.check_cancelled()
                    pending.add(executor.submit(self._compile_worker, *pair, flags, group))
        except BaseException as exc:
            self.cancel(exc)
            for future in pending:
                future.cancel()
            raise
        finally:
            executor.shutdown(wait=True, cancel_futures=True)

    def _compile_worker(self, source, obj, flags, group):
        try:
            self.compile_one(source, obj, flags, group)
        except BaseException as exc:
            # Hashing, output creation, and checkpoint failures are fatal too;
            # latch cancellation before the failed future becomes observable.
            self.cancel(exc)
            raise

    def compile(self):
        self.require_phase('baseline')
        flags = remap_flags(parse_flags((self.root / 'evidence/module-flags.make').read_text()), self.core, self.out)
        self.compile_many(self.dc_sources, self.dc_objects, flags, 'dc')
        self.state['phases']['compile'] = {'passed': True, 'atUtc': utc(), 'translationUnits': len(self.dc_sources),
            'jobs': self.jobs, 'allowedCPUs': self.allowed_cpus,
            'outputKeys': [self.seal_output(path) for path in self.dc_objects]}

    def verify_link(self, target, linkmap, require_registrations):
        map_text = linkmap.read_text()
        old = stale_members(map_text, self.old_members)
        if old:
            raise RuntimeError('Stale DC archive members extracted: ' + ', '.join(old))
        load_objects = set(re.findall(r'^LOAD (.+)$', map_text, re.MULTILINE))
        missing_objects = set(map(str, self.dc_objects)) - load_objects
        if missing_objects:
            raise RuntimeError('Link map lacks replacement DC objects: ' + ', '.join(sorted(missing_objects)))
        symbol_log = self.run(target.name + '-symbols', ['/usr/bin/nm', '-C', '--defined-only', str(target)], timeout=180)
        if require_registrations:
            expected = set(re.findall(r'void (Addmod_\w+Scripts)\(\);', (self.root / 'evidence/active-ModulesLoader.cpp').read_text()))
            expected.add('AddModulesScripts')
            actual = set(re.findall(r' [Tt] (Add(?:mod_\w+Scripts|ModulesScripts))\(\)', symbol_log.read_text()))
            if not expected.issubset(actual) or len(expected) != 9:
                raise RuntimeError('Missing/changed module registrations: ' + ', '.join(sorted(expected - actual)))
        self.run(target.name + '-elf-dynamic', ['/usr/bin/readelf', '-d', str(target)], timeout=120)
        return {'oldDCMembersExtracted': [], 'registrationsChecked': require_registrations}

    def link(self):
        self.require_phase('baseline')
        self.require_phase('compile')
        target = self.safe(self.out / 'worldserver-candidate')
        linkmap = self.safe(self.out / 'worldserver-candidate.link.map')
        command = self.active_command.copy()
        position = command.index(str(ACTIVE_ARCHIVE))
        command[position:position] = list(map(str, self.dc_objects))
        command = replace_link_outputs(command, target, linkmap)
        self.run('world-candidate-link', command, timeout=1800)
        target.chmod(0o600)
        details = self.verify_link(target, linkmap, True)
        if sha(target) == self.manifest['baseWorldSha256FromBuildManifest']:
            raise RuntimeError('World candidate did not change.')
        self.state['phases']['link'] = {'passed': True, 'atUtc': utc(), **details,
            'worldserverStarted': False, 'outputKeys': [self.seal_output(target), self.seal_output(linkmap)]}

    def tests_phase(self):
        self.require_phase('baseline')
        self.require_phase('compile')
        self.require_phase('link')
        flags = remap_flags(parse_flags((self.root / 'evidence/tests-flags.make').read_text()), self.core, self.out)
        self.compile_many(self.tests, self.test_objects, flags, 'tests')
        target = self.safe(self.out / 'dungeon_clear_tests')
        linkmap = self.safe(self.out / 'dungeon_clear_tests.link.map')
        command = tests_link_command(self.test_command, self.test_objects, self.dc_objects, self.chat_objects, target, linkmap)
        self.run('tests-candidate-link', command, timeout=1800)
        details = self.verify_link(target, linkmap, False)
        target.chmod(0o700)
        extra = {}
        if self.probe_mmaps:
            probe = self.probe_mmaps.resolve(strict=True)
            if not (probe / 'mmaps').is_dir():
                raise RuntimeError('Navigation probe root lacks mmaps directory.')
            extra['DC_PROBE_MMAPS'] = str(probe)
        try:
            self.run('tests-list', [str(target), '--gtest_list_tests'], timeout=120, env_extra=extra)
            xml = self.safe(self.out / 'dungeon-clear-tests.xml')
            self.run('tests-full', [str(target), '--gtest_filter=*', '--gtest_color=no', '--gtest_output=xml:' + str(xml)], timeout=1800, env_extra=extra)
        finally:
            target.chmod(0o600)
        report = ET.parse(xml).getroot()
        tests = int(report.attrib.get('tests', 0))
        failures = int(report.attrib.get('failures', 0))
        errors = int(report.attrib.get('errors', 0))
        skips = sum(1 for case in report.iter('testcase') if case.find('skipped') is not None)
        if not tests or failures or errors:
            raise RuntimeError('Incomplete or failed full gtest report.')
        self.state['phases']['tests'] = {'passed': True, 'atUtc': utc(), **details, 'tests': tests,
            'jobs': self.jobs, 'allowedCPUs': self.allowed_cpus,
            'failures': failures, 'errors': errors, 'skipped': skips, 'navigationFixturesProvided': bool(self.probe_mmaps),
            'translationUnits': len(self.tests), 'databaseAccess': False,
            'outputKeys': [self.seal_output(target), self.seal_output(linkmap), self.seal_output(xml)]}

    def execute(self):
        self.initialise()
        # SIGTERM must stop a compiler/linker child too, not leave it orphaned.
        def interrupted(signum, _frame):
            # Do not raise inside Popen or a state write. Workers/mainline observe
            # the latched cancellation and unwind after child tracking is complete.
            self.cancel(RuntimeError('Build interrupted by signal ' + str(signum)))
        signal.signal(signal.SIGTERM, interrupted)
        try:
            # A failed retry must not retain an earlier PASS, especially when
            # repeating the test phase with navigation fixtures added.
            index = MUTATING_PHASES.index(self.phase)
            for dependent in MUTATING_PHASES[index:]:
                self.state['phases'][dependent] = {'passed': False, 'invalidatedAtUtc': utc()}
            self.state['phases'][self.phase]['startedAtUtc'] = utc()
            self.state['phases'][self.phase]['jobs'] = self.jobs
            self.save()
            if self.phase == 'tests':
                self.tests_phase()
            else:
                getattr(self, self.phase)()
            self.check_cancelled()
            self.verify_sources()
            self.verify_inputs()
            if self.auxiliary_hashes() != self.state['auxiliaryInputSha256']:
                raise RuntimeError('Auxiliary inputs changed during this phase.')
            self.disk_guard()
            self.check_cancelled()
            self.state.pop('lastFailure', None)
            self.save()
            print(json.dumps(self.state['phases'][self.phase], indent=2), flush=True)
        except BaseException as exc:
            self.stop_child()
            for name in ['worldserver-baseline-reproduced', 'worldserver-candidate', 'dungeon_clear_tests']:
                binary = self.safe(self.out / name)
                if binary.is_file():
                    binary.chmod(0o600)
            self.state['lastFailure'] = {'phase': self.phase, 'atUtc': utc(), 'error': str(exc)}
            self.state['phases'][self.phase]['passed'] = False
            self.save()
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument('--phase', choices=['plan', *MUTATING_PHASES], default='plan')
    parser.add_argument('--confirm-isolated-build', action='store_true')
    parser.add_argument('--probe-mmaps', type=Path)
    parser.add_argument('--jobs', type=int, choices=range(1, 9), default=1)
    args = parser.parse_args()
    if args.phase == 'plan':
        print(json.dumps(plan(args.root.resolve(), args.jobs), indent=2))
        return
    if not args.confirm_isolated_build:
        raise RuntimeError('Execution requires --confirm-isolated-build after separate authorisation; default plan writes nothing.')
    Builder(args.root, args.phase, args.probe_mmaps, args.jobs).execute()


if __name__ == '__main__':
    main()
