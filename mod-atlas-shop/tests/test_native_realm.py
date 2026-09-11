#!/usr/bin/env python3
"""Exercise the real launcher API and native 3.3.5 world handlers in a fixture.

Uses fresh synthetic accounts and disposable balances. It provisions a test
session key in that account (authserver/SRP and Hermes are outside this test).
No delivery heartbeat, receipt or rename consumption is simulated here.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import select
import socket
import struct
import subprocess
import time
import urllib.error
import urllib.request
import uuid


class Arc4:
    def __init__(self, key):
        self.s = list(range(256))
        j = 0
        for i in range(256):
            j = (j + self.s[i] + key[i % len(key)]) & 255
            self.s[i], self.s[j] = self.s[j], self.s[i]
        self.i = self.j = 0
        self.apply(bytes(1024))

    def apply(self, data):
        result = bytearray()
        for value in data:
            self.i = (self.i + 1) & 255
            self.j = (self.j + self.s[self.i]) & 255
            self.s[self.i], self.s[self.j] = self.s[self.j], self.s[self.i]
            result.append(value ^ self.s[(self.s[self.i] + self.s[self.j]) & 255])
        return bytes(result)


class WorldClient:
    """Bounded fixture client derived from WorldSocket.cpp and AuthCrypt.cpp."""
    def __init__(self, account, key, wait_auth=True):
        self.socket = socket.create_connection(('127.0.0.1', 14001), timeout=20)
        self.encrypt = self.decrypt = None
        self.auth_result = None
        opcode, challenge = self.receive()
        if opcode != 0x1EC or len(challenge) != 40:
            raise RuntimeError('Expected native SMSG_AUTH_CHALLENGE.')
        seed = secrets.token_bytes(4)
        digest = hashlib.sha1(account.encode() + bytes(4) + seed + challenge[4:8] + key).digest()
        payload = struct.pack('<II', 12340, 0) + account.encode() + b'\0'
        payload += struct.pack('<I', 0) + seed + struct.pack('<IIIQ', 0, 0, 1, 0) + digest + bytes(4)
        self.send(0x1ED, payload)
        self.encrypt = Arc4(hmac.new(bytes.fromhex('c2b3723cc6aed9b5343c53ee2f4367ce'), key, hashlib.sha1).digest())
        self.decrypt = Arc4(hmac.new(bytes.fromhex('cc98ae04e897eaca12ddc09342915357'), key, hashlib.sha1).digest())
        if wait_auth:
            self.complete_auth()

    def complete_auth(self):
        reply = self.auth_result or self.until(0x1EE)
        if not reply or reply[0] != 0x0C:
            self.close()
            raise RuntimeError('Native world authentication failed: ' + reply.hex())

    def exact(self, size):
        data = bytearray()
        while len(data) < size:
            part = self.socket.recv(size - len(data))
            if not part:
                raise RuntimeError('World socket closed before the expected response.')
            data += part
        return bytes(data)

    def receive(self):
        first = self.exact(1)
        if self.decrypt:
            first = self.decrypt.apply(first)
        rest = self.exact(4 if first[0] & 0x80 else 3)
        if self.decrypt:
            rest = self.decrypt.apply(rest)
        header = first + rest
        size = int.from_bytes(bytes([header[0] & 0x7F]) + header[1:-2], 'big')
        if size < 2 or size > 1024 * 1024:
            raise RuntimeError('Invalid native server packet size.')
        opcode, body = int.from_bytes(header[-2:], 'little'), self.exact(size - 2)
        if opcode == 0x1EE:
            self.auth_result = body
        return opcode, body

    def send(self, opcode, payload=b''):
        header = struct.pack('>H', len(payload) + 4) + struct.pack('<I', opcode)
        if self.encrypt:
            header = self.encrypt.apply(header)
        self.socket.sendall(header + payload)

    def until(self, expected):
        for _ in range(200):
            opcode, body = self.receive()
            if opcode == expected:
                return body
        raise RuntimeError('Expected native opcode not received: ' + hex(expected))

    def create(self, name):
        self.send(0x036, name.encode() + b'\0' + bytes([1, 1, 0, 0, 0, 0, 0, 0, 0]))
        return self.until(0x03A)[0]

    def rename(self, guid, name):
        self.send(0x2C7, struct.pack('<Q', guid) + name.encode() + b'\0')
        return self.until(0x2C8)[0]

    def delete(self, guid):
        self.send(0x038, struct.pack('<Q', guid))
        return self.until(0x03C)[0]

    def characters(self):
        self.send(0x037)
        data = self.until(0x03B)
        pos = 1
        result = []
        for _ in range(data[0]):
            guid = struct.unpack_from('<Q', data, pos)[0]
            pos += 8
            end = data.index(0, pos)
            name = data[pos:end].decode()
            pos = end + 1
            # Race/class/gender, five appearance bytes, level; zone/map/xyz/guild.
            pos += 9 + 4 + 4 + 12 + 4
            flags = struct.unpack_from('<I', data, pos)[0]
            pos += 4 + 4 + 1 + 12 + 23 * 9
            result.append({'guid': guid, 'name': name, 'flags': flags})
        if pos != len(data):
            raise RuntimeError('Native character enumeration layout differs from the pinned core.')
        return result

    def close(self):
        self.socket.close()


def run(root, restart_world=None):
    root = Path(root).resolve(strict=True)
    if root.parent != Path('/opt/atlas-shop-tests') or not root.name.startswith('rename-'):
        raise RuntimeError('Only disposable Atlas shop test fixtures are accepted.')
    if os.readlink('/proc/self/ns/net') == os.readlink('/proc/1/ns/net'):
        raise RuntimeError('The native test must run in the private fixture network.')
    checks = []
    sockets = []
    token = None

    def check(condition, description):
        if not condition:
            raise AssertionError(description)
        checks.append(description)
        print('PASS', description, flush=True)

    def sql(query):
        return subprocess.check_output(['mysql', '--defaults-extra-file=' + str(root / 'mysql-client.cnf'),
                                        '-NBe', query], text=True, stderr=subprocess.PIPE, timeout=30).strip()

    def http(path, data=None, expected=200):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        request = urllib.request.Request('http://127.0.0.1:18081/api/v1/' + path,
            data=None if data is None else json.dumps(data).encode(), headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                status, body = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, body = error.code, error.read()
        if status != expected:
            raise RuntimeError('HTTP ' + path + ': expected ' + str(expected) + ', got ' + str(status) + ': ' + body.decode()[:500])
        return json.loads(body) if body else None

    def wait_for(fn, timeout=30):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            result = fn()
            if result:
                return result
            time.sleep(0.25)
        raise AssertionError('Timed out waiting for fixture state.')

    def connect(wait_auth=True):
        client = WorldClient(username, key, wait_auth=wait_auth)
        sockets.append(client)
        return client

    def character(guid):
        row = sql('SELECT name,at_login FROM shop_test_chars.characters WHERE guid=' + str(guid) + ';').split('\t')
        return row[0], int(row[1])

    def state(order):
        return sql("SELECT status FROM shop_test_auth.atlas_shop_order WHERE id='" + order['id'] + "';")

    def balances():
        return tuple(map(int, sql('SELECT euro_cents,credit_cents FROM shop_test_auth.atlas_shop_wallet WHERE account_id=' + str(account) + ';').split('\t')))

    def order_input(guid, currency='eur'):
        return {'idempotencyKey': uuid.uuid4().hex, 'offerId': 'character-rename', 'characterGuid': guid,
                'currency': currency, 'expectedAmountCents': 500 if currency == 'eur' else 700,
                'catalogRevision': revision}

    def random_name(prefix):
        return prefix + ''.join(secrets.choice('abcdefghijklmnopqrstuvwxyz') for _ in range(6))

    try:
        wait_for(lambda: '(worldserver-daemon) ready...' in (root / 'logs/world-console.log').read_text(errors='replace')
            and sql('SELECT COUNT(*) FROM shop_test_auth.atlas_shop_delivery_health WHERE realm_id=1 AND protocol=1 AND last_seen_at>UTC_TIMESTAMP()-INTERVAL 15 SECOND;') == '1', timeout=600)
        check(True, 'Real world module emits a fresh protocol-1 delivery heartbeat.')
        username = 'SHOP' + secrets.token_hex(4).upper()
        response = http('accounts', {'username': username, 'password': secrets.token_hex(12), 'email': username.lower() + '@example.invalid'})
        token, account = response['accessToken'], response['profile']['accountId']
        check(account > 0, 'Real API registration creates a fresh synthetic account and authenticated session.')
        key = secrets.token_bytes(40)
        sql("UPDATE shop_test_auth.account SET session_key=UNHEX('" + key.hex() + "'),expansion=2,os='Win',last_ip='127.0.0.1' WHERE id=" + str(account) + ';')
        game = connect()
        check(True, 'Native world socket authentication succeeds with that fixture account.')
        name, other = random_name('El'), random_name('Lu')
        check(game.create(name) == 0x2F and game.create(other) == 0x2F, 'Two characters are created by native CMSG_CHAR_CREATE handlers.')
        native = game.characters()
        guid = next(c['guid'] for c in native if c['name'].lower() == name.lower())
        other_guid = next(c['guid'] for c in native if c['name'].lower() == other.lower())
        snapshot = http('shop')
        revision = snapshot['catalogRevision']
        check(snapshot['checkoutAvailable'] and snapshot['purchases']['renameAvailable'], 'API opens rename checkout only with the actual module heartbeat.')
        check(set(c['guid'] for c in snapshot['characters']) == {guid, other_guid}, 'Launcher API discovers both native-created characters.')
        # Synthetic funds and an unrelated entitlement, never real money or player data.
        sql('INSERT INTO shop_test_auth.atlas_shop_wallet(account_id,euro_cents,credit_cents,updated_at) VALUES(' + str(account)
            + ',10000,10000,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE euro_cents=10000,credit_cents=10000;')
        sql('UPDATE shop_test_chars.characters SET at_login=at_login|8 WHERE guid=' + str(guid) + ';')
        original_flags = character(guid)[1]
        attempt = order_input(guid)
        with ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(lambda _: http('shop/orders', attempt), range(4)))
        order = results[0]
        check(all(o['id'] == order['id'] for o in results) and balances() == (9500, 10000), 'Four concurrent API retries debit the euro wallet exactly once.')
        time.sleep(3)
        check(state(order) == 'pending' and character(guid)[1] == original_flags, 'Delivery waits while the real account is still at character selection.')
        cancelled = http('shop/orders', order_input(other_guid, 'credits'))
        check(balances() == (9500, 9300), 'A credit purchase affects only the independent credit balance.')
        http('shop/orders/' + cancelled['id'] + '/cancel', {})
        http('shop/orders/' + cancelled['id'] + '/cancel', {})
        check(state(cancelled) == 'refunded' and balances() == (9500, 10000), 'Cancelling a pending order refunds its currency exactly once.')
        if restart_world:
            restart_world()
            check(state(order) in ('pending', 'delivered') and balances() == (9500, 10000),
                  'A pending purchase survives an actual world restart without a second debit.')
        game.close()
        wait_for(lambda: state(order) == 'delivered')
        check(character(guid)[1] == original_flags | 1, 'Actual module delivery grants AT_LOGIN_RENAME and preserves other flags.')
        game = connect()
        check(next(c for c in game.characters() if c['guid'] == guid)['flags'] & 0x4000, 'SMSG_CHAR_ENUM advertises the rename entitlement to a native client.')
        check(game.rename(guid, 'Invalid123') != 0 and character(guid)[1] == original_flags | 1, 'Native invalid-name rejection does not consume the entitlement.')
        renamed = random_name('Se')
        check(game.rename(guid, renamed) == 0, 'Native CMSG_CHAR_RENAME accepts the chosen valid name.')
        wait_for(lambda: character(guid) == (renamed.capitalize(), original_flags))
        check(True, 'The core persists the new name and consumes only the rename bit.')
        check(game.rename(guid, random_name('Re')) != 0, 'Native replay cannot rename the same character a second time.')
        check(http('shop/orders', attempt)['id'] == order['id'] and balances() == (9500, 10000), 'Replaying the delivered API request does not charge again.')
        game.close()
        time.sleep(3)
        check(character(guid)[1] == original_flags and state(order) == 'delivered', 'The real worker does not regrant a consumed delivered order.')
        if restart_world:
            restart_world()
            time.sleep(3)
            check(character(guid)[1] == original_flags and state(order) == 'delivered',
                  'A second actual world restart cannot regrant the consumed service.')
        game = connect()
        credit_order = http('shop/orders', order_input(guid, 'credits'))
        game.close()
        wait_for(lambda: state(credit_order) == 'delivered')
        game = connect()
        check(game.rename(guid, random_name('Ny')) == 0 and balances() == (9500, 9300), 'A second purchase using credits is delivered and consumed natively.')
        deleted_order = http('shop/orders', order_input(other_guid))
        check(game.delete(other_guid) == 0x47, 'Native character deletion succeeds while its purchase is still pending.')
        game.close()
        wait_for(lambda: state(deleted_order) == 'refunded')
        check(balances() == (9500, 9300), 'Real delivery rejection and API background worker refund a deleted beneficiary.')
        check(len(http('shop')['purchases']['orders']) == 4, 'The API history retains delivered, cancelled and rejected/refunded orders.')

        # Hold the target row in this isolated MySQL to observe actual ordering
        # between delivery and reconnect. SQL, hooks and packet replies stay real.
        game = connect()
        guard_order = http('shop/orders', order_input(guid))
        lock_name = 'atlas_shop_guard_' + uuid.uuid4().hex
        lock_sql = 'START TRANSACTION; SELECT guid FROM shop_test_chars.characters WHERE guid=' + str(guid)
        lock_sql += " FOR UPDATE; SELECT GET_LOCK('" + lock_name + "',0); DO SLEEP(20); ROLLBACK;"
        locker = subprocess.Popen(['mysql', '--defaults-extra-file=' + str(root / 'mysql-client.cnf'), '-NBe', lock_sql],
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        connection_id = None
        early_reply = None
        guarded_name = random_name('Ga')
        before_guard = character(guid)
        try:
            wait_for(lambda: sql("SELECT IS_USED_LOCK('" + lock_name + "') IS NOT NULL;") == '1')
            connection_id = int(sql("SELECT IS_USED_LOCK('" + lock_name + "');"))
            game.close()
            wait_for(lambda: int(sql("SELECT COUNT(*) FROM information_schema.innodb_trx WHERE trx_state='LOCK WAIT' AND trx_query LIKE '%atlas_shop_order%';")) > 0)
            # This core also queues final session initialization behind delivery.
            # Pipeline a request early and verify the safety invariant regardless
            # of whether the core defers it or the module explicitly rejects it.
            game = connect(wait_auth=False)
            wait_for(lambda: sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account) + ';') == '1')
            game.send(0x2C7, struct.pack('<Q', guid) + guarded_name.encode() + b'\0')
            deadline = time.monotonic() + 1
            while time.monotonic() < deadline and select.select([game.socket], [], [], max(0, deadline - time.monotonic()))[0]:
                opcode, body = game.receive()
                if opcode == 0x2C8:
                    early_reply = body[0]
                    break
            check(early_reply is None or early_reply != 0, 'An early rename request on a reconnect cannot succeed before the delivery transaction commits.')
            check(state(guard_order) == 'pending' and character(guid) == before_guard,
                  'The blocked transaction has neither delivered a receipt nor changed the character entitlement/name.')
        finally:
            if connection_id is not None and locker.poll() is None:
                sql('KILL CONNECTION ' + str(connection_id) + ';')
            locker.wait(timeout=25)
        wait_for(lambda: state(guard_order) == 'delivered')
        if early_reply is None:
            check(game.until(0x2C8)[0] == 0, 'The deferred native rename succeeds only after delivery commits.')
        else:
            game.complete_auth()
            check(game.rename(guid, guarded_name) == 0, 'The previously rejected native rename succeeds after delivery commits.')
        game.complete_auth()
        wait_for(lambda: character(guid) == (guarded_name.capitalize(), original_flags))
        check(balances() == (9000, 9300), 'The reconnect race consumes exactly the purchased entitlement and charges once.')
        # Exercise a real Player instance and the core's asynchronous logout/save,
        # not only an account sitting at character selection.
        game.characters()
        in_world_order = http('shop/orders', order_input(guid))
        game.send(0x03D, struct.pack('<Q', guid))
        login = game.until(0x236)
        wait_for(lambda: sql('SELECT online FROM shop_test_chars.characters WHERE guid=' + str(guid) + ';') == '1')
        check(len(login) == 20 and struct.unpack_from('<I', login)[0] == 0,
              'The character enters the actual world map and is recorded online.')
        rejected_online = http('shop/orders', order_input(guid), expected=409)
        check(rejected_online['error'] == 'shop-character-online' and balances() == (8500, 9300),
              'A further purchase while the character is in world is rejected without a debit.')
        time.sleep(3)
        check(state(in_world_order) == 'pending' and character(guid)[1] & 1 == 0,
              'A purchase made before player login stays pending while that character is in world.')
        game.send(0x04B)
        # Initial world updates may precede the logout completion. Drain them
        # with a bounded deadline and answer the core time-sync challenge.
        logout_started = time.monotonic()
        logout_response = None
        while time.monotonic() - logout_started < 40:
            opcode, body = game.receive()
            if opcode == 0x390:
                game.send(0x391, body[:4] + struct.pack('<I', int(time.monotonic() * 1000) & 0xFFFFFFFF))
            elif opcode == 0x04C:
                logout_response = body
            elif opcode == 0x04D:
                break
        else:
            raise RuntimeError('Real character logout did not complete.')
        check(logout_response is not None and int.from_bytes(logout_response[:4], 'little') == 0,
              'The core accepts the real CMSG_LOGOUT_REQUEST and completes logout.')
        wait_for(lambda: sql('SELECT online FROM shop_test_chars.characters WHERE guid=' + str(guid) + ';') == '0')
        after_logout = character(guid)
        time.sleep(2)
        check(state(in_world_order) == 'pending' and after_logout[1] & 1 == 0,
              'After player save, delivery still waits for the account session to disconnect.')
        game.close()
        wait_for(lambda: state(in_world_order) == 'delivered')
        check(character(guid) == (after_logout[0], after_logout[1] | 1),
              'Delivery after the actual logout/save grants rename without losing other flags.')
        game = connect()
        check(next(c for c in game.characters() if c['guid'] == guid)['flags'] & 0x4000 != 0,
              'Reconnect exposes the purchased rename after a real in-world session.')
        after_play_name = random_name('Jo')
        check(game.rename(guid, after_play_name) == 0 and balances() == (8500, 9300),
              'The post-play rename succeeds with exactly one debit.')
        wait_for(lambda: character(guid) == (after_play_name.capitalize(), after_logout[1]))
        game.close()
        report = {'passed': True, 'checks': checks, 'checkCount': len(checks), 'accountId': account,
                  'authserverTested': False, 'hermesTested': False, 'graphicalClientTested': False,
                  'realWorldHandlersTested': True, 'playerLoginLogoutTested': True,
                  'worldRestartsTested': restart_world is not None,
                  'completedAtUnix': int(time.time())}
        (root / 'native-test-result.json').write_text(json.dumps(report, indent=2) + '\n')
        print('PASS: ' + str(len(checks)) + ' API/native realm checks.', flush=True)
        return report
    finally:
        for client in sockets:
            client.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    run(parser.parse_args().root)
