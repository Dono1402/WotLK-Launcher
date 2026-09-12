#!/usr/bin/env python3
"""Run account VAS services through real SSO, Hermes, core and MySQL, without UI."""
import argparse
import select
import secrets
import struct
import time
from hermes_protocol_client import BnetClient, ModernWorldClient, packed_guid
from test_account_services_realm import Fixture


def distribution(data, pos=0):
    identity, status, product = struct.unpack_from('<QII', data, pos)
    pos += 16
    _, account, pos = packed_guid(data, pos)
    _, character, pos = packed_guid(data, pos)
    realm, native_realm, purchase, review = struct.unpack_from('<IIQI', data, pos)
    pos += 20
    flags = data[pos]
    pos += 1
    if not flags & 0x80 or product != 0x41544c01:
        raise RuntimeError('Expected the verified inline rename product.')
    inline, kind = struct.unpack_from('<IB', data, pos)
    if inline != product or kind != 5 or struct.unpack_from('<I', data, pos + 25)[0] != 1024:
        raise RuntimeError('Invalid native rename product classification.')
    pos += 48
    if pos > len(data): raise RuntimeError('Truncated distribution.')
    return {'id': identity, 'status': status, 'account': account, 'character': character,
            'realm': realm, 'nativeRealm': native_realm, 'revoked': bool(flags & 0x40)}, pos


class VasClient(ModernWorldClient):
    def __init__(self, *args, **kwargs):
        self.available, self.updates, self.lists, self.glue = {}, [], 0, []
        super().__init__(*args, **kwargs)

    def receive(self):
        opcode, data = super().receive()
        if opcode == 0x2777:
            if int.from_bytes(data[:4], 'little') != 0: raise RuntimeError('Distribution list failed.')
            count, pos = int.from_bytes(data[4:6], 'big') >> 5, 6
            if count > 100: raise RuntimeError('Unbounded service list.')
            for _ in range(count):
                entry, pos = distribution(data, pos)
                self.available[entry['id']] = entry
            if pos != len(data): raise RuntimeError('Distribution list has trailing bytes.')
            self.lists += 1
        elif opcode == 0x2779:
            entry, pos = distribution(data)
            if pos != len(data): raise RuntimeError('Distribution update has trailing bytes.')
            self.updates.append(entry)
            if entry['revoked'] or entry['status'] == 3: self.available.pop(entry['id'], None)
            else: self.available[entry['id']] = entry
        elif opcode == 9664:
            self.glue.append(data)
        return opcode, data

    def pump(self, predicate, timeout=15):
        deadline = time.monotonic() + timeout
        while not predicate():
            if time.monotonic() >= deadline: raise RuntimeError('Modern service update timed out.')
            if select.select([self.socket], [], [], .2)[0]:
                opcode, data = self.receive()
                if opcode == 11730:
                    self.send(14909, data[:4] + struct.pack('<I', int(time.monotonic() * 1000) & 0xffffffff))

    def assign(self, order, character, realm, name, validate=False):
        token = secrets.randbits(32)
        encoded = name.encode('utf-8')
        body = struct.pack('<IQ', token, order['sequence']) + character['packedGuid'] + struct.pack('<II', realm, 0)
        body += bytes(6) + bytes([2, 0, 0]) + bytes(4)
        body += ((len(encoded) << 18) | (4 if validate else 0)).to_bytes(3, 'big') + encoded
        self.send(0x3742, body)
        reply = struct.unpack('<III', self.until(0x2888))
        if reply[0] != token: raise RuntimeError('The modern assignment response lost its request token.')
        return reply[1:]

    def service_characters(self):
        token = secrets.randbits(32)
        self.send(0x36f8, struct.pack('<II', token, 4))
        data = self.until(0x27f1)
        returned, error, extra, count = struct.unpack_from('<IIII', data)
        if returned != token or error or extra or count > 100:
            raise RuntimeError('Invalid service character list response.')
        pos, result = 16, []
        for _ in range(count):
            _, account, pos = packed_guid(data, pos)
            guid_bytes, guid, pos = packed_guid(data, pos)
            realm, race, cls, sex, level, last_login, unknown = struct.unpack_from('<IBBBBQI', data, pos)
            pos += 20
            sizes = int.from_bytes(data[pos:pos + 2], 'big')
            pos += 2
            name_length, realm_length = sizes >> 10, (sizes >> 1) & 511
            name = data[pos:pos + name_length].decode('utf-8')
            pos += name_length
            realm_name = data[pos:pos + realm_length].decode('utf-8')
            pos += realm_length
            result.append({'account': account, 'guid': guid, 'packedGuid': guid_bytes,
                           'realm': realm, 'realmName': realm_name, 'level': level, 'name': name})
        if pos != len(data): raise RuntimeError('Service character list has trailing bytes.')
        return result


