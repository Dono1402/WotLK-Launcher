#!/usr/bin/env python3
"""Small 3.4.3.54261 wire client for the disposable Hermes realm fixture.

Layouts follow the pinned Hermes f859d0c sources: BnetTcpSession, RpcTypes,
AuthenticationService, GameUtilities, AuthenticationPackets, CharacterPackets,
PacketCrypt and SessionKeyGeneration. This runs no game executable or UI.
Only loopback endpoints inside the fixture network namespace are accepted.
"""
import hashlib
import hmac
import json
import secrets
import socket
import ssl
import struct
import time
import zlib
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes


def varint(value):
    result = bytearray()
    while value > 127:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)


def field(number, value, fixed=False):
    if isinstance(value, str):
        value = value.encode()
    if isinstance(value, bytes):
        return varint(number * 8 + 2) + varint(len(value)) + value
    if fixed:
        return varint(number * 8 + 5) + struct.pack('<I', value)
    return varint(number * 8) + varint(value)


def protobuf(data):
    result, pos = {}, 0
    def read_varint():
        nonlocal pos
        value = 0
        for shift in range(0, 70, 7):
            part = data[pos]
            pos += 1
            value |= (part & 127) << shift
            if not part & 128:
                return value
        raise ValueError('Invalid protobuf varint.')
    while pos < len(data):
        tag = read_varint()
        number, wire = tag >> 3, tag & 7
        if wire == 0:
            value = read_varint()
        elif wire in (1, 5):
            size = 8 if wire == 1 else 4
            value = int.from_bytes(data[pos:pos + size], 'little')
            pos += size
        elif wire == 2:
            size = read_varint()
            value = data[pos:pos + size]
            pos += size
        else:
            raise ValueError('Unsupported protobuf wire type.')
        if pos > len(data):
            raise ValueError('Truncated protobuf field.')
        result.setdefault(number, []).append(value)
    return result


def one(fields, number, default=None):
    return fields.get(number, [default])[0]


def exact(connection, size):
    result = bytearray()
    while len(result) < size:
        part = connection.recv(size - len(result))
        if not part:
            raise RuntimeError('Fixture peer closed before the expected response.')
        result.extend(part)
    return bytes(result)


def attribute(name, value):
    kind = 5 if isinstance(value, str) else 6 if isinstance(value, bytes) else 9
    return field(1, field(1, name) + field(2, field(kind, value)))


def attributes(data):
    result = {}
    for encoded in protobuf(data).get(1, []):
        entry = protobuf(encoded)
        variant = protobuf(one(entry, 2))
        result[one(entry, 1).decode()] = next(iter(variant.values()))[0]
    return result


