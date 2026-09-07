#!/usr/bin/env python3
"""Prepare an isolated Atlas world binary; never install, configure or start it.

Reuse the active release's immutable link inputs only after rebuilding its exact
runtime sections. Add a replacement registration object before the static module
archive, so the archive's old registration object is not extracted by the linker.
No large archive is copied or modified. Run on the Linux build host.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import resource
import shlex
import subprocess
import struct
import sys


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def run(arguments, cwd, output=None):
    print('RUN', shlex.join(map(str, arguments)), flush=True)
    subprocess.run(arguments, cwd=cwd, check=True, stdout=output)


def runtime_sections(path):
    # --no-keep-memory can change DWARF/debug ordering and therefore the build ID.
    # Compare every allocated ELF section, including code, relocations, dynamic
    # symbols, data and BSS, with only the nonfunctional build-id note excluded.
    with Path(path).open('rb') as stream:
        header = struct.unpack('<16sHHIQQQIHHHHHH', stream.read(64))
        if header[0][:6] != b'\x7fELF\x02\x01' or header[11] != 64:
            raise RuntimeError('Expected a little-endian ELF64 executable.')
        stream.seek(header[6])
        sections = [struct.unpack('<IIQQQQIIQQ', stream.read(64)) for _ in range(header[12])]
        names = sections[header[13]]
        stream.seek(names[4])
        strings = stream.read(names[5])
        result = {'elf-header': [header[i] for i in [1, 2, 3, 4, 7]]}
        for section in sections:
            if not section[2] & 2:
                continue
            start = section[0]
            name = strings[start:strings.index(b'\0', start)].decode('ascii')
            if name == '.note.gnu.build-id':
                continue
            digest = hashlib.sha256()
            if section[1] != 8:  # SHT_NOBITS has size/layout but no stored payload.
                stream.seek(section[4])
                remaining = section[5]
                while remaining:
                    chunk = stream.read(min(remaining, 1024**2))
                    if not chunk:
                        raise RuntimeError('Truncated ELF section.')
                    digest.update(chunk)
                    remaining -= len(chunk)
            result[name] = [section[i] for i in [1, 2, 3, 5, 6, 7, 8, 9]] + [digest.hexdigest()]
        return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--module-source', required=True, type=Path)
    parser.add_argument('--resume', action='store_true', help='Resume this unfinished candidate after a failed preparation.')
    args = parser.parse_args()
    root = Path('/opt/arthas-next/candidates').resolve()
    baseline, output, module = args.baseline.resolve(), args.output.resolve(), args.module_source.resolve()
    if sys.platform != 'linux' or not baseline.is_relative_to(root) or not output.is_relative_to(root):
        raise RuntimeError('Both releases must be inside the Linux Atlas candidate directory.')
    if baseline == output or not output.name.startswith('atlas-chat-'):
        raise RuntimeError('Use a separate atlas-chat-* candidate directory.')
    output.mkdir(exist_ok=True)
    if args.resume:
        if (output / 'build-manifest.json').exists() or not (output / 'preparation.lock').is_file():
            raise RuntimeError('Only an unfinished preparation can be resumed.')
        previous_pid = int((output / 'preparation.lock').read_text())
        if Path(f'/proc/{previous_pid}').exists():
            raise RuntimeError('The previous preparation process is still present.')
    with (output / 'preparation.lock').open('w' if args.resume else 'x') as lock:
        lock.write(str(os.getpid()))
    os.nice(19)
    resource.setrlimit(resource.RLIMIT_AS, (4 * 1024**3, 4 * 1024**3))
    manifest = json.loads((baseline / 'build-manifest.json').read_text())
    baseline_binary = baseline / 'server/bin/worldserver'
    if sha(baseline_binary) != manifest['sha256']:
        raise RuntimeError('The baseline executable no longer matches its build manifest.')
    build = Path(manifest['baseline']) / 'build'
    cwd = build / 'src/server/apps'
    recorded = json.loads((baseline / 'build-overlay/baseline-inputs.json').read_text())
    for name, expected in recorded.items():
        current = Path(name).stat()
        if [current.st_size, current.st_mtime_ns] != expected:
            raise RuntimeError('An original link input changed: ' + name)
    original = json.loads((baseline / 'build-overlay/link-command.json').read_text())
    inputs = {str((cwd / item).resolve()): [(cwd / item).stat().st_size, (cwd / item).stat().st_mtime_ns]
              for item in original if item.endswith(('.a', '.o', '.so')) or '.so.' in item}
    overlay = output / 'build-overlay'
    overlay.mkdir(exist_ok=args.resume)
    (output / 'server/bin').mkdir(parents=True, exist_ok=args.resume)

    def link_to(target, extra=()):
        command = original.copy()
        command[command.index('-o') + 1] = str(target)
        command = [f'-Wl,--dependency-file={overlay / (target.name + ".link.d")}'
                   if item.startswith('-Wl,--dependency-file=') else item for item in command]
        command.insert(1, '-Wl,--no-keep-memory')
        archive_index = command.index(str(baseline / 'build-overlay/libmodules.a'))
        command[archive_index:archive_index] = [str(item) for item in extra]
        (overlay / (target.name + '.link-command.json')).write_text(json.dumps(command, indent=2))
        run(command, cwd)

    reproduced = overlay / 'worldserver-baseline-reproduced'
    if not args.resume or not reproduced.is_file():
        link_to(reproduced)
    expected_sections, rebuilt_sections = runtime_sections(baseline_binary), runtime_sections(reproduced)
    if expected_sections != rebuilt_sections:
        differences = [name for name in expected_sections.keys() | rebuilt_sections.keys()
                       if expected_sections.get(name) != rebuilt_sections.get(name)]
        raise RuntimeError('Baseline runtime differs from the active executable: ' + ', '.join(differences))
    (overlay / 'baseline-runtime-sections.json').write_text(json.dumps(expected_sections, indent=2))
    baseline_byte_exact = sha(reproduced) == manifest['sha256']
    print('PASS: all baseline runtime sections match the active executable exactly.', flush=True)

    loader_text = (baseline / 'build-overlay/ModulesLoader.baseline.cpp').read_text()
    registrations = set(re.findall(r'void (Addmod_\w+Scripts)\(\);', loader_text))
    with (overlay / 'ModulesLoader.baseline.o').open('wb') as dest:
        run(['ar', 'p', baseline / 'build-overlay/libmodules.a', 'ModulesLoader.cpp.o'], cwd, dest)
    symbols = subprocess.check_output(['nm', '-C', overlay / 'ModulesLoader.baseline.o'], text=True)
    if registrations != set(re.findall(r' U (Addmod_\w+Scripts)\(\)', symbols)):
        raise RuntimeError('Registration source does not match the active archive.')
    if loader_text.count('    // Modules\n') != 1 or 'Addmod_atlas_chatScripts' in loader_text:
        raise RuntimeError('Unexpected module registration source.')
    loader_text = 'void Addmod_atlas_chatScripts();\n' + loader_text.replace(
        '    // Modules\n', '    // Modules\n    Addmod_atlas_chatScripts();\n')
    loader = overlay / 'ModulesLoader.cpp'
    loader.write_text(loader_text)
    flags_text = (build / 'modules/CMakeFiles/modules.dir/flags.make').read_text()
    flags = []
    for key in ['CXX_DEFINES', 'CXX_INCLUDES', 'CXX_FLAGS']:
        match = re.search(r'^' + key + r' = (.*)$', flags_text, re.MULTILINE)
        if not match:
            raise RuntimeError('Missing original compile flags: ' + key)
        flags.extend(shlex.split(match[1]))
    objects = []
    for source in [loader, module / 'src/atlas_chat.cpp', module / 'src/atlas_chat_loader.cpp']:
        obj = overlay / (source.name + '.o')
        run(['/usr/bin/c++', *flags, '-c', source, '-o', obj], build / 'modules')
        objects.append(obj)
    candidate = output / 'server/bin/worldserver'
    link_to(candidate, objects)
    if sha(candidate) == manifest['sha256']:
        raise RuntimeError('Candidate has not incorporated the chat module.')
    symbols = subprocess.check_output(['nm', '-C', candidate], text=True)
    for function in ['AddModulesScripts', 'AddAtlasChatScripts', 'Addmod_atlas_chatScripts', *registrations]:
        if not re.search(r' T ' + function + r'\(\)', symbols):
            raise RuntimeError('Missing module registration: ' + function)
    for name, expected in inputs.items():
        current = Path(name).stat()
        if [current.st_size, current.st_mtime_ns] != expected:
            raise RuntimeError('A baseline link input changed during preparation: ' + name)
    dependencies = subprocess.check_output(['ldd', candidate], text=True)
    if 'not found' in dependencies:
        raise RuntimeError('Candidate has an unresolved shared-library dependency.')
    (overlay / 'runtime-dependencies.txt').write_text(dependencies)
    result = dict(baseline=str(baseline), baselineSha256=manifest['sha256'], baselineReproducedExactly=baseline_byte_exact,
                  baselineRuntimeReproducedExactly=True,
                  candidate=str(candidate), sha256=sha(candidate), size=candidate.stat().st_size,
                  registrationsPreserved=sorted(registrations),
                  sourceSha256={p.name: sha(p) for p in (module / 'src').glob('*') if p.is_file()},
                  linkInputsUnchanged=True, linked=True, installed=False, serviceStarted=False)
    (output / 'build-manifest.json').write_text(json.dumps(result, indent=2))
    print(json.dumps(result, indent=2), flush=True)


if __name__ == '__main__':
    main()
