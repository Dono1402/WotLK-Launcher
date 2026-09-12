#!/usr/bin/env python3
"""Verify Atlas social modules through the real API, World and Hermes fixture."""
from pathlib import Path
import select
import struct
import sys
import time
import uuid

ROOT = Path('/opt/atlas-shop-tests/rename-20260911')
sys.path.insert(0, str(ROOT / 'mod-atlas-shop/tests'))
from hermes_protocol_client import BnetClient
from test_account_services_realm import Fixture
from test_hermes_identity_realm import IdentityClient


class SocialClient(IdentityClient):
    def __init__(self, *args, **kwargs):
        self.chat_packets = []
        super().__init__(*args, **kwargs)

    def receive(self):
        opcode, data = super().receive()
        if opcode == 0x2bad:
            self.chat_packets.append(data)
        return opcode, data


def run():
    f = Fixture(ROOT, 'custom-social-result.json')
    connections, active = [], []

    def pump(timeout=.15):
        for sock in select.select([c.socket for c in active], [], [], timeout)[0]:
            client = next(c for c in active if c.socket is sock)
            opcode, data = client.receive()
            if opcode == 11730:
                client.send(14909, data[:4] + struct.pack('<I', int(time.monotonic() * 1000) & 0xffffffff))

    def drain(predicate, timeout=30):
        deadline = time.monotonic() + timeout
        while True:
            value = predicate()
            if value:
                return value
            if time.monotonic() >= deadline:
                raise RuntimeError('Atlas social condition timed out.')
            pump()

    def provision():
        account = f.register()
        native = f.connect(account)
        assert native.create(f.name('Soc').capitalize()) == 0x2f
        guid = native.characters()[0]['guid']
        native.close()
        f.wait(lambda: f.sql('SELECT online FROM shop_test_auth.account WHERE id=' + str(account['id'])) == '0')
        f.sql('UPDATE shop_test_chars.characters SET level=10 WHERE guid=' + str(guid))
        return account, guid

    def enter(account):
        ticket = f.http(account, 'game-ticket', {})
        bnet = BnetClient(ticket['ticket'], account['id'])
        world = SocialClient(bnet)
        connections.extend([bnet, world])
        active.append(world)
        character = world.character()
        world.send(13817, struct.pack('<IB', 0, 0x80))
        world.send(13803, character['packedGuid'] + struct.pack('<f', 300.0))
        target = world.until(12365)
        instance = SocialClient(continued=(int.from_bytes(target[-8:], 'little'), world.key),
                                names=world.names, events=world.events)
        connections.append(instance)
        active.append(instance)
        instance.until(9623)
        f.wait(lambda: f.character(character['guid'])[2] == '1')
        world.send(13817, struct.pack('<IB', 0xffffffff, 0))
        drain(lambda: character['guid'] in world.names)
        return world, instance

    try:
        sender, sender_guid = provision()
        recipient, recipient_guid = provision()
        f.http(sender, 'friends/requests', {'username': recipient['username']})
        f.http(recipient, 'friends/' + str(sender['id']) + '/accept', {}, expected=204)
        sending, sending_instance = enter(sender)
        receiving, receiving_instance = enter(recipient)
        drain(lambda: f.sql('SELECT COUNT(*) FROM shop_test_chars.atlas_friend_binding WHERE '
            '(owner_guid=' + str(sender_guid) + ' AND friend_account_id=' + str(recipient['id']) + ') OR '
            '(owner_guid=' + str(recipient_guid) + ' AND friend_account_id=' + str(sender['id']) + ')') == '2', 45)
        f.check(True, 'An API friendship is synchronized into both real in-game friend bindings.')
        drain(lambda: f.sql('SELECT COUNT(*) FROM shop_test_chars.atlas_armory_combat_snapshot WHERE guid IN ('
            + str(sender_guid) + ',' + str(recipient_guid) + ')') == '2', 45)
        f.check(True, 'Armory live collection records both actual online synthetic characters.')
        nonce = uuid.uuid4().hex
        outgoing = 'atlas-launcher-to-game-' + nonce
        response = f.http(sender, 'chat/conversations/' + str(recipient['id']) + '/messages',
                         {'clientMessageId': str(uuid.uuid4()), 'body': outgoing})
        message_id = int(response['message']['id'])
        drain(lambda: any(e[0] == 'chat' and e[-1] == outgoing for e in receiving.events))
        drain(lambda: f.sql('SELECT CONCAT(status,CHAR(58),delivered_character_guid) '
            'FROM shop_test_auth.atlas_launcher_chat_outbox WHERE message_id=' + str(message_id))
            == '2:' + str(recipient_guid))
        packets = receiving.chat_packets + receiving_instance.chat_packets
        f.check(any((sender['username'] + '#Launcher').encode() in packet and outgoing.encode() in packet
                    for packet in packets),
                'A launcher message arrives over encrypted Hermes as a named #Launcher whisper and is acknowledged by the core.')
        reply = 'atlas-game-to-launcher-' + nonce
        receiving.whisper(sender['username'] + '#Launcher', reply)

        drain(lambda: f.sql('SELECT COUNT(*) FROM shop_test_auth.atlas_launcher_chat_message WHERE '
            'sender_account_id=' + str(recipient['id']) + ' AND recipient_account_id=' + str(sender['id'])
            + " AND body=CONVERT(X'" + reply.encode().hex() + "' USING utf8mb4)") == '1')
        messages = f.http(sender, 'chat/conversations/' + str(recipient['id']) + '/messages')['messages']
        stored = next(message for message in messages if message['body'] == reply)
        f.check(stored['origin'] == 'game' and stored['senderAccountId'] == recipient['id']
                and stored['recipientAccountId'] == sender['id'],
                'The native #Launcher reply crosses Hermes, the core inbox and API processing with the correct account identities.')
        drain(lambda: any(e[0] == 'chat' and e[-1] == reply for e in sending.events))
        f.check(True, 'The ingame recipient of that reply also receives the real named whisper.')
        f.save(True, 'complete')
    except Exception as error:
        f.save(False, type(error).__name__ + ': ' + str(error))
        raise
    finally:
        for connection in reversed(connections):
            connection.close()
        f.close()


if __name__ == '__main__':
    run()
