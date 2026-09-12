#!/usr/bin/env python3
"""Read the real HTTPS shop using a short-lived synthetic account, with no orders."""
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import sys
import time
import urllib.error
import urllib.request
from release_runtime import ROOT, query, verify_runtime

def main():
    os.umask(0o077)
    if os.geteuid() != 0 or sys.argv[1:] not in (['closed'], ['enabled'], ['published']): raise RuntimeError('Expected a live check phase')
    phase = sys.argv[1]
    runtime = verify_runtime(phase != 'closed')
    original_dns = socket.getaddrinfo
    socket.getaddrinfo = lambda host, port, family=0, type=0, proto=0, flags=0: original_dns(
        host, port, socket.AF_INET if host == 'animeclub.fr' else family, type, proto, flags)
    username = 'ATLASREL' + secrets.token_hex(4).upper()
    email = username.lower() + '@example.invalid'
    token = 'atl_access-' + base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip('=')
    token_hash = hashlib.sha256(token.encode()).hexdigest()
    session = secrets.token_hex(16)
    identity = ROOT / ('canary-' + phase + '.json')
    if identity.exists(): raise RuntimeError('Canary record exists; inspect before retry')
    identity.write_text(json.dumps({'username': username, 'sessionId': session, 'cleaned': False}) + '\n')
    account_id = None
    report = {'phase': phase, 'passed': False, 'realOrdersCreated': 0, 'runtime': runtime}
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, fp, code, message, headers, newurl):
            return None
    opener = urllib.request.build_opener(NoRedirect)
    def fetch(path, authenticated=True):
        headers = {'Accept': 'application/json', 'Cache-Control': 'no-cache'}
        if authenticated: headers['Authorization'] = 'Bearer ' + token
        request = urllib.request.Request('https://animeclub.fr/wotlk/' + path, headers=headers)
        with opener.open(request, timeout=20) as response:
            if response.status != 200: raise RuntimeError('Unexpected API status')
            data = response.read(1024 * 1024 + 1)
            if len(data) > 1024 * 1024: raise RuntimeError('Unexpected API response size')
            return json.loads(data)
    try:
        rows = query("START TRANSACTION; INSERT INTO arthas_auth.account(username,salt,verifier,email,reg_mail,joindate,expansion) "
            f"VALUES('{username}',UNHEX('{secrets.token_hex(32)}'),UNHEX('{secrets.token_hex(32)}'),'{email}','{email}',UTC_TIMESTAMP(),2); "
            "SET @canary=LAST_INSERT_ID(); INSERT INTO arthas_auth.atlas_launcher_profile(account_id,display_username,email_normalized) "
            f"VALUES(@canary,'{username}','{email}'); INSERT INTO arthas_auth.atlas_launcher_session "
            "(id,account_id,access_hash,refresh_hash,device_name,access_expires_at,refresh_expires_at,absolute_expires_at) "
            f"VALUES(UNHEX('{session}'),@canary,UNHEX('{token_hash}'),UNHEX('{secrets.token_hex(32)}'),'Atlas release 1.7.0 read check',"
            "UTC_TIMESTAMP()+INTERVAL 5 MINUTE,UTC_TIMESTAMP()+INTERVAL 5 MINUTE,UTC_TIMESTAMP()+INTERVAL 5 MINUTE); COMMIT; SELECT @canary;")
        account_id = int(rows[0][0])
        shop = fetch('api/v1/shop')
        if shop.get('schemaVersion') != 2 or shop.get('checkoutAvailable') != (phase != 'closed'): raise RuntimeError('Unexpected shop availability')
        if len(shop['offers']) != 4 or shop['offers'][0]['id'] != 'character-rename': raise RuntimeError('Unexpected shop catalog')
        if shop.get('characters') != [] or shop.get('history') != []: raise RuntimeError('Canary inherited data')
        if shop.get('euroBalanceCents') != 0 or shop.get('creditBalanceEuroCents') != 0: raise RuntimeError('Canary balance is not zero')
        purchases = shop.get('purchases', {})
        if not purchases.get('accountServices') or purchases.get('orders') != []: raise RuntimeError('Native account-service state differs')
        notes = fetch('api/v1/patch-notes')
        # The route returns the historical list envelope used by the launcher.
        entries = notes if isinstance(notes, list) else notes.get('items', notes.get('notes', []))
        expected_id = 'atlas-launcher-1-7-0' if phase == 'published' else 'atlas-launcher-1-6-0'
        if not entries or entries[0]['id'] != expected_id: raise RuntimeError('Unexpected authenticated patch-note feed')
        for path in ('api/v1/shop', 'api/v1/patch-notes'):
            try:
                fetch(path, False)
                raise RuntimeError('Anonymous API request succeeded')
            except urllib.error.HTTPError as error:
                if error.code != 401: raise
        if query(f'SELECT COUNT(*) FROM arthas_auth.atlas_shop_order WHERE account_id={account_id};') != [['0']]:
            raise RuntimeError('Unexpected canary order')
        report.update(passed=True, httpsAuthenticatedShop=True, checkoutAvailable=shop['checkoutAvailable'],
            accountServices=True, independentZeroBalances=True, emptyHistory=True, authenticatedPatchNotes=expected_id,
            anonymousRequestsRejected=True, completedAtUnix=int(time.time()))
    finally:
        owned = query(f"SELECT id FROM arthas_auth.account WHERE username='{username}' AND email='{email}';")
        if owned:
            if len(owned) != 1 or (account_id is not None and int(owned[0][0]) != account_id): raise RuntimeError('Canary identity changed')
            owned_id = int(owned[0][0])
            if query(f'SELECT COUNT(*) FROM arthas_auth.atlas_shop_order WHERE account_id={owned_id};') != [['0']]: raise RuntimeError('Refuse deleting a canary with an order')
            query(f"DELETE FROM arthas_auth.account WHERE id={owned_id} AND username='{username}' AND email='{email}';")
        if query(f"SELECT COUNT(*) FROM arthas_auth.account WHERE username='{username}';") != [['0']]: raise RuntimeError('Canary cleanup failed')
        identity.write_text(json.dumps({'username': username, 'sessionId': session, 'cleaned': True}) + '\n')
        report['canaryRemoved'] = True
        (ROOT / ('live-check-' + phase + '.json')).write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({key: value for key, value in report.items() if key != 'runtime'}), flush=True)

if __name__ == '__main__': main()
