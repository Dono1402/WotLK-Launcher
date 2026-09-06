import hashlib
import json
import pathlib
import re
import shlex
import shutil
import subprocess
import sys
from datetime import datetime, timezone

# Isolated additive relink. Never edit the baseline or activate a service here.
base, candidate = (pathlib.Path(arg).resolve() for arg in sys.argv[1:3])
module = pathlib.Path(__file__).resolve().parent / 'mod-atlas-armory'
assert base.parent == candidate.parent == pathlib.Path('/opt/arthas-next/candidates')
assert candidate != base and candidate.name.startswith('armory-live-')
work = candidate / 'build-overlay'
work.mkdir(exist_ok=False)
(candidate / 'server/bin').mkdir(parents=True, exist_ok=False)
shutil.copytree(module, candidate / 'module')

def run(args, **options):
    print('Running:', args[0], pathlib.Path(str(args[-1])).name, flush=True)
    return subprocess.run(args, check=True, **options)

def build_id(file):
    output = subprocess.check_output(['readelf', '-n', str(file)], text=True)
    return re.search(r'Build ID: (\w+)', output).group(1)

installed = base / 'server/bin/worldserver'
old_binary = base / 'build/src/server/apps/worldserver'
assert build_id(installed) == build_id(old_binary) == '0793bcdb2a6486ac94767ce4056bd162049a083d'
commands = json.loads((base / 'build/compile_commands.json').read_text())
link_dir = base / 'build/src/server/apps'
link = shlex.split((link_dir / 'CMakeFiles/worldserver.dir/link.txt').read_text())
inputs = [(link_dir / arg).resolve() for arg in link if arg.endswith(('.a', '.o'))]
fingerprints = {str(file): (file.stat().st_size, file.stat().st_mtime_ns) for file in inputs}
assert all(file.stat().st_mtime_ns <= old_binary.stat().st_mtime_ns for file in inputs)
(work / 'baseline-inputs.json').write_text(json.dumps(fingerprints, indent=2))

source = candidate / 'module/src/atlas_armory.cpp'
entry = next(item for item in commands if item['file'].endswith('/atlas_armory.cpp'))
original = entry.get('arguments') or shlex.split(entry['command'])
args, skip = [], False
for arg in original:
    if skip:
        skip = False
    elif arg in ('-o', '-MF', '-MT', '-MQ', '-MJ'):
        skip = True
    elif arg not in ('-c', '-MD', '-MMD', '-MP', entry['file']):
        args.append(arg)
args[1:1] = ['-I', str(source.parent)]
obj = work / 'atlas_armory.cpp.o'
run(args + ['-c', str(source), '-o', str(obj)], cwd=entry['directory'])

old_archive = base / 'build/modules/libmodules.a'
members = subprocess.check_output(['ar', 't', str(old_archive)], text=True).splitlines()
assert members.count(obj.name) == 1 and members.count('ModulesLoader.cpp.o') == 1
archive = work / 'libmodules.a'
print('Copying module archive; all other modules and loaders are retained', flush=True)
shutil.copy2(old_archive, archive)
run(['ar', 'rcs', str(archive), str(obj)])
assert subprocess.check_output(['ar', 't', str(archive)], text=True).splitlines() == members
loader = base / 'build/modules/gen_scriptloader/static/ModulesLoader.cpp'
shutil.copy2(loader, work / 'ModulesLoader.baseline.cpp')

binary = candidate / 'server/bin/worldserver'
new_link, output_next, archives_replaced = [], False, 0
for arg in link:
    if output_next:
        new_link.append(str(binary))
        output_next = False
    elif arg == '-o':
        new_link.append(arg)
        output_next = True
    elif arg.startswith('-Wl,--dependency-file='):
        new_link.append('-Wl,--dependency-file=' + str(work / 'worldserver.link.d'))
    elif arg.startswith('-Wl,-rpath,'):
        new_link.append('-Wl,-rpath,' + str(base / 'server/lib'))
    elif arg.endswith('.a') and (link_dir / arg).resolve() == old_archive:
        new_link.append(str(archive))
        archives_replaced += 1
    else:
        new_link.append(arg)
assert archives_replaced == 1
(work / 'link-command.json').write_text(json.dumps(new_link, indent=2))
run(new_link, cwd=link_dir)
assert {str(file): (file.stat().st_size, file.stat().st_mtime_ns) for file in inputs} == fingerprints
assert loader.read_bytes() == (work / 'ModulesLoader.baseline.cpp').read_bytes()
assert build_id(binary) != build_id(installed)
assert 'not found' not in subprocess.check_output(['ldd', str(binary)], text=True)
def needed(file):
    return sorted(re.findall(r'Shared library: \[(.*?)\]', subprocess.check_output(['readelf', '-d', str(file)], text=True)))
assert needed(binary) == needed(installed)
run([str(binary), '--version'])
with binary.open('rb') as stream:
    digest = hashlib.file_digest(stream, 'sha256').hexdigest()
manifest = {'builtAt': datetime.now(timezone.utc).isoformat(), 'candidate': str(candidate), 'baseline': str(base),
            'baselineBuildId': build_id(installed), 'newBuildId': build_id(binary), 'sha256': digest,
            'moduleRegistrationPreserved': True, 'baselineInputsUnchanged': True, 'runtimeDependenciesUnchanged': True}
(candidate / 'build-manifest.json').write_text(json.dumps(manifest, indent=2))
print(json.dumps(manifest), flush=True)
