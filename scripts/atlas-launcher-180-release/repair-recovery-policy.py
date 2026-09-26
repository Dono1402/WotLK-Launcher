#!/usr/bin/env python3
"""Preserve API nonce CSP only on the Atlas password-reset page; graceful reload."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import tempfile
import urllib.request

ROOT = Path('/opt/atlas-launcher-releases/1.8.0-20260926')
CADDY = Path('/etc/caddy/Caddyfile')
os.umask(0o077)
assert os.geteuid() == 0


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def state():
    return subprocess.check_output(['systemctl', 'show', 'caddy', 'wotlk-launcher-api',
        'arthas-worldserver.dungeon-clear-8224099', 'arthas-authserver', 'hermesproxy-wotlk',
        '-p', 'Id,MainPID,ActiveState,ExecMainStartTimestampMonotonic,NRestarts'], text=True)


def request(path):
    with urllib.request.urlopen('https://animeclub.fr' + path, timeout=15) as response:
        assert response.status == 200
        return dict(response.headers), response.read()


def replace(source):
    original = CADDY.stat()
    assert CADDY.resolve(strict=True) == CADDY
    fd, temporary = tempfile.mkstemp(prefix='.atlas-recovery-policy-', dir=CADDY.parent)
    try:
        with os.fdopen(fd, 'wb') as out:
            out.write(source.read_bytes()); out.flush(); os.fsync(out.fileno())
        os.chmod(temporary, original.st_mode & 0o777)
        os.chown(temporary, original.st_uid, original.st_gid)
        os.replace(temporary, CADDY)
    finally:
        if os.path.exists(temporary): os.unlink(temporary)


plan = json.loads((ROOT / 'api/plan.json').read_text())
before_hash = plan['configuration'][str(CADDY)]
assert sha(CADDY) == before_hash and not (ROOT / 'api/caddy-recovery-policy.json').exists()
original = CADDY.read_text()
assert '@atlas_default_page_policy' not in original
csp = re.findall(r'^\t\tContent-Security-Policy .+$', original, re.M)
referrer = re.findall(r'^\t\tReferrer-Policy .+$', original, re.M)
assert len(csp) == len(referrer) == 1
replacement = original.replace(csp[0] + '\n', '').replace(referrer[0] + '\n', '')
policy = ('\t# The reset page provides its own nonce-based CSP and no-referrer policy.\n'
    '\t@atlas_default_page_policy not path /wotlk/api/v1/auth/password-reset\n'
    '\theader @atlas_default_page_policy {\n' + csp[0] + '\n' + referrer[0] + '\n\t}\n\n')
assert replacement.count('\t@sensitive_spa_paths ') == 1
replacement = replacement.replace('\t@sensitive_spa_paths ', policy + '\t@sensitive_spa_paths ', 1)
candidate = ROOT / 'api/Caddyfile.recovery-policy'
candidate.write_text(replacement)
subprocess.run(['caddy', 'validate', '--adapter', 'caddyfile', '--config', str(candidate)], check=True,
    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
resolve = socket.getaddrinfo
socket.getaddrinfo = lambda host, port, family=0, type=0, proto=0, flags=0: resolve(
    host, port, socket.AF_INET if host == 'animeclub.fr' else family, type, proto, flags)
before_services = state()
assert before_services.count('ActiveState=active') == 5
other_headers, _ = request('/')
_, old_manifest = request('/wotlk/launcher/launcher-update.json')
backup = ROOT / 'api/before/etc/caddy/Caddyfile'
assert sha(backup) == before_hash
try:
    replace(candidate)
    subprocess.run(['systemctl', 'reload', 'caddy'], check=True)
    headers, body = request('/wotlk/api/v1/auth/password-reset')
    nonce = re.search(rb'<script nonce="([^"]+)"', body).group(1).decode()
    assert "'nonce-" + nonce + "'" in headers['Content-Security-Policy']
    assert headers['Referrer-Policy'] == 'no-referrer'
    assert 'no-store' in headers['Cache-Control']
    new_other_headers, _ = request('/')
    for name in ['Content-Security-Policy', 'Referrer-Policy', 'Strict-Transport-Security', 'X-Frame-Options']:
        assert new_other_headers[name] == other_headers[name]
    assert request('/wotlk/launcher/launcher-update.json')[1] == old_manifest
    assert state() == before_services
except BaseException:
    replace(backup)
    subprocess.run(['systemctl', 'reload', 'caddy'], check=True)
    raise
report = dict(verified=True, beforeSha256=before_hash, afterSha256=sha(CADDY),
    scope='/wotlk/api/v1/auth/password-reset', otherPagePolicyUnchanged=True,
    allServiceProcessesUnchanged=True, manifestUnchanged=True, nonceMatchesBody=True)
(ROOT / 'api/caddy-recovery-policy.json').write_text(json.dumps(report, indent=2) + '\n')
# Amend the client publication baseline explicitly, preserving the original proof.
proof_path = ROOT / 'client/preparation.json'
proof = json.loads(proof_path.read_text())
assert proof['baseline'][str(CADDY)]['sha256'] == before_hash
shutil.copyfile(proof_path, ROOT / 'client/preparation.before-recovery-policy.json')
shutil.copyfile(CADDY, ROOT / 'client/before/etc/caddy/Caddyfile')
proof['baseline'][str(CADDY)]['sha256'] = report['afterSha256']
proof['caddyPolicyRepair'] = report
proof_path.write_text(json.dumps(proof, indent=2) + '\n')
print('PASS: reset page nonce CSP preserved; other page policies, manifest and all service processes unchanged.')
