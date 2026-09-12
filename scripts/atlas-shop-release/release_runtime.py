"""Shared helpers for the explicitly authorized Atlas 1.7.0 maintenance."""
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from prepare_backend import ROOT, API, HERMES, WORLD, digest

WORLD_SERVICE = 'arthas-worldserver.dungeon-clear-8224099'
SERVICES = (WORLD_SERVICE, 'hermesproxy-wotlk', 'wotlk-launcher-api', 'arthas-authserver')

def run(command, **kwargs):
    return subprocess.run(command, check=True, capture_output=True, text=True, timeout=90, **kwargs).stdout

def service(name):
    return dict(line.split('=', 1) for line in run(['systemctl', 'show', name, '-p',
        'MainPID,ActiveState,NRestarts,WorkingDirectory,ExecMainStartTimestampMonotonic']).strip().splitlines())

def query(sql):
    info = json.loads(run(['docker', 'inspect', 'arthas-mysql']))[0]
    if info['Name'] != '/arthas-mysql': raise RuntimeError('Unexpected database container')
    env = dict(x.split('=', 1) for x in info['Config']['Env'] if '=' in x)
    password = env['MYSQL_ROOT_PASSWORD'].replace('\\', '\\\\').replace('"', '\\"')
    if '\n' in password or '\r' in password: raise RuntimeError('Invalid credential format')
    cnf = '[client]\nuser=root\npassword="' + password + '"\n'
    output = run(['docker', 'exec', '-i', info['Id'], 'mysql', '--defaults-extra-file=/dev/stdin', '-NBe', sql], input=cnf)
    return [line.split('\t') for line in output.strip().splitlines()]

def atomic_copy(source, target, mode=None):
    source, target = Path(source), Path(target)
    if target.parent.resolve(strict=True) != target.parent or target.is_symlink(): raise RuntimeError('Unsafe copy destination')
    previous = target.stat() if target.exists() else None
    fd, name = tempfile.mkstemp(prefix='.' + target.name + '.atlas170-', dir=target.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, 'wb') as output, source.open('rb') as input_file:
            shutil.copyfileobj(input_file, output, 1024 * 1024)
            output.flush(); os.fsync(output.fileno())
        os.chmod(temporary, mode if mode is not None else (previous.st_mode & 0o777 if previous else 0o644))
        if previous: os.chown(temporary, previous.st_uid, previous.st_gid)
        if digest(temporary) != digest(source): raise RuntimeError('Copy hash mismatch')
        os.replace(temporary, target)
        fd = os.open(target.parent, os.O_RDONLY | os.O_DIRECTORY)
        try: os.fsync(fd)
        finally: os.close(fd)
    finally:
        if temporary.exists(): temporary.unlink()

def health():
    try:
        with urllib.request.urlopen('http://127.0.0.1:4323/health', timeout=3) as response:
            return response.status == 200 and json.load(response).get('status') == 'ok'
    except (OSError, ValueError): return False

def world_ready():
    rows = query("SELECT COUNT(*) FROM arthas_auth.atlas_shop_delivery_health WHERE realm_id=1 AND protocol=2 "
        "AND character_database='arthas_chars' AND last_seen_at BETWEEN UTC_TIMESTAMP(6)-INTERVAL 30 SECOND AND UTC_TIMESTAMP(6)+INTERVAL 5 SECOND;")
    if rows != [['1']]: return False
    try:
        with socket.create_connection(('127.0.0.1', 4000), timeout=2): return True
    except OSError: return False

def wait_for(label, check, timeout=180):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if check():
            print('PASS: ' + label, flush=True)
            return
        time.sleep(2)
    raise RuntimeError('Timed out waiting for ' + label)

def verify_runtime(purchases):
    plan = json.loads((ROOT / 'plan.json').read_text())
    expected = {WORLD_SERVICE: (WORLD / 'build/worldserver', plan['worldSha256']),
        'hermesproxy-wotlk': (HERMES / 'HermesProxy', plan['files']['hermes/HermesProxy']),
        'wotlk-launcher-api': (API / 'WotLK.Launcher.Server', plan['files']['api/WotLK.Launcher.Server'])}
    result = {}
    for name, (path, sha) in expected.items():
        state = service(name)
        proc = Path('/proc') / state['MainPID']
        if state['ActiveState'] != 'active' or state['MainPID'] == '0' or (proc / 'exe').resolve() != path:
            raise RuntimeError('Unexpected active service: ' + name)
        if digest(proc / 'exe') != sha: raise RuntimeError('Unexpected active binary: ' + name)
        state.update(executable=str(path), sha256=sha)
        result[name] = state
    auth = service('arthas-authserver')
    if auth['MainPID'] != plan['activeBefore']['services']['arthas-authserver']['MainPID'] or auth['ActiveState'] != 'active':
        raise RuntimeError('Auth process changed')
    result['arthas-authserver'] = auth
    api_pid = result['wotlk-launcher-api']['MainPID']
    environment = dict(x.decode().split('=', 1) for x in (Path('/proc') / api_pid / 'environ').read_bytes().split(b'\0') if b'=' in x)
    flags = {'WOTLK_LAUNCHER_MAX_SCHEMA_VERSION': '13', 'AtlasShop__Purchases__AccountServicesEnabled': 'true',
        'AtlasShop__Purchases__RenameEnabled': 'true' if purchases else 'false'}
    if any(environment.get(key) != value for key, value in flags.items()): raise RuntimeError('API flags differ')
    result['apiFlags'] = flags
    if not health() or not world_ready(): raise RuntimeError('API or World health failed')
    return result
