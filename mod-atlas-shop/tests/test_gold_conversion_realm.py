#!/usr/bin/env python3
"""Real gold -> wallet -> native rename integration, only in the private fixture."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import os
from pathlib import Path
import signal
import struct
import time
import uuid
from hermes_protocol_client import BnetClient
from test_hermes_account_services import VasClient
from test_account_services_realm import Fixture, RowLock


@contextmanager
def pause_world(f):
    pid = int((f.root / 'world.pid').read_text())
    executable = Path('/proc/' + str(pid) + '/exe').resolve(strict=True)
    candidates = [Path('/opt/arthas-next/candidates') / name for name in
                  ('atlas-shop-rename-gold-20260912', 'atlas-shop-rename-identity-20260912')]
    private_candidate = any(executable == candidate / 'build/worldserver'
                            and (candidate / 'server/etc').resolve() == f.root / 'etc' for candidate in candidates)
    if os.readlink('/proc/' + str(pid) + '/ns/net') != os.readlink('/proc/self/ns/net'):
        raise RuntimeError('Refusing to signal a world outside this private test network.')
    if executable != f.root / 'build-native/worldserver' and not private_candidate:
        raise RuntimeError('Refusing to signal anything except the owned test world.')
    os.kill(pid, signal.SIGSTOP)
    try:
        yield
    finally:
        os.kill(pid, signal.SIGCONT)


def run(root, restart_world=None):
    f = Fixture(root, 'gold-conversion-result.json')
    connections, trigger = [], None
    try:
        f.wait(lambda: f.sql('SELECT COUNT(*) FROM shop_test_auth.atlas_shop_conversion_health WHERE realm_id=1 '
            'AND protocol=1 AND last_seen_at>UTC_TIMESTAMP()-INTERVAL 15 SECOND') == '1', timeout=180)
        account = f.register(euro_cents=1234, credit_cents=0)
        game = f.connect(account)
        original = f.name('Gold')
        f.check(game.create(original) == 0x2f, 'The core creates the synthetic source character through ordinary character creation.')
        guid = game.characters()[0]['guid']
        game.close()
        f.wait(lambda: f.sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account['id'])) == '0')
        f.sql('UPDATE shop_test_chars.characters SET level=10,money=25000067 WHERE guid=' + str(guid))
        money = lambda: int(f.sql('SELECT money FROM shop_test_chars.characters WHERE guid=' + str(guid)))
        snapshot = f.http(account, 'shop')
        f.check(snapshot['conversions']['available'] and f.balance(account) == (1234, 0),
            'A live core heartbeat opens gold conversion; the starting credit wallet is empty.')

        def input(copper=100000, key=None):
            return {'idempotencyKey': key or uuid.uuid4().hex, 'characterGuid': guid, 'offeredCopper': copper,
                'expectedCreditCents': copper // 10000, 'catalogRevision': snapshot['catalogRevision']}

        def create(body):
            return f.http(account, 'shop/conversions', body)

        def read(order):
            return f.http(account, 'shop/conversions/' + order['id'])

        def finished(order):
            return f.wait(lambda: (row := read(order))['status'] != 'pending' and row)

        def reject_input(body, status=409):
            result = f.http(account, 'shop/conversions', body, status)
            f.check('error' in result, 'The API rejects invalid or ineligible conversion input without enqueueing or charging.')

        for change, status in [({'characterGuid': guid + 999999}, 409), ({'offeredCopper': 10001}, 400),
                ({'expectedCreditCents': 999}, 409), ({'catalogRevision': 'obsolete'}, 409),
                ({'offeredCopper': 26000000, 'expectedCreditCents': 2600}, 409)]:
            reject_input(input() | change, status)

        main = input(20000000)
        with ThreadPoolExecutor(max_workers=6) as pool:
            receipts = list(pool.map(lambda _: create(main), range(6)))
        converted = finished(receipts[0])
        f.check(all(r['id'] == converted['id'] for r in receipts) and converted['status'] == 'completed'
            and converted['goldBeforeCopper'] == 25000067 and converted['goldAfterCopper'] == 5000067
            and converted['creditBeforeCents'] == 0 and converted['creditAfterCents'] == 2000
            and money() == 5000067 and f.balance(account) == (1234, 2000),
            'Six concurrent POST retries convert 2000 gold exactly once, preserving all silver/copper and the euro wallet.')
        reject_input(main | {'offeredCopper': 10000000, 'expectedCreditCents': 1000})
        foreign = f.register(euro_cents=0, credit_cents=0)
        f.check(f.http(foreign, 'shop/conversions/' + converted['id'], expected=404)['error'],
            'A conversion receipt is inaccessible from another account.')
        after = f.http(account, 'shop')
        f.check(after['creditBalanceEuroCents'] == 2000 and next(c for c in after['characters'] if c['guid'] == guid)['goldCopper'] == 5000067
            and any(h['kind'] == 'conversion' and h['amountCents'] == 2000 for h in after['history']),
            'The snapshot and history expose the same committed gold debit and credit receipt.')

        order = f.buy(account, 'credits')
        f.check(f.balance(account) == (1234, 1300) and f.state(order) == 'available' and order['characterGuid'] == 0,
            'The rename service is funded entirely by converted gold and remains unassigned until selection in game.')

        def modern_connect():
            ticket = f.http(account, 'game-ticket', {})
            bnet = BnetClient(ticket['ticket'], account['id']); connections.append(bnet)
            world = VasClient(bnet); connections.append(world)
            return bnet, world

        bnet, world = modern_connect()
        modern = world.character()
        world.pump(lambda: order['sequence'] in world.available)
        choices = world.service_characters()
        f.check(any(c['guid'] == guid and c['account'] == account['id'] for c in choices),
            'Real SSO and encrypted 3.4.3 Hermes packets expose the service and its character selector.')
        requested = '\u00c9' + f.name('or').lower()
        f.check(world.assign(order, modern, bnet.realm_address, requested, True) == (0, 0) and f.state(order) == 'available',
            'Native Unicode name validation leaves the paid service available until confirmation.')
        f.check(world.assign(order, modern, bnet.realm_address, requested) == (0, 0)
            and f.state(order) == 'consumed' and f.balance(account) == (1234, 1300) and money() == 5000067,
            'Native name confirmation consumes the service without charging credits or gold a second time.')
        world.close(); bnet.close()
        f.wait(lambda: f.sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account['id'])) == '0')
        bnet, world = modern_connect()
        modern = world.character()
        f.check(requested.encode() in modern['payload'] and not modern['flags'] & 0x4000
            and f.character(guid)[0] == requested and f.balance(account) == (1234, 1300),
            'A fresh SSO/Hermes connection retains the new name, consumed service and exact remaining balances.')
        world.close(); bnet.close()
        f.wait(lambda: f.sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account['id'])) == '0')
        # Real SRP performed by Hermes replaced the bootstrap fixture session key.
        account['key'] = bytes.fromhex(f.sql('SELECT HEX(session_key) FROM shop_test_auth.account WHERE id=' + str(account['id'])))
        game = f.connect(account); game.characters()

        before = money(), f.balance(account)
        with pause_world(f):
            stale = create(input())
            f.sql('UPDATE shop_test_chars.characters SET money=9999 WHERE guid=' + str(guid))
        row = finished(stale)
        f.check(row['status'] == 'rejected' and row['reason'] == 'insufficient-gold' and f.balance(account) == before[1] and money() == 9999,
            'The core rechecks actual money after API enqueue and rejects a stale gold quote without granting credits.')
        f.sql('UPDATE shop_test_chars.characters SET money=' + str(before[0]) + ' WHERE guid=' + str(guid))

        with pause_world(f):
            expired = create(input())
            f.sql("UPDATE shop_test_auth.atlas_shop_gold_conversion SET expires_at=UTC_TIMESTAMP()-INTERVAL 1 SECOND WHERE id='" + expired['id'] + "'")
        row = finished(expired)
        f.check(row['status'] == 'rejected' and row['reason'] == 'request-expired' and (money(), f.balance(account)) == before,
            'An expired durable request releases its pending slot without changing either balance.')

        trigger = 'atlas_gold_failure_' + uuid.uuid4().hex
        f.sql('CREATE TRIGGER shop_test_auth.' + trigger + ' BEFORE UPDATE ON shop_test_auth.atlas_shop_gold_conversion FOR EACH ROW '
            "SET NEW.status=IF(NEW.account_id=" + str(account['id']) + " AND NEW.status='completed','blocked',NEW.status)")
        failed = create(input())
        time.sleep(2)
        f.check(read(failed)['status'] == 'pending' and (money(), f.balance(account)) == before,
            'A failure writing the final receipt rolls back the real character debit and wallet credit together.')
        f.sql('DROP TRIGGER shop_test_auth.' + trigger); trigger = None
        row = finished(failed)
        f.check(row['status'] == 'completed' and money() == before[0] - 100000 and f.balance(account) == (1234, before[1][1] + 10),
            'The durable request succeeds once after the SQL failure is removed, on the normal realm retry.')

        before = money(), f.balance(account)
        with pause_world(f):
            guarded = create(input())
            lock = RowLock(f, 'shop_test_chars.characters', 'guid', guid)
        try:
            # INNODB_TRX truncates long queries; identify the stable SELECT prefix.
            f.wait(lambda: int(f.sql("SELECT COUNT(*) FROM information_schema.innodb_trx WHERE trx_state='LOCK WAIT' AND trx_query LIKE 'SELECT c.money,w.credit_cents,%'")) > 0)
            game.send(0x03d, struct.pack('<Q', guid))
            f.check(len(game.until(0x041)) > 0 and f.character(guid)[2] == '0',
                'While conversion holds the core write guard, a competing native login cannot load the old money.')
        finally:
            lock.__exit__(None, None, None)
        f.check(finished(guarded)['status'] == 'completed' and money() == before[0] - 100000,
            'Releasing the controlled SQL lock commits the guarded conversion once.')

        game.characters(); game.send(0x03d, struct.pack('<Q', guid)); game.until(0x236)
        f.wait(lambda: f.character(guid)[2] == '1')
        reject_input(input())
        game.send(0x04b); game.until(0x04d)
        f.wait(lambda: f.character(guid)[2] == '0')
        f.check(money() == before[0] - 100000, 'A full native login, logout and character save preserve the converted gold balance.')

        # Reserve enough headroom to refund an unused credit service at the cap.
        reserve = f.buy(account, 'credits')
        f.sql('UPDATE shop_test_auth.atlas_shop_wallet SET credit_cents=999999300 WHERE account_id=' + str(account['id']))
        reject_input(input())
        f.http(account, 'shop/orders/' + reserve['id'] + '/cancel', {})
        f.check(f.balance(account) == (1234, 1000000000),
            'Gold conversion preserves refund headroom; an unused service can refund exactly at the wallet ceiling.')
        f.sql('UPDATE shop_test_auth.atlas_shop_wallet SET credit_cents=1000 WHERE account_id=' + str(account['id']))
        with pause_world(f):
            cap_changed = create(input())
            f.sql('UPDATE shop_test_auth.atlas_shop_wallet SET credit_cents=1000000000 WHERE account_id=' + str(account['id']))
        row = finished(cap_changed)
        f.check(row['status'] == 'rejected' and row['reason'] == 'credit-limit',
            'The core independently rechecks the credit ceiling after enqueue, under the wallet lock.')
        f.sql('UPDATE shop_test_auth.atlas_shop_wallet SET credit_cents=1000 WHERE account_id=' + str(account['id']))

        with pause_world(f):
            durable_input = input()
            durable = create(durable_input)
            reject_input(input())
        game.close()
        if restart_world is None: raise RuntimeError('A real fixture restart callback is required.')
        restart_world()
        row = finished(durable)
        f.check(row['status'] == 'completed' and create(durable_input)['id'] == durable['id']
            and f.http(account, 'shop')['conversions']['available'],
            'After a real world process restart, conversion history and the same idempotency key remain recoverable.')
        f.check(f.sql('SELECT COUNT(*) FROM shop_test_auth.atlas_shop_gold_conversion WHERE account_id=' + str(account['id'])
            + " AND status='pending'") == '0', 'Every test request has a durable terminal outcome with no stranded debit.')
        f.save(True, 'complete')
        print('GOLD CONVERSION AND FULL NATIVE RENAME PASS:', len(f.checks), 'checks', flush=True)
    finally:
        if trigger: f.sql('DROP TRIGGER IF EXISTS shop_test_auth.' + trigger)
        for connection in connections:
            try: connection.close()
            except OSError: pass
        f.close()
