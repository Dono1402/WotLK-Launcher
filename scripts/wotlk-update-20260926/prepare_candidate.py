#!/usr/bin/env python3
"""Prepare/build only the inactive September 26 candidate; never activate production."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path('/opt/arthas-next/candidates/atlas-all-update-20260926')
OLD = Path('/opt/arthas-next/candidates/atlas-all-update-20260912')
FIXTURE = Path('/opt/atlas-shop-tests/rename-20260926')
PINS = {
    'core': ('mod-playerbots/azerothcore-wotlk', 'Playerbot', '7f12e89ee5f467a50e62eba1d525eac7dc953d03'),
    'playerbots': ('mod-playerbots/mod-playerbots', 'master', '7bae1b5c58c76a0aa20381155edc08096d1485b2'),
    'dungeon-clear': ('jrad7/mod-dungeon-clear', 'master', '805b909c7286348e75d0561f8cc259750e6ae62b'),
    'ah-bot': ('azerothcore/mod-ah-bot', 'master', 'c11d8318cbd8714a9980f9464f78e07d3d48a70a'),
}
UNITS = ['arthas-worldserver.dungeon-clear-8224099', 'arthas-authserver', 'hermesproxy-wotlk',
         'wotlk-launcher-api', 'wotlk-launcher-server']


def run(args, **kwargs):
    return subprocess.run([str(a) for a in args], check=True, **kwargs)


def digest(p):
    with Path(p).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def inactive():
    for unit in UNITS:
        state = subprocess.check_output(['systemctl', 'show', unit, '-p', 'ActiveState', '--value'], text=True).strip()
        if state != 'inactive':
            raise RuntimeError('Production must remain inactive: ' + unit)
    state = json.loads(subprocess.check_output(['docker', 'inspect', 'arthas-mysql'], text=True))[0]['State']
    if state['Running']:
        raise RuntimeError('Production MySQL must remain stopped.')


def validate_root():
    if ROOT.resolve(strict=True) != ROOT or ROOT.parent != Path('/opt/arthas-next/candidates'):
        raise RuntimeError('Unexpected dedicated candidate path.')
    inactive()


def clone(name, dest):
    repo, branch, pin = PINS[name]
    if dest.exists():
        raise RuntimeError('Refuse to overwrite source: ' + str(dest))
    run(['git', 'clone', '--depth=1', '--single-branch', '--branch', branch, 'https://github.com/' + repo, dest])
    head = subprocess.check_output(['git', '-C', str(dest), 'rev-parse', 'HEAD'], text=True).strip()
    if head != pin:
        run(['git', '-C', dest, 'fetch', '--depth=1', 'origin', pin])
        run(['git', '-C', dest, 'switch', '--detach', pin])
    run(['git', '-C', dest, 'config', 'user.name', 'Atlas Integration'])
    run(['git', '-C', dest, 'config', 'user.email', 'atlas-integration@localhost'])


def prepare():
    validate_root()
    if (ROOT / 'core').exists():
        raise RuntimeError('Candidate already has sources; inspect before continuing.')
    os.umask(0o077)
    for rel in ['evidence', 'private', 'tools', 'server', 'build']:
        (ROOT / rel).mkdir(exist_ok=True)
    source_hashes = json.loads((OLD / 'deployment-20260913/prepared.json').read_text())['fileHashes']
    baseline = {p: digest(p) for p in source_hashes}
    (ROOT / 'private/baseline.json').write_text(json.dumps(baseline, indent=2))
    shutil.copytree(OLD / 'server/etc', ROOT / 'private/production-etc')
    clone('core', ROOT / 'core')
    run(['git', '-C', ROOT / 'core', 'apply', '--check', ROOT / 'inputs/core-atlas.patch'])
    run(['git', '-C', ROOT / 'core', 'apply', ROOT / 'inputs/core-atlas.patch'])
    modules = ROOT / 'core/modules'
    for name in ['playerbots', 'dungeon-clear', 'ah-bot']:
        clone(name, modules / ('mod-' + name))
    run(['git', '-C', modules / 'mod-playerbots', 'apply', '--check', ROOT / 'inputs/playerbots-atlas.patch'])
    run(['git', '-C', modules / 'mod-playerbots', 'apply', ROOT / 'inputs/playerbots-atlas.patch'])
    preserved = {}
    for name in ['mod-transmog', 'mod-account-achievements', 'mod-atlas-armory', 'mod-atlas-friends',
                 'mod-atlas-chat', 'mod-atlas-shop']:
        src = OLD / 'core/modules' / name
        shutil.copytree(src, modules / name, ignore=shutil.ignore_patterns('.git', '__pycache__'))
        preserved[name] = {str(p.relative_to(src)): digest(p) for p in src.rglob('*') if p.is_file()}
    # AH Bot's Atlas owner policy is in a file untouched by the upstream update.
    ah = 'src/AuctionHouseBotAuctionHouseScript.cpp'
    shutil.copy2(OLD / 'core/modules/mod-ah-bot' / ah, modules / 'mod-ah-bot' / ah)
    preserved['mod-ah-bot'] = {ah: digest(modules / 'mod-ah-bot' / ah)}
    (ROOT / 'evidence/preserved-modules.json').write_text(json.dumps(preserved, indent=2))
    for name, path in [('core', ROOT/'core'), ('playerbots', modules/'mod-playerbots'), ('ah-bot', modules/'mod-ah-bot')]:
        run(['git', '-C', path, 'add', '-u'])
        if name == 'core':
            run(['git', '-C', path, 'add', 'src/test/server/game/Entities/CorpseReclaimDelayTest.cpp'])
        run(['git', '-C', path, 'commit', '-m', 'custom(Atlas): Preserve deployed integration on September 26 upstream'])
    (ROOT / 'evidence/pins.json').write_text(json.dumps(PINS, indent=2))
    verify()
    print('PREPARED inactive sources; production unchanged', flush=True)


def verify():
    validate_root()
    baseline = json.loads((ROOT / 'private/baseline.json').read_text())
    changed = [p for p, expected in baseline.items() if digest(p) != expected]
    if changed:
        raise RuntimeError('Production file changed: ' + repr(changed))
    print('PRESERVED', len(baseline), 'production files', flush=True)


def build():
    verify()
    if (ROOT/'evidence/build-success.json').exists():
        raise RuntimeError('Preserve the validated build and inspect before rebuilding.')
    (ROOT / 'tools/build-home').mkdir(exist_ok=True)
    env = {**os.environ, 'HOME': str(ROOT/'tools/build-home'), 'TMPDIR': str(ROOT/'build'),
           'CCACHE_DISABLE': '1'}
    configure = ['cmake', '-S', ROOT/'core', '-B', ROOT/'build', '-DCMAKE_BUILD_TYPE=RelWithDebInfo',
                 '-DCMAKE_INSTALL_PREFIX='+str(ROOT/'server'), '-DSCRIPTS=static', '-DMODULES=static',
                 '-DBUILD_TESTING=ON', '-DTOOLS=0', '-DWITH_WARNINGS=1']
    (ROOT/'evidence/configure-command.json').write_text(json.dumps([str(x) for x in configure],indent=2))
    for name, command in [('configure', configure), ('build', ['cmake','--build',ROOT/'build','--parallel','6'])]:
        print('START',name,flush=True)
        with (ROOT/'evidence'/f'{name}.log').open('w') as log:
            run(command, env=env, stdout=log, stderr=subprocess.STDOUT)
        print('PASS',name,flush=True)
    verify()
    (ROOT/'evidence/build-success.json').write_text(json.dumps({'passed':True,
        'completedAt':datetime.datetime.now(datetime.timezone.utc).isoformat()}))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('phase', choices=['prepare','build','verify'])
    phase = p.parse_args().phase
    globals()[phase]()
