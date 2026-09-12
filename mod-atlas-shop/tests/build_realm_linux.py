#!/usr/bin/env python3
"""Link an isolated Atlas test realm from the verified September 9 Linux recipe.

Reads existing core/build inputs; writes only below --root. Does not install,
start services, connect to a database, or change the production checkout.
Run inside a resource-limited, read-only systemd sandbox (see the test guide).
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shlex
import subprocess
import time
from patch_native_core import overlay

BASE = Path('/opt/arthas-next/candidates/modules-update-20260905T1016Z')
CURRENT = Path('/opt/arthas-next/candidates/modules-update-20260909T1035Z')
CHAT = Path('/opt/arthas-next/candidates/atlas-chat-whispers-20260907')


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read_flags(path, config_dir, main=False):
    text = path.read_text()
    flags = []
    for key in ('CXX_DEFINES', 'CXX_INCLUDES', 'CXX_FLAGS'):
        match = re.search(r'^' + key + r' = (.*)$', text, re.M)
        if not match:
            raise RuntimeError('Missing CMake flag group: ' + key)
        flags += shlex.split(match[1])
    result = []
    for flag in flags:
        if flag.startswith('-D_CONF_DIR='):
            flag = '-D_CONF_DIR="' + str(config_dir) + '"'
        elif main and flag.startswith('-DAC_MODULES_LIST='):
            flag = flag[:-1] + 'mod-atlas-shop,"'
        elif main and flag.startswith('-DCONFIG_FILE_LIST='):
            flag = flag[:-1] + 'mod_atlas_shop.conf,"'
        result.append(flag)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--output-name', choices=('build', 'build-native'), default='build')
    parser.add_argument('--reuse-verified', action='store_true', help='Reuse matching objects from this fixture when its core inputs and module headers are unchanged.')
    parser.add_argument('--release-candidate', action='store_true',
                        help='Build a separate, inactive AtlasShop release with its own server/etc directory.')
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    if args.release_candidate:
        if root.parent != Path('/opt/arthas-next/candidates') or not re.fullmatch(r'atlas-shop-rename-[0-9A-Za-z-]+', root.name):
            raise RuntimeError('Expected a dedicated /opt/arthas-next/candidates/atlas-shop-rename-* directory.')
        config_dir = root / 'server/etc'
    else:
        if root.parent != Path('/opt/atlas-shop-tests') or not re.fullmatch(r'rename-[0-9A-Za-z-]+', root.name):
            raise RuntimeError('Expected an existing, dedicated /opt/atlas-shop-tests/rename-* directory.')
        config_dir = root / 'etc'
    out = root / args.output_name
    out.mkdir(exist_ok=True)
    module = root / 'mod-atlas-shop'
    recipe = CURRENT / 'build-isolated/logs/world-candidate-link.command.json'
    command = json.loads(recipe.read_text())
    if command[0] != '/usr/bin/c++' or command.count('-o') != 1:
        raise RuntimeError('Unrecognized original linker command.')
    inputs = [Path(x) for x in command if not x.startswith('-') and x.endswith(('.o', '.a', '.so'))]
    before = {str(p): [p.stat().st_size, p.stat().st_mtime_ns, p.stat().st_ino] for p in inputs}
    source_hashes = {}
    for p in sorted((module / 'src').glob('*')):
        if p.is_file():
            source_hashes[str(p.relative_to(module))] = sha(p)
    manifest = {'moduleSources': source_hashes, 'baseCoreHead': subprocess.check_output(
        ['git', '-c', 'safe.directory=' + str(BASE / 'core'), '-C', str(BASE / 'core'), 'rev-parse', 'HEAD'], text=True).strip(),
        'originalLinkRecipeSha256': sha(recipe), 'inputSignatures': before, 'compiled': []}
    previous = {}
    manifest_path = out / 'manifest.json'
    if args.reuse_verified and manifest_path.exists():
        candidate = json.loads(manifest_path.read_text())
        headers = lambda entries: {key: value for key, value in entries.items() if key.endswith(('.h', '.hpp'))}
        if all(candidate.get(key) == manifest[key] for key in ('baseCoreHead', 'originalLinkRecipeSha256', 'inputSignatures')) \
                and headers(candidate.get('moduleSources', {})) == headers(source_hashes):
            previous = {entry['source']: entry for entry in candidate.get('compiled', [])}

    def run(name, cmd):
        print('RUN', name, flush=True)
        (out / (name + '.command.json')).write_text(json.dumps(cmd, indent=2) + '\n')
        with (out / (name + '.log')).open('w') as log:
            result = subprocess.run(cmd, cwd=out, stdout=log, stderr=subprocess.STDOUT, timeout=1800)
        if result.returncode:
            print((out / (name + '.log')).read_text()[-18000:], flush=True)
            raise RuntimeError(name + ' failed: ' + str(result.returncode))

    def compile_source(name, source, flags):
        obj = out / (name + '.o')
        old = previous.get(str(source))
        if old and old['sourceSha256'] == sha(source) and old['flags'] == flags and obj.exists() and old['objectSha256'] == sha(obj):
            print('REUSE', name, flush=True)
            manifest['compiled'].append({**old, 'reused': True})
            return str(obj)
        cmd = ['/usr/bin/c++', *flags, '-c', str(source), '-o', str(obj)]
        run(name, cmd)
        manifest['compiled'].append({'source': str(source), 'sourceSha256': sha(source),
                                     'objectSha256': sha(obj), 'flags': flags})
        return str(obj)

    module_flags = read_flags(BASE / 'build/modules/CMakeFiles/modules.dir/flags.make', config_dir)
    common_flags = read_flags(BASE / 'build/src/common/CMakeFiles/common.dir/flags.make', config_dir)
    app_flags = read_flags(BASE / 'build/src/server/apps/CMakeFiles/worldserver.dir/flags.make', config_dir, main=True)
    loader_text = (CURRENT / 'evidence/active-ModulesLoader.cpp').read_text()
    marker = '    // Modules\n'
    if loader_text.count(marker) != 1 or 'Addmod_atlas_shopScripts' in loader_text:
        raise RuntimeError('Unexpected previous module loader.')
    loader = out / 'ModulesLoader.cpp'
    loader.write_text('void Addmod_atlas_shopScripts();\n' + loader_text.replace(marker, marker + '    Addmod_atlas_shopScripts();\n'))
    additions = [compile_source(name, module / 'src' / (name + '.cpp'), module_flags)
                 for name in ('atlas_shop', 'atlas_shop_native', 'atlas_shop_loader')]
    # The save barrier and session lifetime hooks are compiled into a separate
    # overlay. Never edit or reuse objects from the active/source checkout.
    game_flags = read_flags(BASE / 'build/src/server/game/CMakeFiles/game.dir/flags.make', config_dir)
    game_flags += ['-I' + str(module / 'src')]
    core_overlay = out / 'native-core'
    manifest['nativeCoreOverlay'] = overlay(BASE / 'core/src/server/game', core_overlay)
    for entry in manifest['nativeCoreOverlay']:
        source = core_overlay / entry['relative']
        additions.append(compile_source('native_' + source.stem, source, game_flags))
    loader_obj = compile_source('ModulesLoader', loader, module_flags)
    # ConfigMgr embeds _CONF_DIR: changing only --config would still read live module configs.
    additions.append(compile_source('Config', BASE / 'core/src/common/Configuration/Config.cpp', common_flags))
    main_obj = compile_source('Main', BASE / 'core/src/server/apps/worldserver/Main.cpp', app_flags)
    old_loader = str(CHAT / 'build-overlay/ModulesLoader.cpp.o')
    old_main = str(BASE / 'build/src/server/apps/CMakeFiles/worldserver.dir/worldserver/Main.cpp.o')
    if command.count(old_loader) != 1 or command.count(old_main) != 1:
        raise RuntimeError('Expected exactly one original loader and main object.')
    command[command.index(old_loader)] = loader_obj
    command[command.index(old_main)] = main_obj
    command[command.index('-o') + 1] = str(out / 'worldserver')
    command[1:1] = additions
    for i, token in enumerate(command):
        if token.startswith('-Wl,-Map='):
            command[i] = '-Wl,-Map=' + str(out / 'worldserver.map')
        elif token.startswith('-Wl,--dependency-file='):
            command[i] = '-Wl,--dependency-file=' + str(out / 'worldserver.link.d')
    run('link', command)
    symbols = subprocess.check_output(['nm', '-C', str(out / 'worldserver')], text=True)
    expected = set(re.findall(r'void (Addmod_\w+Scripts)\(\);', loader.read_text()))
    missing = sorted(x for x in expected if x + '()' not in symbols)
    if missing:
        raise RuntimeError('Missing module registrations: ' + ', '.join(missing))
    for marker in ('StorageHooksVersion', 'CharacterHooksVersion', 'SessionHooksVersion'):
        if 'AtlasShop::' + marker + '()' not in symbols:
            raise RuntimeError('Native core hook missing from linked worldserver: ' + marker)
    after = {str(p): [p.stat().st_size, p.stat().st_mtime_ns, p.stat().st_ino] for p in inputs}
    if before != after:
        raise RuntimeError('Existing link inputs changed during the build.')
    manifest.update({'worldserverSha256': sha(out / 'worldserver'), 'registrations': sorted(expected),
                     'configurationDirectory': str(config_dir), 'releaseCandidate': args.release_candidate,
                     'builtAtUnix': int(time.time()), 'linked': True, 'started': False})
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print('PASS: isolated worldserver linked; all previous module registrations plus AtlasShop present.', flush=True)


if __name__ == '__main__':
    main()
