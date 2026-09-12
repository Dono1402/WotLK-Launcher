#!/usr/bin/env python3
"""Test real account-service purchases and the native core consumer in isolation.

No receipt, heartbeat or name-change result is fabricated. Synthetic accounts,
funds and minimum levels are provisioned only in the dedicated MySQL fixture.
"""
import argparse
from collections import deque
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import secrets
import select
import struct
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from test_native_realm import WorldClient

MAGIC = b'\xffATLS\x02'


class ServiceClient(WorldClient):
    def __init__(self, username, key):
        self.replies = deque()
        self.nonce = secrets.randbits(64) or 1
        self.serial = 0
        self.last_service = 0
        super().__init__(username, key)

    def until(self, expected):
        for _ in range(2000):
            opcode, body = self.receive()
            if opcode == 0x03b and body.startswith(MAGIC):
                self.replies.append(self.decode(body))
            elif opcode == 0x390:
                self.send(0x391, body[:4] + struct.pack('<I', int(time.monotonic() * 1000) & 0xffffffff))
            elif opcode == expected:
                return body
        raise RuntimeError('Expected native opcode not received: ' + hex(expected))

    def decode(self, body):
        kind, request, nonce, error = struct.unpack_from('<BIQB', body, 6)
        if nonce != self.nonce or error > 1:
            raise RuntimeError('Invalid native extension correlation.')
        if kind == 1:
            count = struct.unpack_from('<I', body, 20)[0]
            if count > 100 or len(body) != 24 + 8 * count:
                raise RuntimeError('Invalid native distribution list size.')
            return {'kind': kind, 'request': request, 'error': error,
                    'ids': list(struct.unpack_from('<' + 'Q' * count, body, 24))}
        if kind != 2 or len(body) < 38:
            raise RuntimeError('Unknown native response.')
        distribution, guid, token, validate, length = struct.unpack_from('<QIIBB', body, 20)
        if len(body) != 38 + length:
            raise RuntimeError('Invalid native assignment response size.')
        return {'kind': kind, 'request': request, 'error': error, 'distribution': distribution,
                'guid': guid, 'token': token, 'validate': validate, 'name': body[38:].decode('utf-8')}

    def service_until(self, kind, request, timeout=20):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for reply in list(self.replies):
                if reply['kind'] == kind and reply['request'] == request:
                    self.replies.remove(reply)
                    return reply
            opcode, body = self.receive()
            if opcode == 0x03b and body.startswith(MAGIC):
                self.replies.append(self.decode(body))
            elif opcode == 0x390:
                self.send(0x391, body[:4] + struct.pack('<I', int(time.monotonic() * 1000) & 0xffffffff))
        raise RuntimeError('Native service response timed out.')

    def services(self):
        self.pace()
        self.serial += 1
        self.send(0x037, MAGIC + struct.pack('<BIQ', 1, self.serial, self.nonce))
        return self.service_until(1, self.serial)

    def send_assignment(self, distribution, guid, name, validate=False):
        self.pace()
        self.serial += 1
        token = secrets.randbits(32)
        encoded = name.encode('utf-8')
        self.send(0x037, MAGIC + struct.pack('<BIQQIIBB', 2, self.serial, self.nonce,
            distribution, guid, token, int(validate), len(encoded)) + encoded)
        return self.serial, token

    def pace(self):
        delay = 0.55 - (time.monotonic() - self.last_service)
        if delay > 0: time.sleep(delay)
        self.last_service = time.monotonic()

    def assign(self, distribution, guid, name, validate=False):
        request, token = self.send_assignment(distribution, guid, name, validate)
        reply = self.service_until(2, request)
        if reply['token'] != token or reply['distribution'] != distribution or reply['guid'] != guid or reply['validate'] != int(validate):
            raise RuntimeError('Native response changed the assignment identity.')
        return reply


