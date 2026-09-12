#!/usr/bin/env python3
"""Exercise the pinned client's name dispatcher, caches and unit-name getter.

Input is synthetic data emitted by PlayerIdentityPacketsTests. Private client
sections are required but are never distributed or executed as an application.
"""
import argparse
import hashlib
import json
import struct
from pathlib import Path
from unicorn import UC_HOOK_CODE
from native_client_fixture import EXPECTED_CLIENT_SHA256, Fixture, load_client_image

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--client-executable', type=Path, required=True)
parser.add_argument('--snapshot-root', type=Path, required=True)
parser.add_argument('--packets', type=Path, required=True)
parser.add_argument('--report', type=Path, required=True)
args = parser.parse_args()
n = load_client_image(args.client_executable, args.snapshot_root)
f = Fixture(n)
packets = json.loads(args.packets.read_text(encoding='utf-8'))
checks, notifications = [], []


def stub(address, callback):
    f.stubs[address] = callback
    f.u.hook_add(UC_HOOK_CODE, f.hook, begin=address, end=address)


# Allocation and byte-copy stubs are provided by Fixture. The following
# boundaries need an OS, transport, localization or a graphical object manager; none
# determines the decoded name or the cache's validity/key/value.
stub(0x14026bd4c, lambda: f.ret())
stub(0x141237be0, lambda: f.ret(123456))
stub(0x140266ca0, lambda: f.ret(0))
stub(0x14156dc60, lambda: f.ret())
stub(0x14172d2b0, lambda: f.ret())
stub(0x1419254f0, lambda: f.ret())
unknown = f.obj + 0x18000
stub(0x14063cc00, lambda: f.ret(unknown))


def name_ready():
    notifications.append('unit-name-ready')
    f.ret()


stub(0x1417a56f0, name_ready)
auth = bytes.fromhex(packets['auth'])
o = f.parse(0x1406b8730, auth, constructor=True)
assert f.get(o + 0x38, 'Q') == 1
realm = f.get(o + 0x30, 'Q')
assert f.get(realm) == packets['realm']
f.invoke(0x1416718a0, (f.get(realm), realm + 4))
checks.append('native-authentication-parser-and-realm-cache')
f.invoke(0x1403512d0)  # Native registration of both name-cache handlers.
f.put(0x142d1a190, 100, 'Q')  # Bound the native outgoing name-query batch.
f.u.mem_write(unknown, b'UNKNOWN\0')
unit = f.obj + 0x20000
f.put(unit + 0x10, 7, 'B')  # ActivePlayer type, with no unit-name override.
f.u.mem_write(unit + 0x18, struct.pack('<QQ', packets['guidLow'], packets['guidHigh']))


def dispatch(opcode, payload):
    f.u.mem_write(f.buf, bytes(4096))
    f.u.mem_write(f.data, payload)
    f.put(f.buf + 8, f.data, 'Q')
    f.put(f.buf + 20, len(payload))
    f.put(f.buf + 24, len(payload))
    # The actual native opcode dispatcher constructs the response, invokes
    # its registered handler and disposes the packet. No Python decoder is
    # involved in inserting or looking up the character's name.
    assert f.invoke(0x1407429f0, (0, 0, 0, opcode, f.buf)) & 255 == 1
    assert f.get(f.buf + 28) == len(payload)


def unit_name():
    address = f.invoke(0x141799970, (unit, 0, 1))
    return f.read(address, 49).split(b'\0')[0].decode('utf-8')


for index, (name, hexdata) in enumerate(packets['names'].items()):
    before = len(notifications)
    if index:
        dispatch(0x2fff, bytes.fromhex(packets['invalidate']))
        assert unit_name() == 'UNKNOWN'
        checks.append('invalidation-clears-prior-native-name-' + str(index))
    dispatch(0x301b, bytes.fromhex(hexdata))
    assert unit_name() == name
    checks.append('native-dispatch-cache-and-unit-getter-' + str(index))
    if index:
        assert len(notifications) == before + 1
        checks.append('native-pending-unit-callback-notified-' + str(index))

report = {
    'passed': True, 'clientBuild': '3.4.3.54261',
    'clientExecutableSha256': EXPECTED_CLIENT_SHA256,
    'snapshotSectionSha256': n.section_hashes,
    'packetFixtureSha256': hashlib.sha256(args.packets.read_bytes()).hexdigest(),
    'method': 'Actual opcode dispatchers, packet constructors, realm/name caches, invalidation, pending-name callbacks and CGUnit name getter emulated on emitted Hermes packets.',
    'checks': checks, 'nativeNameReadyCallbacks': len(notifications),
    'limitations': 'OS allocation, clock, transport, localization and graphics notification boundaries are stubbed. This test does not reproduce a full loading screen or prove visible in-game rendering.'
}
args.report.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
