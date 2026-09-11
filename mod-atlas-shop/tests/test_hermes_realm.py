#!/usr/bin/env python3
"""Exercise launcher SSO and 3.4.3 rename packets through real fixture Hermes.

There is no GUI automation. Native character bootstrap provisions a disposable
account only; subsequent delivery, protocol translation, player login/logout
and rename consumption all run through the real services.
"""
import argparse
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
from hermes_protocol_client import BnetClient, ModernWorldClient
from test_native_realm import WorldClient


def run(root):
    root = Path(root).resolve(strict=True)
    if root != Path('/opt/atlas-shop-tests/rename-20260911') or os.readlink('/proc/self/ns/net') == os.readlink('/proc/1/ns/net'):
        raise RuntimeError('Expected the dedicated private fixture.')
    report_path = root / 'hermes-test-result.json'
    checks, sockets = [], []
    token = None
    report_path.write_text(json.dumps({'passed': False, 'state': 'starting', 'checks': checks}) + '\n')

    def check(value, message):
        if not value:
            raise AssertionError(message)
        checks.append(message)
        print('PASS', message, flush=True)
        report_path.write_text(json.dumps({'passed': False, 'state': 'running', 'checks': checks}, indent=2) + '\n')

    def sql(query):
        return subprocess.check_output(['mysql', '--defaults-extra-file=' + str(root / 'mysql-client.cnf'),
            '-NBe', query], text=True, stderr=subprocess.PIPE, timeout=30).strip()

    def http(path, body=None, expected=200):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        request = urllib.request.Request('http://127.0.0.1:18081/api/v1/' + path,
            data=None if body is None else json.dumps(body).encode(), headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                status, data = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, data = error.code, error.read()
        if status != expected:
            raise RuntimeError('Unexpected HTTP status for ' + path + ': ' + str(status))
        return json.loads(data) if data else None

    def wait_for(predicate, timeout=40):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if predicate():
                return
            time.sleep(0.2)
        raise RuntimeError('Fixture condition did not become true within ' + str(timeout) + ' seconds.')

    def char_state():
        value = sql('SELECT name,at_login,online FROM shop_test_chars.characters WHERE guid=' + str(guid) + ';').split('\t')
        return value[0], int(value[1]), int(value[2])

    def order_state(order):
        return sql("SELECT status FROM shop_test_auth.atlas_shop_order WHERE id='" + order['id'] + "';")

    def balance():
        return tuple(map(int, sql('SELECT euro_cents,credit_cents FROM shop_test_auth.atlas_shop_wallet WHERE account_id=' + str(account_id) + ';').split('\t')))

    def purchase(currency='eur'):
        return {'idempotencyKey': uuid.uuid4().hex, 'offerId': 'character-rename', 'characterGuid': guid,
            'currency': currency, 'expectedAmountCents': 500 if currency == 'eur' else 700,
            'catalogRevision': snapshot['catalogRevision']}

    def connect(abort_before_ack=False):
        ticket = http('game-ticket', {})
        if ticket['accountId'] != account_id:
            raise RuntimeError('SSO ticket belongs to the wrong fixture account.')
        bnet = BnetClient(ticket['ticket'], account_id)
        sockets.append(bnet)
        world = ModernWorldClient(bnet, abort_before_ack=abort_before_ack)
        sockets.append(world)
        return bnet, world

    def name(prefix):
        return prefix + ''.join(secrets.choice('abcdefghijklmnopqrstuvwxyz') for _ in range(6))

    try:
        wait_for(lambda: sql('SELECT COUNT(*) FROM shop_test_auth.atlas_shop_delivery_health WHERE realm_id=1 AND last_seen_at>UTC_TIMESTAMP()-INTERVAL 15 SECOND;') == '1', timeout=180)
        username = 'WIRE' + secrets.token_hex(4).upper()
        registered = http('accounts', {'username': username, 'password': secrets.token_hex(16),
                                      'email': username.lower() + '@example.invalid'})
        account_id, token = registered['profile']['accountId'], registered['accessToken']
        bootstrap_key = secrets.token_bytes(40)
        sql("UPDATE shop_test_auth.account SET session_key=UNHEX('" + bootstrap_key.hex()
            + "'),expansion=2,os='Win',last_ip='127.0.0.1' WHERE id=" + str(account_id) + ';')
        bootstrap = WorldClient(username, bootstrap_key)
        sockets.append(bootstrap)
        initial_name = name('Al')
        check(bootstrap.create(initial_name) == 0x2F, 'The fixture bootstraps one native-created character on a new synthetic account.')
        guid = bootstrap.characters()[0]['guid']
        bootstrap.close()
        wait_for(lambda: sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account_id) + ';') == '0')
        sql('INSERT INTO shop_test_auth.atlas_shop_wallet(account_id,euro_cents,credit_cents,updated_at) VALUES('
            + str(account_id) + ',10000,10000,UTC_TIMESTAMP(6));')
        snapshot = http('shop')
        bnet, world = connect()
        check(True, 'The real API SSO ticket authenticates BNet and an AES-GCM 3.4.3 realm socket through Hermes.')
        modern = world.character()
        check(modern['guid'] == guid and initial_name.capitalize().encode() in modern['payload'],
              'Hermes translates native character enumeration to the 3.4.3 GUID, name and envelope.')
        check(world.rename(modern, name('No')) != 0 and char_state()[0] == initial_name.capitalize(),
              'A 3.4.3 rename without a purchased entitlement is rejected by the real core.')
        order = http('shop/orders', purchase())
        time.sleep(3)
        check(order_state(order) == 'pending' and balance() == (9500, 10000),
              'A purchase remains pending while the real Hermes account session is connected.')
        world.send(14185, struct.pack('<I', 0))  # Normal CMSG_LOG_DISCONNECT from the modern client.
        wait_for(lambda: order_state(order) == 'delivered')
        world.close()
        bnet.close()
        bnet, world = connect()
        modern = world.character()
        check(modern['flags'] & 0x4000 != 0, 'The delivered native rename flag is present in the 3.4.3 enumeration.')
        check(world.rename(modern, '1') != 0 and char_state()[1] & 1 != 0,
              'An invalid 3.4.3 name is rejected without consuming the paid entitlement.')
        renamed = name('Be')
        check(world.rename(modern, renamed) == 0, 'A valid 3.4.3 rename round trip succeeds through Hermes and the real core.')
        wait_for(lambda: char_state()[0] == renamed.capitalize() and char_state()[1] & 1 == 0)
        check(world.rename(modern, name('No')) != 0 and balance() == (9500, 10000),
              'Replaying the 3.4.3 rename neither renames again nor debits again.')
        modern = world.character()
        check(renamed.capitalize().encode() in modern['payload'] and modern['flags'] & 0x4000 == 0,
              'A fresh 3.4.3 enumeration contains the new name and no remaining rename flag.')

        # Create the credit order before entering world; the API forbids a new
        # purchase once online. Exercise both the realm and instance sockets.
        played_order = http('shop/orders', purchase('credits'))
        world.send(13803, modern['packedGuid'] + struct.pack('<f', 300.0))
        connect_to = world.until(12365)
        check(len(connect_to) >= 276, 'Hermes issues a real instance-connection request for 3.4.3 player login.')
        instance = ModernWorldClient(continued=(int.from_bytes(connect_to[-8:], 'little'), world.key))
        sockets.append(instance)
        entered = instance.until(9623)
        wait_for(lambda: char_state()[2] == 1)
        check(len(entered) == 24 and int.from_bytes(entered[:4], 'little') == 0,
              'The 3.4.3 instance connection receives LOGIN_VERIFY_WORLD and the core records the player online.')
        online_reject = http('shop/orders', purchase(), expected=409)
        check(online_reject['error'] == 'shop-character-online' and balance() == (9500, 9300),
              'The API refuses an online-player purchase without an extra debit during the Hermes session.')
        check(order_state(played_order) == 'pending' and char_state()[1] & 1 == 0,
              'The earlier credit purchase stays pending while the character is playing through Hermes.')
        instance.send(13526, b'\0')
        finished, accepted = False, False
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline and not finished:
            ready, _, _ = select.select([world.socket, instance.socket], [], [], 1)
            for connection in ready:
                client = world if connection is world.socket else instance
                try:
                    opcode, data = client.receive()
                except RuntimeError:
                    if client is instance:
                        continue  # Hermes closes this socket on successful logout.
                    raise
                if opcode == 11730:
                    client.send(14909, data[:4] + struct.pack('<I', int(time.monotonic() * 1000) & 0xFFFFFFFF))
                elif opcode == 9859:
                    accepted = int.from_bytes(data[:4], 'little') == 0
                elif opcode == 9860:
                    finished = True
        check(accepted and finished, 'Hermes translates the real logout request, acceptance and completion across both 3.4.3 sockets.')
        wait_for(lambda: char_state()[2] == 0)
        saved_name, saved_flags, _ = char_state()
        check(order_state(played_order) == 'pending', 'After real player logout, delivery still waits for the remaining account connection.')
        instance.close()
        world.send(14185, struct.pack('<I', 0))
        wait_for(lambda: order_state(played_order) == 'delivered')
        world.close()
        bnet.close()
        check(char_state() == (saved_name, saved_flags | 1, 0),
              'After the Hermes session disconnects, delivery preserves the saved player flags and grants rename.')
        bnet, world = connect()
        modern = world.character()
        after_play = name('Ce')
        check(modern['flags'] & 0x4000 != 0 and world.rename(modern, after_play) == 0,
              'A 3.4.3 reconnect consumes the credit-funded entitlement after actual play/logout.')
        wait_for(lambda: char_state() == (after_play.capitalize(), saved_flags, 0))
        check(balance() == (9500, 9300), 'Both real Hermes rename purchases debit their currency exactly once.')
        # Regression: abruptly losing the modern realm socket used to leave its
        # legacy socket and periodic pings alive, blocking shop delivery forever.
        abrupt_order = http('shop/orders', purchase())
        world.close()  # Deliberately no CMSG_LOG_DISCONNECT; keep BNet alive.
        wait_for(lambda: order_state(abrupt_order) == 'delivered', timeout=15)
        check(sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account_id) + ';') == '0',
              'An abrupt realm-socket EOF releases the legacy account and delivers the pending order.')
        bnet.close()
        bnet, world = connect()
        modern = world.character()
        check(world.rename(modern, name('Da')) == 0 and balance() == (9000, 9300),
              'The purchase interrupted by a network drop remains usable and is charged once.')

        # Closing the old socket after a new realm socket authenticates must not
        # tear down the replacement connection or its legacy client.
        old_world = world
        old_world.send(14185, struct.pack('<I', 0))
        wait_for(lambda: sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account_id) + ';') == '0')
        bnet.join_realm()
        world = ModernWorldClient(bnet)
        sockets.append(world)
        old_world.close()
        check(world.character()['guid'] == guid and sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account_id) + ';') == '1',
              'Closing an old realm socket preserves its already authenticated replacement.')
        world.send(14185, struct.pack('<I', 0))
        wait_for(lambda: sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account_id) + ';') == '0')
        world.close()
        bnet.close()

        bnet, world = connect(abort_before_ack=True)
        wait_for(lambda: sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account_id) + ';') == '0', timeout=15)
        check(True, 'An EOF before encryption acknowledgement also releases the authenticated legacy socket.')
        bnet.close()

        # Abrupt realm loss while a Player exists must close the instance socket,
        # let the core save/logout, and only then grant the pending entitlement.
        bnet, world = connect()
        modern = world.character()
        crash_order = http('shop/orders', purchase('credits'))
        world.send(13803, modern['packedGuid'] + struct.pack('<f', 300.0))
        connect_to = world.until(12365)
        instance = ModernWorldClient(continued=(int.from_bytes(connect_to[-8:], 'little'), world.key))
        sockets.append(instance)
        instance.until(9623)
        wait_for(lambda: char_state()[2] == 1)
        # Force an actual TCP reset on this Linux fixture, independently of
        # whether unread world updates happened to be buffered at close time.
        world.socket.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack('ii', 1, 0))
        dropped_at = time.monotonic()
        world.close()
        instance.socket.settimeout(5)
        try:
            while instance.socket.recv(65536):
                if time.monotonic() - dropped_at > 5:
                    raise RuntimeError('The companion instance socket stayed open after realm reset.')
        except (ConnectionResetError, ConnectionAbortedError):
            pass
        check(True, 'A realm TCP reset also closes the companion instance connection.')
        # WorldSessionMgr retains disconnected in-world players in its offline
        # session map for 60 seconds. Keep that real reconnect grace unchanged.
        time.sleep(2)
        check(order_state(crash_order) == 'pending' and char_state()[2] == 1,
              'Delivery waits during the core 60-second offline-player reconnect grace period.')
        wait_for(lambda: order_state(crash_order) == 'delivered' and char_state()[2] == 0, timeout=80)
        drop_delivery_seconds = round(time.monotonic() - dropped_at, 2)
        check(char_state()[1] & 1 != 0 and balance() == (9000, 8600),
              'Abrupt realm loss during play saves/logs out the player and safely delivers the credit order.')
        instance.close()
        bnet.close()
        bnet, world = connect()
        modern = world.character()
        check(world.rename(modern, name('Ef')) == 0 and balance() == (9000, 8600),
              'Reconnection after an in-world network drop consumes the entitlement without another debit.')
        world.send(14185, struct.pack('<I', 0))
        wait_for(lambda: sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account_id) + ';') == '0')
        report = {'passed': True, 'checkCount': len(checks), 'checks': checks, 'accountId': account_id,
            'characterGuid': guid, 'clientBuild': 54261, 'hermesTested': True, 'launcherSsoTested': True,
            'legacySrpTested': False, 'graphicalClientTested': False, 'playerLoginLogoutTested': True,
            'abruptDisconnectTested': True, 'preEncryptionDisconnectTested': True, 'realmReplacementTested': True,
            'tcpResetTested': True, 'companionInstanceCloseTested': True,
            'inWorldDropDeliverySeconds': drop_delivery_seconds,
            'bnetCertificateSha256': bnet.certificate_sha256, 'completedAtUnix': int(time.time())}
        report_path.write_text(json.dumps(report, indent=2) + '\n')
        print('PASS: ' + str(len(checks)) + ' real Hermes/3.4.3 protocol checks.', flush=True)
        return report
    finally:
        for connection in reversed(sockets):
            connection.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    run(parser.parse_args().root)
