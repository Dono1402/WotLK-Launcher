#!/usr/bin/env python3
"""Exercise persisted client name caches against the real isolated rename chain.

The client cache is a test model fed by decoded native packets. The API, SQL
transaction, core player, name queries, Hermes and both encrypted sockets are
real. No graphical-client or addon rendering is claimed by this suite.
"""
import argparse
import select
import struct
import time
from hermes_protocol_client import BnetClient, packed_guid
from test_hermes_account_services import VasClient
from test_account_services_realm import Fixture


def lookup(data):
    assert struct.unpack_from('<I', data)[0] == 1 and data[4] == 0
    _, guid, pos = packed_guid(data, 5)
    assert data[pos] == 0x80
    pos += 1
    bits = int.from_bytes(data[pos:pos + 6], 'big')
    deleted, size = bits >> 47, (bits >> 41) & 63
    declined = [(bits >> (34 - i * 7)) & 127 for i in range(5)]
    pos += 6 + sum(declined)
    _, account, pos = packed_guid(data, pos)
    _, bnet, pos = packed_guid(data, pos)
    _, actual, pos = packed_guid(data, pos)
    club, realm, race, sex, cls, level, unused = struct.unpack_from('<QIBBBBB', data, pos)
    pos += 17
    name = data[pos:pos + size].decode('utf-8')
    assert not deleted and actual == guid and realm and size and pos + size == len(data)
    return guid, {'name': name, 'account': account, 'level': level}


def chat(data):
    kind, language = struct.unpack_from('<BI', data)
    _, sender, pos = packed_guid(data, 5)
    _, _, pos = packed_guid(data, pos)
    _, _, pos = packed_guid(data, pos)
    _, target, pos = packed_guid(data, pos)
    pos += 20
    bits = int.from_bytes(data[pos:pos + 9], 'big')
    sizes = [(bits >> 61) & 2047, (bits >> 50) & 2047, (bits >> 45) & 31,
             (bits >> 38) & 127, (bits >> 26) & 4095]
    pos += 9
    fields = []
    for size in sizes:
        fields.append(data[pos:pos + size].decode('utf-8')); pos += size
    return kind, sender, target, fields[-1]


class IdentityClient(VasClient):
    def __init__(self, *args, names=None, events=None, **kwargs):
        self.names = names if names is not None else {}
        self.events = events if events is not None else []
        super().__init__(*args, **kwargs)

    def receive(self):
        opcode, data = super().receive()
        if opcode == 0x2fff:
            _, guid, end = packed_guid(data, 0)
            assert end == len(data)
            self.names.pop(guid, None)
            self.events.append(('invalidate', guid))
        elif opcode == 0x301b:
            guid, entry = lookup(data)
            self.names[guid] = entry
            self.events.append(('name', guid, entry['name']))
        elif opcode == 0x2bad:
            self.events.append(('chat', *chat(data)))
        elif opcode == 0x2bb7:
            size = int.from_bytes(data[:2], 'big') >> 7
            assert len(data) == 2 + size
            self.events.append(('not-found', data[2:].decode('utf-8')))
        return opcode, data

    def query(self, character):
        self.send(0x3772, struct.pack('<I', 1) + character['packedGuid'])

    def whisper(self, target, message):
        name, text = target.encode('utf-8'), message.encode('utf-8')
        self.send(0x37d0, struct.pack('<I', 7) + ((len(name) << 15) | (len(text) << 4)).to_bytes(3, 'big') + name + text)