def run(root):
    f = Fixture(root, 'hermes-account-services-result.json')
    connections = []
    try:
        started = int((f.root / 'world.pid').stat().st_mtime)
        f.wait(lambda: f.sql('SELECT COUNT(*) FROM shop_test_auth.atlas_shop_delivery_health WHERE realm_id=1 AND protocol=2 '
            'AND last_seen_at>FROM_UNIXTIME(' + str(started) + ')') == '1', timeout=180)
        account = f.register()
        bootstrap = f.connect(account)
        original = f.name('Vas')
        f.check(bootstrap.create(original) == 0x2f, 'The actual core creates the disposable beneficiary through ordinary character creation.')
        guid = bootstrap.characters()[0]['guid']
        bootstrap.close()
        f.wait(lambda: f.sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account['id'])) == '0')
        f.sql('UPDATE shop_test_chars.characters SET level=10 WHERE guid=' + str(guid))
        order = f.buy(account)
        funds = f.balance(account)

        def connect():
            ticket = f.http(account, 'game-ticket', {})
            bnet = BnetClient(ticket['ticket'], account['id'])
            connections.append(bnet)
            world = VasClient(bnet)
            connections.append(world)
            return bnet, world

        bnet, world = connect()
        modern = world.character()
        world.pump(lambda: order['sequence'] in world.available)
        entry = world.available[order['sequence']]
        f.check(entry['account'] == account['id'] and entry['character'] == 0 and entry['status'] == 1
            and entry['realm'] == bnet.realm_address and modern['guid'] == guid,
            'Launcher SSO and the encrypted 3.4.3 Hermes connection expose the real unassigned account service.')
        f.check(len(world.glue) >= 2 and world.glue[0] != world.glue[-1],
            'Hermes updates the native service feature gate after the real core handshake succeeds.')
        world.send(0x36c4)
        f.check(world.until(0x2775) == bytes(24), 'The native product-list request returns a valid empty Blizzard store.')
        world.send(0x36c5)
        f.check(world.until(0x2776) == bytes(8), 'The native purchase-list request returns the verified empty store envelope.')
        world.send(0x36fb)
        f.check(world.until(0x27f5) == bytes(1), 'The native VAS-state request returns the verified state envelope.')
        choices = world.service_characters()
        expected_realm_name = f.sql('SELECT name FROM shop_test_auth.realmlist WHERE id=1')
        f.check(len(choices) == 1 and choices[0]['account'] == account['id'] and choices[0]['guid'] == guid
            and choices[0]['level'] == 10 and choices[0]['name'] == original.capitalize()
            and choices[0]['realm'] == bnet.realm_address and choices[0]['realmName'] == expected_realm_name != '',
            'The native service selector receives the authenticated account character and a nonempty matching realm name.')
        requested = '\u00c9' + f.name('va').lower()
        f.check(world.assign(order, modern, bnet.realm_address, 'Invalid123') != (0, 0) and f.state(order) == 'available',
            'A native 3.4.3 name rejection leaves the paid service available.')
        f.check(world.assign(order, modern, bnet.realm_address, requested, True) == (0, 0)
            and f.state(order) == 'available' and f.character(guid)[0] == original.capitalize(),
            'The real native validation round trip accepts Unicode without consuming or renaming.')
        f.check(world.assign(order, modern, bnet.realm_address, requested) == (0, 0)
            and f.state(order) == 'consumed' and f.character(guid)[0] == requested and f.balance(account) == funds,
            'The native confirmation round trip commits the Unicode name and receipt without a second debit.')
        f.check(order['sequence'] not in world.available and any(entry['id'] == order['sequence']
            and entry['status'] == 3 and entry['character'] == guid for entry in world.updates),
            'Hermes sends the consumed distribution update that removes the native account-service button.')
        refreshed = world.character()
        f.check(requested.encode() in refreshed['payload'] and not refreshed['flags'] & 0x4000,
            'Fresh 3.4.3 character enumeration shows the new name with no forced-rename entitlement.')
        f.check(world.assign(order, refreshed, bnet.realm_address, requested) == (0, 0) and f.balance(account) == funds,
            'A repeated native confirmation returns the same receipt without another debit.')
        cancelled = f.buy(account, 'credits')
        world.pump(lambda: cancelled['sequence'] in world.available)
        f.http(account, 'shop/orders/' + cancelled['id'] + '/cancel', {})
        world.pump(lambda: cancelled['sequence'] not in world.available)
        f.check(any(entry['id'] == cancelled['sequence'] and entry['revoked'] for entry in world.updates)
            and f.balance(account) == funds,
            'An API cancellation produces a native revocation update and refunds the currency exactly once.')

        world.send(13803, refreshed['packedGuid'] + struct.pack('<f', 300.0))
        connect_to = world.until(12365)
        instance = VasClient(continued=(int.from_bytes(connect_to[-8:], 'little'), world.key))
        connections.append(instance)
        instance.until(9623)
        f.wait(lambda: f.character(guid)[2] == '1')
        played = f.buy(account, 'credits')
        f.check(f.state(played) == 'available' and world.assign(played, refreshed, bnet.realm_address, f.name()) != (0, 0),
            'An account service can be purchased during actual Hermes gameplay; consumption requires character selection.')
        instance.send(13526, b'\0')
        accepted, finished = False, False
        deadline = time.monotonic() + 40
        while not finished and time.monotonic() < deadline:
            for connection in select.select([world.socket, instance.socket], [], [], 1)[0]:
                client = world if connection is world.socket else instance
                try: opcode, data = client.receive()
                except RuntimeError:
                    if client is instance: continue
                    raise
                if opcode == 11730: client.send(14909, data[:4] + struct.pack('<I', int(time.monotonic() * 1000) & 0xffffffff))
                elif opcode == 9859: accepted = int.from_bytes(data[:4], 'little') == 0
                elif opcode == 9860: finished = True
        f.check(accepted and finished, 'The actual player logs out through both Hermes sockets while the account connection stays open.')
        f.wait(lambda: f.character(guid)[2] == '0')
        instance.close()
        refreshed = world.character()
        world.pump(lambda: played['sequence'] in world.available)
        post_play = f.name('Post')
        f.check(world.assign(played, refreshed, bnet.realm_address, post_play) == (0, 0)
            and f.state(played) == 'consumed' and f.character(guid)[0] == post_play.capitalize(),
            'The player consumes the account service immediately after real logout, without closing the account or BNet session.')

        dropped = f.buy(account)
        world.pump(lambda: dropped['sequence'] in world.available)
        world.close()
        f.wait(lambda: f.sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account['id'])) == '0')
        bnet.close()
        bnet, world = connect()
        refreshed = world.character()
        world.pump(lambda: dropped['sequence'] in world.available)
        f.check(world.assign(dropped, refreshed, bnet.realm_address, f.name('Back')) == (0, 0)
            and f.state(dropped) == 'consumed',
            'A real realm-socket drop and SSO reconnection preserve the available native service and allow its consumption.')
        f.check(f.balance(account) == (funds[0] - 500, funds[1] - 700),
            'All completed native purchases preserve independent balances and debit each service exactly once.')
        f.save(True, 'complete')
        print('PASS: real Hermes native account services completed,', len(f.checks), 'checks.', flush=True)
    except Exception as error:
        f.save(False, type(error).__name__ + ': ' + str(error))
        raise
    finally:
        for connection in reversed(connections): connection.close()
        f.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    run(parser.parse_args().root)