class BnetClient:
    def __init__(self, ticket, account_id):
        # The dedicated fixture uses the embedded development certificate.
        # TLS still encrypts transport; no system trust or security setting changes.
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        self.socket = context.wrap_socket(socket.create_connection(('127.0.0.1', 1119), timeout=20), server_hostname='localhost')
        self.certificate_sha256 = hashlib.sha256(self.socket.getpeercert(binary_form=True)).hexdigest()
        self.token = 0
        self.logon = None
        self.call(0x65446991, 1, field(3, 1))
        self.call(0x0DECFC01, 1, field(1, 'WoW') + field(2, 'Wn64') + field(3, 'frFR')
                  + field(6, 54261) + field(12, ticket))
        if self.logon is None or one(self.logon, 1, 0) != 0:
            raise RuntimeError('Expected a successful BNet LogonResult.')
        game_accounts = [one(protobuf(value), 2) for value in self.logon.get(3, [])]
        if account_id not in game_accounts:
            raise RuntimeError('BNet returned a different game account.')
        self.client_secret = secrets.token_bytes(32)
        identity = b'JSONRealmListTicketIdentity:' + json.dumps({'gameAccountID': account_id, 'gameAccountRegion': 1}).encode()
        info = b'JSONRealmListTicketClientInformation:' + json.dumps({'info': {'secret': list(self.client_secret), 'version': {'major': 3, 'minor': 4, 'revision': 3, 'build': 54261}}}).encode()
        self.utility('RealmListTicketRequest', '', {'Param_Identity': identity, 'Param_ClientInfo': info})
        realms = self.utility('RealmListRequest', '1-1-0', {})
        packed = realms['Param_RealmList']
        decoded = zlib.decompress(packed[4:]).rstrip(b'\0')
        self.realms = json.loads(decoded.split(b':', 1)[1])
        realm = self.realms['updates'][0]['update']
        self.realm_address = realm['wowRealmAddress']
        self.join_realm()

    def join_realm(self):
        joined = self.utility('RealmJoinRequest', '', {'Param_RealmAddress': self.realm_address})
        self.join_ticket = joined['Param_RealmJoinTicket']
        self.key = self.client_secret + joined['Param_JoinSecret']

    def send(self, service, method, token, body, response=False):
        header = field(1, 254 if response else 0) + field(2, method) + field(3, token)
        header += field(5, len(body)) + field(11, service, fixed=True)
        self.socket.sendall(struct.pack('>H', len(header)) + header + body)

    def call(self, service, method, body):
        self.token += 1
        token = self.token
        self.send(service, method, token, body)
        for _ in range(50):
            size = int.from_bytes(exact(self.socket, 2), 'big')
            if not 0 < size < 4096:
                raise RuntimeError('Unexpected BNet header length.')
            header = protobuf(exact(self.socket, size))
            body_size = one(header, 5, 0)
            if body_size > 1024 * 1024:
                raise RuntimeError('Unexpected BNet response length.')
            payload = exact(self.socket, body_size)
            if one(header, 1) == 254 and one(header, 3) == token:
                if one(header, 6, 0) != 0:
                    raise RuntimeError('BNet RPC failed with status ' + str(one(header, 6)))
                return payload
            if one(header, 11) == 0x71240E35 and one(header, 2) == 5:
                self.logon = protobuf(payload)
            self.send(one(header, 11, 0), one(header, 2, 0), one(header, 3, 0), b'', response=True)
        raise RuntimeError('No matching BNet RPC response.')

    def utility(self, command, value, params):
        payload = attribute('Command_' + command + '_v1_wotlk1', value)
        payload += b''.join(attribute(key, val) for key, val in params.items())
        return attributes(self.call(0x3FC1274D, 1, payload))

    def close(self):
        self.socket.close()


def mac(key, data):
    return hmac.new(key, data, hashlib.sha256).digest()


def session_key(seed):
    left, right = hashlib.sha256(seed[:16]).digest(), hashlib.sha256(seed[16:]).digest()
    first = hashlib.sha256(left + bytes(32) + right).digest()
    return first + hashlib.sha256(left + first + right).digest()[:8]


def packed_guid(data, offset):
    start = offset
    mask = int.from_bytes(data[offset:offset + 2], 'little')
    offset += 2
    full = bytearray(16)
    for i in range(16):
        if mask & 1 << i:
            full[i] = data[offset]
            offset += 1
    return data[start:offset], int.from_bytes(full[:8], 'little'), offset