class Fixture:
    def __init__(self, root, report='native-account-services-result.json'):
        self.root = Path(root).resolve(strict=True)
        if self.root != Path('/opt/atlas-shop-tests/rename-20260911') or os.readlink('/proc/self/ns/net') == os.readlink('/proc/1/ns/net'):
            raise RuntimeError('Expected the dedicated network-isolated test fixture.')
        self.path = self.root / report
        self.checks, self.clients = [], []
        self.save(False, 'starting')

    def save(self, passed, state):
        self.path.write_text(json.dumps({'passed': passed, 'state': state, 'checks': self.checks,
            'characterDatabaseWorkers': 4, 'realCoreConsumer': True, 'realApiAndMySql': True,
            'graphicalClientTested': False}, indent=2) + '\n')

    def check(self, value, message):
        if not value:
            raise AssertionError(message)
        self.checks.append(message)
        self.save(False, 'running')
        print('PASS', message, flush=True)

    def sql(self, query):
        return subprocess.check_output(['mysql', '--defaults-extra-file=' + str(self.root / 'mysql-client.cnf'),
            '-NBe', query], text=True, stderr=subprocess.PIPE, timeout=30).strip()

    def http(self, account, path, body=None, expected=200):
        headers = {'Content-Type': 'application/json'}
        if account:
            headers['Authorization'] = 'Bearer ' + account['token']
        request = urllib.request.Request('http://127.0.0.1:18081/api/v1/' + path,
            data=None if body is None else json.dumps(body).encode(), headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                status, data = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, data = error.code, error.read()
        if status not in ((expected,) if isinstance(expected, int) else expected):
            raise RuntimeError('Unexpected HTTP ' + str(status) + ' for ' + path + ': ' + data.decode()[:400])
        result = json.loads(data) if data else None
        return result if isinstance(expected, int) else (status, result)

    def wait(self, predicate, timeout=30):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            value = predicate()
            if value:
                return value
            time.sleep(0.15)
        raise RuntimeError('Fixture condition did not become true in ' + str(timeout) + ' seconds.')

    @staticmethod
    def name(prefix='Na'):
        return prefix + ''.join(secrets.choice('abcdefghijklmnopqrstuvwxyz') for _ in range(6))

    def register(self, euro_cents=100000, credit_cents=100000):
        username = 'NATIVE' + secrets.token_hex(4).upper()
        result = self.http(None, 'accounts', {'username': username, 'password': secrets.token_hex(16),
            'email': username.lower() + '@example.invalid'})
        account = {'id': result['profile']['accountId'], 'token': result['accessToken'], 'username': username, 'key': secrets.token_bytes(40)}
        self.sql("UPDATE shop_test_auth.account SET session_key=UNHEX('" + account['key'].hex()
            + "'),expansion=2,os='Win',last_ip='127.0.0.1' WHERE id=" + str(account['id']))
        self.sql('INSERT INTO shop_test_auth.atlas_shop_wallet(account_id,euro_cents,credit_cents,updated_at) VALUES('
            + str(account['id']) + ',' + str(int(euro_cents)) + ',' + str(int(credit_cents)) + ',UTC_TIMESTAMP(6))')
        return account

    def connect(self, account):
        client = ServiceClient(account['username'], account['key'])
        self.clients.append(client)
        return client

    def buy(self, account, currency='eur'):
        snapshot = self.http(account, 'shop')
        body = {'idempotencyKey': uuid.uuid4().hex, 'offerId': 'character-rename', 'characterGuid': 0,
            'currency': currency, 'expectedAmountCents': 500 if currency == 'eur' else 700,
            'catalogRevision': snapshot['catalogRevision']}
        order = self.http(account, 'shop/orders', body)
        order['sequence'] = int(self.sql("SELECT sequence_id FROM shop_test_auth.atlas_shop_order WHERE id='" + order['id'] + "'"))
        return order

    def state(self, order):
        return self.sql("SELECT status FROM shop_test_auth.atlas_shop_order WHERE id='" + order['id'] + "'")

    def character(self, guid):
        return self.sql('SELECT name,level,online,at_login FROM shop_test_chars.characters WHERE guid=' + str(guid)).split('\t')

    def balance(self, account):
        return tuple(map(int, self.sql('SELECT euro_cents,credit_cents FROM shop_test_auth.atlas_shop_wallet WHERE account_id='
            + str(account['id'])).split('\t')))

    def close(self):
        for client in self.clients:
            try:
                client.close()
            except OSError:
                pass


class RowLock:
    """Hold one fixture row; the advisory marker proves which connection we own."""
    def __init__(self, fixture, table, key, value):
        if (table, key) not in (('shop_test_auth.atlas_shop_wallet', 'account_id'), ('shop_test_chars.characters', 'guid')):
            raise ValueError('Only the two test row types may be locked.')
        self.fixture = fixture
        self.marker = 'atlas_native_' + uuid.uuid4().hex
        query = 'START TRANSACTION; SELECT ' + key + ' FROM ' + table + ' WHERE ' + key + '=' + str(int(value))
        query += " FOR UPDATE; SELECT GET_LOCK('" + self.marker + "',0); DO SLEEP(90); ROLLBACK;"
        self.process = subprocess.Popen(['mysql', '--defaults-extra-file=' + str(fixture.root / 'mysql-client.cnf'), '-NBe', query],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        fixture.wait(lambda: fixture.sql("SELECT IS_USED_LOCK('" + self.marker + "') IS NOT NULL") == '1')

    def close(self):
        connection = self.fixture.sql("SELECT COALESCE(IS_USED_LOCK('" + self.marker + "'),0)")
        if connection != '0' and self.process.poll() is None:
            self.fixture.sql('KILL CONNECTION ' + str(int(connection)))
        self.process.wait(timeout=10)

    def __enter__(self): return self
    def __exit__(self, *_): self.close()


def refund_races(f, account, game, other, guid, stranger_guid):
    def waiters():
        return int(f.sql("SELECT COUNT(*) FROM information_schema.innodb_trx WHERE trx_state='LOCK WAIT' "
                         "AND trx_query LIKE '%atlas_shop_wallet%'"))

    # Control the first wallet lock in both orderings. All refund and consume
    # writes are still performed by the real API and the real core transaction.
    before_buy = f.balance(account)
    order = f.buy(account)
    with ThreadPoolExecutor(max_workers=1) as pool:
        with RowLock(f, 'shop_test_auth.atlas_shop_wallet', 'account_id', account['id']):
            requested = f.name('Win')
            request, _ = game.send_assignment(order['sequence'], guid, requested)
            f.wait(lambda: waiters() == 1)
            refund = pool.submit(f.http, account, 'shop/orders/' + order['id'] + '/cancel', {}, (200, 409))
            f.wait(lambda: waiters() == 2)
            f.check(other.create(f.name('Busy')) != 0x2f,
                'A competing character creation is temporarily rejected while the native name commit is guarded.')
            game.send(0x03d, struct.pack('<Q', guid))
            f.check(len(game.until(0x041)) == 1 and f.character(guid)[2] == '0',
                'The guarded account cannot load its character before the native transaction completes.')
            other.send(0x03d, struct.pack('<Q', stranger_guid))
            f.check(len(other.until(0x236)) == 20,
                'A different account can enter the actual world while the rename transaction waits on SQL.')
        reply = game.service_until(2, request)
        status, _ = refund.result(timeout=20)
    f.check(reply['error'] == 0 and status == 409 and f.state(order) == 'consumed'
        and f.character(guid)[0] == requested.capitalize() and f.balance(account) == (before_buy[0] - 500, before_buy[1]),
        'When consumption obtains the wallet lock first, it wins once and the concurrent API refund is rejected.')

    before_buy = f.balance(account)
    order = f.buy(account)
    old_name = f.character(guid)[0]
    with ThreadPoolExecutor(max_workers=1) as pool:
        with RowLock(f, 'shop_test_auth.atlas_shop_wallet', 'account_id', account['id']):
            refund = pool.submit(f.http, account, 'shop/orders/' + order['id'] + '/cancel', {}, (200, 409))
            f.wait(lambda: waiters() == 1)
            request, _ = game.send_assignment(order['sequence'], guid, f.name('Lose'))
            f.wait(lambda: waiters() == 2)
        status, _ = refund.result(timeout=20)
        reply = game.service_until(2, request)
    f.check(status == 200 and reply['error'] != 0 and f.state(order) == 'refunded'
        and f.character(guid)[0] == old_name and f.balance(account) == before_buy,
        'When the API refund obtains the wallet lock first, the native confirmation preserves the old name and cannot consume the refunded service.')


def logout_save_race(f, account, game, guid):
    helper = f.connect(f.register())
    game.characters()
    game.send(0x03d, struct.pack('<Q', guid))
    game.until(0x236)
    f.wait(lambda: f.character(guid)[2] == '1')
    order = f.buy(account, 'credits')
    f.check(f.state(order) == 'available' and game.assign(order['sequence'], guid, f.name())['error'] != 0,
        'An account can buy a service while playing, but the core refuses to consume it in world.')
    before = f.character(guid)
    requested = '\u00c9' + f.name('cl').lower()
    with RowLock(f, 'shop_test_chars.characters', 'guid', guid):
        game.send(0x04b)
        f.check(int.from_bytes(game.until(0x04c)[:4], 'little') == 0,
            'The core accepts a real logout while the character row is locked by the fixture.')
        game.until(0x04d)
        f.wait(lambda: int(f.sql("SELECT COUNT(*) FROM information_schema.innodb_trx WHERE trx_state='LOCK WAIT' "
            "AND trx_query LIKE '%characters%'")) >= 1)
        request, _ = game.send_assignment(order['sequence'], guid, requested)
        deadline = time.monotonic() + 0.8
        while time.monotonic() < deadline and select.select([game.socket], [], [], max(0, deadline - time.monotonic()))[0]:
            opcode, body = game.receive()
            if opcode == 0x03b and body.startswith(MAGIC): game.replies.append(game.decode(body))
        f.check(not any(reply['kind'] == 2 and reply['request'] == request for reply in game.replies)
            and f.state(order) == 'available' and f.character(guid)[0] == before[0],
            'Native consumption waits for the real queued logout save before acquiring the name guard.')
        f.check(helper.create(f.name('Free')) == 0x2f,
            'Another account can create a character while a previous logout save is still blocked on SQL.')
    reply = game.service_until(2, request)
    f.check(reply['error'] == 0 and reply['name'] == requested and f.state(order) == 'consumed'
        and f.character(guid)[0] == requested and f.character(guid)[2] == '0',
        'Releasing the real logout save permits one Unicode rename without a later stale-name overwrite.')
    f.check(next(c for c in game.characters() if c['guid'] == guid)['name'] == requested,
        'The normal enumeration exposes the committed Unicode name after the save barrier.')
    game.send(0x03d, struct.pack('<Q', guid))
    game.until(0x236)
    f.wait(lambda: f.character(guid)[2] == '1')
    game.send(0x04b)
    game.until(0x04d)
    f.wait(lambda: f.character(guid)[2] == '0')
    f.check(f.character(guid)[0] == requested,
        'A subsequent actual player login, logout and save preserve the new Unicode name.')
    helper.close()


def run(root, restart_world=None):
    f = Fixture(root)
    trigger = None
    try:
        world_started = int((f.root / 'world.pid').stat().st_mtime)
        f.wait(lambda: '(worldserver-daemon) ready...' in (f.root / 'logs/world-console.log').read_text(errors='replace')
            and f.sql('SELECT COUNT(*) FROM shop_test_auth.atlas_shop_delivery_health WHERE realm_id=1 AND protocol=2 '
                'AND last_seen_at>FROM_UNIXTIME(' + str(world_started) + ') AND last_seen_at>UTC_TIMESTAMP()-INTERVAL 15 SECOND') == '1', timeout=300)
        f.check("Opening DatabasePool 'shop_test_chars'. Asynchronous connections: 4, synchronous connections: 1."
            in (f.root / 'logs/world-console.log').read_text(errors='replace'),
            'The real account consumer opens four asynchronous character database connections and one synchronous connection.')
        f.check(True, 'The real core advertises protocol 2 after schema and transaction-grant checks.')
        account, stranger = f.register(), f.register()
        order = f.buy(account)
        f.check(f.state(order) == 'available' and order['characterGuid'] == 0,
            'The real API sells an unassigned account service before any character exists.')
        game, other = f.connect(account), f.connect(stranger)
        f.check(game.services()['ids'] == [order['sequence']] and other.services()['ids'] == [],
            'Authenticated native subscriptions expose only the current account services.')
        name, young, taken = f.name('Al'), f.name('Mi'), f.name('Zu')
        f.check(game.create(name) == 0x2f and game.create(young) == 0x2f and other.create(taken) == 0x2f,
            'Three synthetic characters are created by the real ordinary core handlers.')
        chars = game.characters()
        guid = next(c['guid'] for c in chars if c['name'].lower() == name.lower())
        young_guid = next(c['guid'] for c in chars if c['name'].lower() == young.lower())
        stranger_guid = other.characters()[0]['guid']
        f.sql('UPDATE shop_test_chars.characters SET level=10 WHERE guid=' + str(guid))
        before = f.character(guid)
        funds = f.balance(account)
        for target, proposed, label in [(guid, 'Invalid123', 'invalid name'), (guid, taken, 'occupied name'),
                (young_guid, f.name(), 'character below level 10'), (stranger_guid, f.name(), 'foreign character')]:
            f.check(game.assign(order['sequence'], target, proposed)['error'] != 0 and f.state(order) == 'available',
                'The core rejects an ' + label + ' without consuming the account service.')
        f.check(other.assign(order['sequence'], stranger_guid, f.name())['error'] != 0,
            'A different authenticated account cannot consume the order by guessing its distribution ID.')
        f.sql('UPDATE shop_test_chars.characters SET at_login=at_login|8 WHERE guid=' + str(guid))
        f.check(game.assign(order['sequence'], guid, f.name())['error'] != 0,
            'An already-pending appearance service prevents a conflicting native rename.')
        f.sql('UPDATE shop_test_chars.characters SET at_login=at_login&~8 WHERE guid=' + str(guid))
        renamed = f.name('Re')
        validation = game.assign(order['sequence'], guid, renamed, True)
        f.check(validation['error'] == 0 and f.state(order) == 'available' and f.character(guid) == before,
            'Native validation succeeds without changing the character or consuming the jeton.')
        confirmation = game.assign(order['sequence'], guid, renamed)
        f.check(confirmation['error'] == 0 and f.state(order) == 'consumed' and f.character(guid)[0] == renamed.capitalize(),
            'Native confirmation atomically renames the character and consumes the paid account service.')
        f.check(f.balance(account) == funds and f.character(guid)[3] == before[3],
            'Consumption neither charges the wallet again nor changes unrelated at-login flags.')
        receipt = next(o for o in f.http(account, 'shop')['purchases']['orders'] if o['id'] == order['id'])
        f.check(receipt['characterGuid'] == guid and receipt['appliedName'] == renamed.capitalize()
            and receipt['characterName'] == name.capitalize(), 'The real API exposes the committed old-to-new name receipt.')
        f.check(next(c for c in game.characters() if c['guid'] == guid)['name'] == renamed.capitalize(),
            'A fresh ordinary character enumeration immediately observes the core cache update.')
        f.check(game.assign(order['sequence'], guid, renamed)['error'] == 0 and f.balance(account) == funds,
            'A repeated confirmation returns its durable receipt without another debit.')
        f.check(game.assign(order['sequence'], guid, f.name())['error'] != 0,
            'A consumed distribution cannot be replayed with a different requested name.')
        f.http(account, 'shop/orders/' + order['id'] + '/cancel', {}, expected=409)
        f.check(True, 'The API refuses to refund an already-consumed native service.')

        cancelled = f.buy(account, 'credits')
        f.http(account, 'shop/orders/' + cancelled['id'] + '/cancel', {})
        f.check(game.assign(cancelled['sequence'], guid, f.name())['error'] != 0 and f.balance(account) == funds,
            'A cancelled credit service cannot rename a character and is refunded exactly once.')

        failing = f.buy(account)
        trigger = 'atlas_native_failure_' + uuid.uuid4().hex
        f.sql('CREATE TRIGGER shop_test_auth.' + trigger + ' BEFORE UPDATE ON shop_test_auth.atlas_shop_order FOR EACH ROW '
            "SET NEW.status=IF(NEW.sequence_id=" + str(failing['sequence']) + " AND NEW.status='consumed','blocked',NEW.status)")
        old = f.character(guid)
        f.check(game.assign(failing['sequence'], guid, f.name())['error'] != 0 and f.state(failing) == 'available'
            and f.character(guid) == old, 'A database failure rolls back both the actual character rename and its receipt.')
        f.sql('DROP TRIGGER shop_test_auth.' + trigger); trigger = None
        f.check(game.assign(failing['sequence'], guid, f.name('Ok'))['error'] == 0,
            'The available service can be retried successfully after the database failure is removed.')
        current = f.character(guid)[0]
        replay = game.assign(order['sequence'], guid, renamed)
        f.check(replay['error'] == 0 and replay['name'] == current and f.character(guid)[0] == current,
            'Replaying an older consumed receipt returns the current identity without overwriting a later legitimate name change.')

        refund_races(f, account, game, other, guid, stranger_guid)
        logout_save_race(f, account, game, guid)

        if restart_world:
            pending = f.buy(account)
            game.close(); other.close()
            restart_world()
            game = f.connect(account)
            f.wait(lambda: f.sql('SELECT COUNT(*) FROM shop_test_auth.atlas_shop_delivery_health WHERE protocol=2 '
                'AND last_seen_at>UTC_TIMESTAMP()-INTERVAL 10 SECOND') == '1')
            f.check(game.services()['ids'] == [pending['sequence']] and game.assign(order['sequence'], guid, renamed)['error'] == 0,
                'Available services and idempotent consumed receipts survive a real world restart and reconnection.')
        f.save(True, 'complete')
        print('PASS: native account-service integration completed,', len(f.checks), 'checks.', flush=True)
    except Exception as error:
        f.save(False, type(error).__name__ + ': ' + str(error))
        raise
    finally:
        if trigger:
            f.sql('DROP TRIGGER IF EXISTS shop_test_auth.' + trigger)
        f.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    run(parser.parse_args().root)