def run(root):
    f = Fixture(root, 'hermes-identity-result.json')
    connections, active = [], []

    def drain(predicate, timeout=15):
        deadline = time.monotonic() + timeout
        while not predicate():
            if time.monotonic() >= deadline:
                raise RuntimeError('Identity packet condition timed out.')
            for sock in select.select([c.socket for c in active], [], [], .2)[0]:
                client = next(c for c in active if c.socket is sock)
                opcode, data = client.receive()
                if opcode == 11730:
                    client.send(14909, data[:4] + struct.pack('<I', int(time.monotonic() * 1000) & 0xffffffff))
                elif opcode == 9859:
                    client.events.append(('logout-accepted', int.from_bytes(data[:4], 'little')))
                elif opcode == 9860:
                    client.events.append(('logout-complete',))

    def connect(account, names=None):
        ticket = f.http(account, 'game-ticket', {})
        bnet = BnetClient(ticket['ticket'], account['id'])
        world = IdentityClient(bnet, names=names)
        connections.extend([bnet, world]); active.append(world)
        return bnet, world, world.character()

    def enter(world, character):
        world.send(13803, character['packedGuid'] + struct.pack('<f', 300.0))
        target = world.until(12365)
        instance = IdentityClient(continued=(int.from_bytes(target[-8:], 'little'), world.key),
                                  names=world.names, events=world.events)
        connections.append(instance); active.append(instance)
        instance.until(9623)
        f.wait(lambda: f.character(character['guid'])[2] == '1')
        return instance

    def logout(world, instance, guid):
        start = len(world.events)
        instance.send(13526, b'\0')
        drain(lambda: ('logout-complete',) in world.events[start:], 40)
        assert ('logout-accepted', 0) in world.events[start:]
        active.remove(instance); instance.close()
        f.wait(lambda: f.character(guid)[2] == '0')
        return world.character()

    def provision(prefix):
        account = f.register()
        client = f.connect(account)
        name = f.name(prefix).capitalize()
        assert client.create(name) == 0x2f
        guid = client.characters()[0]['guid']
        client.close()
        f.wait(lambda: f.sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account['id'])) == '0')
        f.sql('UPDATE shop_test_chars.characters SET level=10 WHERE guid=' + str(guid))
        return account, name, guid

    try:
        started = int((f.root / 'world.pid').stat().st_mtime)
        f.wait(lambda: f.sql('SELECT COUNT(*) FROM shop_test_auth.atlas_shop_delivery_health WHERE protocol=2 '
            'AND last_seen_at>FROM_UNIXTIME(' + str(started) + ')') == '1', 180)
        owner, original, guid = provision('Id')
        observer, _, _ = provision('See')
        bnet, world, character = connect(owner)
        _, watching, watchchar = connect(observer)
        instance = enter(world, character)
        watchinstance = enter(watching, watchchar)
        world.query(character); watching.query(character)
        drain(lambda: guid in world.names and guid in watching.names)
        f.check(world.names[guid]['name'] == watching.names[guid]['name'] == original,
                'Owner and connected observer resolve the original name from actual core queries before rename.')
        character = logout(world, instance, guid)
        f.check(world.names[guid]['name'] == original,
                'The owner returns to character selection with the old in-game name still cached.')
        order = f.buy(owner)
        world.pump(lambda: order['sequence'] in world.available)
        funds = f.balance(owner)
        renamed = '\u00c9' + f.name('id').lower()
        invalidations = sum(e == ('invalidate', guid) for e in world.events + watching.events)
        assert world.assign(order, character, bnet.realm_address, 'Invalid123') != (0, 0)
        assert world.assign(order, character, bnet.realm_address, renamed, True) == (0, 0)
        f.check(f.state(order) == 'available' and f.character(guid)[0] == original
                and invalidations == sum(e == ('invalidate', guid) for e in world.events + watching.events),
                'Rejected names and validation alone preserve the character, service and client name caches.')
        event_start = len(world.events)
        assert world.assign(order, character, bnet.realm_address, renamed) == (0, 0)
        drain(lambda: ('invalidate', guid) in world.events[event_start:] and ('invalidate', guid) in watching.events
              and watching.names.get(guid, {}).get('name') == renamed)
        f.check(f.state(order) == 'consumed' and f.character(guid)[0] == renamed and f.balance(owner) == funds,
                'Committed rename invalidates the owner and refreshes the online observer without another purchase or reconnect.')
        character = world.character()
        assert renamed.encode() in character['payload']
        instance = enter(world, character)
        drain(lambda: world.names.get(guid, {}).get('name') == renamed)
        f.check(world.names[guid]['account'] == owner['id'] and world.names[guid]['level'] == 10,
                'Re-entry sends the current Unicode identity without a client query and preserves own-character account and level metadata.')
        watching.whisper(renamed, 'identity-new-name')
        drain(lambda: any(e[0] == 'chat' and e[-1] == 'identity-new-name' for e in world.events))
        watching.whisper(original, 'identity-old-name')
        drain(lambda: ('not-found', original) in watching.events)
        f.check(any(e[0] == 'chat' and e[2] == watchchar['guid'] and e[-1] == 'identity-new-name' for e in world.events),
                'A real whisper reaches the renamed character by its new name; the old name is no longer a valid online target.')
        character = logout(world, instance, guid)
        newer_order = f.buy(owner)
        world.pump(lambda: newer_order['sequence'] in world.available)
        newest = f.name('New').capitalize()
        assert world.assign(newer_order, character, bnet.realm_address, newest) == (0, 0)
        drain(lambda: watching.names.get(guid, {}).get('name') == newest)
        # Replay an earlier successful receipt after a later rename. Both the
        # database and the proxy selector must retain the current identity.
        character = world.character()
        assert world.assign(order, character, bnet.realm_address, renamed) == (0, 0)
        choices = world.service_characters()
        f.check(f.character(guid)[0] == newest and choices[0]['name'] == newest,
                'Replaying an older receipt after a second rename cannot restore the older name in the core or proxy selector.')
        # Simulate the persisted client cache reported before this fix: stale
        # across a full SSO reconnect, with no remaining paid service to apply.
        world.close(); active.remove(world); bnet.close()
        f.wait(lambda: f.sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(owner['id'])) == '0')
        persisted = {guid: {'name': original, 'account': owner['id'], 'level': 10}}
        _, world, character = connect(owner, persisted)
        instance = enter(world, character)
        drain(lambda: world.names.get(guid, {}).get('name') == newest)
        f.check(('invalidate', guid) in world.events and f.balance(owner) == (funds[0] - 500, funds[1]),
                'A stale persisted client cache is repaired at login without rebuying or reapplying an already consumed service.')
        f.save(True, 'complete')
        print('PASS: real in-game rename identity chain,', len(f.checks), 'checks.', flush=True)
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