class ModernWorldClient:
    def __init__(self, bnet=None, continued=None, abort_before_ack=False):
        self.socket = socket.create_connection(('127.0.0.1', 8086 if continued else 8084), timeout=30)
        self.send_count = self.recv_count = 0
        self.encryption_key = None
        banner = b'WORLD OF WARCRAFT CONNECTION - SERVER TO CLIENT - V2\n'
        if exact(self.socket, len(banner)) != banner:
            raise RuntimeError('Unexpected modern world banner.')
        self.socket.sendall(b'WORLD OF WARCRAFT CONNECTION - CLIENT TO SERVER - V2\n')
        opcode, challenge = self.receive()
        if opcode != 12360 or len(challenge) != 49:
            raise RuntimeError('Expected 3.4.3 SMSG_AUTH_CHALLENGE.')
        server = challenge[32:48]
        local = secrets.token_bytes(16)
        if continued:
            connect_key, self.key = continued
            digest = mac(self.key, struct.pack('<Q', connect_key) + local + server
                         + bytes.fromhex('16ad0cd446f94fb2ef7dea2a17664d2f'))[:24]
            self.send(14182, struct.pack('<QQ', 0, connect_key) + local + digest)
        else:
            digest_key = hashlib.sha256(bnet.key + bytes.fromhex('179d3dc3235629d07113a9b3867f97a7')).digest()
            digest = mac(digest_key, local + server + bytes.fromhex('c5c69895763f1dcdb6a13728b312ff8a'))[:24]
            address = bnet.realm_address
            payload = struct.pack('<QIII', 0, address >> 24, address >> 16 & 255, address & 65535)
            payload += local + digest + b'\0' + struct.pack('<I', len(bnet.join_ticket)) + bnet.join_ticket
            self.send(14181, payload)
            self.key = session_key(mac(hashlib.sha256(bnet.key).digest(), server + local
                                       + bytes.fromhex('58cbcf40fe2ecea65a90b801686c280b')))
        encryption_key = mac(self.key, local + server + bytes.fromhex('e9753c50909361da3b07eefaff9d41b8'))[:16]
        encrypted_mode = self.until(12361)
        if len(encrypted_mode) != 65 or encrypted_mode[-1] != 128:
            raise RuntimeError('Expected 3.4.3 Ed25519 encrypted-mode layout.')
        if abort_before_ack:
            self.close()
            return
        self.send(14183)
        self.encryption_key = encryption_key
        if continued:
            self.until(12363)
        elif int.from_bytes(self.until(9581)[:4], 'little') != 0:
            raise RuntimeError('Modern world authentication failed.')

    def receive(self):
        header = exact(self.socket, 16)
        size = int.from_bytes(header[:4], 'little')
        if not 2 <= size < 0x40000:
            raise RuntimeError('Invalid modern packet size.')
        payload = exact(self.socket, size)
        if self.encryption_key is not None:
            nonce = struct.pack('<Q', self.recv_count) + b'SRVR'
            decryptor = Cipher(algorithms.AES(self.encryption_key), modes.GCM(nonce, header[4:], min_tag_length=12)).decryptor()
            payload = decryptor.update(payload) + decryptor.finalize()
        self.recv_count += 1
        return int.from_bytes(payload[:2], 'little'), payload[2:]

    def send(self, opcode, body=b''):
        payload = struct.pack('<H', opcode) + body
        tag = bytes(12)
        if self.encryption_key is not None:
            nonce = struct.pack('<Q', self.send_count) + b'CLNT'
            encryptor = Cipher(algorithms.AES(self.encryption_key), modes.GCM(nonce)).encryptor()
            payload = encryptor.update(payload) + encryptor.finalize()
            tag = encryptor.tag[:12]
        self.send_count += 1
        self.socket.sendall(struct.pack('<I', len(payload)) + tag + payload)

    def until(self, wanted):
        for _ in range(2000):
            opcode, data = self.receive()
            if opcode == wanted:
                return data
            if opcode == 11730:
                self.send(14909, data[:4] + struct.pack('<I', int(time.monotonic() * 1000) & 0xFFFFFFFF))
        raise RuntimeError('Expected modern opcode not received: ' + str(wanted))

    def character(self):
        self.send(13801)
        data = self.until(9603)
        count, _, _, appearances, limits = struct.unpack_from('<IIIII', data, 1)
        if count != 1 or appearances or limits:
            raise RuntimeError('Expected one fixture character and no conditional appearance entries.')
        pos = 21 + (4 if data[0] & 2 else 0)
        raw, guid, pos = packed_guid(data, pos)
        pos += 8 + 4 + 4 + 1 + 4 + 4 + 12
        _, _, pos = packed_guid(data, pos)
        flags = int.from_bytes(data[pos:pos + 4], 'little')
        return {'packedGuid': raw, 'guid': guid, 'flags': flags, 'payload': data}

    def rename(self, character, name):
        self.send(14025, character['packedGuid'] + bytes([len(name.encode()) << 2]) + name.encode())
        return self.until(10087)[0]

    def close(self):
        self.socket.close()
