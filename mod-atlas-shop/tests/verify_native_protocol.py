#!/usr/bin/env python3
"""Verify emitted Hermes VAS packets with isolated CPU emulation of client parsers.

Requires unicorn 2.1.4, a private initialized-section snapshot of the user's
3.4.3.54261 executable and the synthetic JSON emitted by the Hermes packet tests.
Does not launch a Windows executable, connect to a server, or exercise a game UI.
No client code or snapshot is shipped in this repository.
"""
import argparse
import hashlib
import json
import struct
from pathlib import Path
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import *
from native_client_fixture import EXPECTED_CLIENT_SHA256, Fixture, load_client_image
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--client-executable', type=Path, required=True)
parser.add_argument('--snapshot-root', type=Path, required=True)
parser.add_argument('--packets', type=Path, required=True)
parser.add_argument('--report', type=Path, required=True)
args = parser.parse_args()
n = load_client_image(args.client_executable, args.snapshot_root)
section_hashes = n.section_hashes
f = Fixture(n)
packets = json.loads(args.packets.read_text(encoding='utf-8'))
expected = {'distributions-0', 'distributions-1', 'distributions-2', 'distributions-100', 'consumed', 'revoked', 'products', 'purchases', 'states', 'assign-success', 'assign-rejected', 'characters', 'glue-enabled', 'glue-disabled'}
if set(packets) != expected:
    raise RuntimeError('The complete set of emitted packet fixtures is required.')
checks = []
# Exercise the client's conversion before checking server responses. Lua's
# ValueAddedServiceType.PaidNameChange is 4; the network request uses type 7.
# Capture the request at the transport boundary; no socket is opened.
requests = []
def capture_character_request():
    payload = f.get(f.reg(UC_X86_REG_RCX) + 0x20, 'Q')
    requests.append(f.read(payload, 8).hex())
    f.ret()
f.stubs[0x14156dc60] = capture_character_request
request_hook = f.u.hook_add(UC_HOOK_CODE, f.hook, begin=0x14156dc60, end=0x14156dc60)
f.put(0x1431f7828, 0)
f.invoke(0x141a4c410, (4,))
f.u.hook_del(request_hook)
assert requests == ['0100000007000000'], requests
checks.append('client-name-change-request-wire-type-7')
for name, hexdata in packets.items():
    p = bytes.fromhex(hexdata)
    if name.startswith('distributions-'):
        count = int(name.split('-')[1])
        o = f.parse(5376017040, p)
        assert f.get(o + 48, 'Q') == count
        if count:
            a = f.get(o + 40, 'Q')
            assert f.get(a, 'Q') == 1000 and f.get(a + (count - 1) * 21936, 'Q') == 999 + count
            assert f.invoke(5392411840, (a, 0)) == 4
    elif name in ('consumed', 'revoked'):
        o = f.parse(5376085696, p)
        assert f.get(o, 'Q') == 1000 and f.invoke(5392411840, (o, 0)) == 0
    elif name == 'products':
        f.parse(5376017600, p)
    elif name == 'purchases':
        f.parse(5375758224, p, True)
    elif name == 'states':
        f.parse(5375783440, p, True)
    elif name.startswith('glue-'):
        o = f.parse(0x1406bf640, p, True)
        # The real glue-status callback copies object+0x35 into the flag read by
        # C_CharacterServices.IsBoostEnabled, also used by the paid-name button.
        assert f.get(o + 0x35, 'B') == (name == 'glue-enabled')
        assert f.read(o + 0x20, 3) == bytes(3)  # The Blizzard store remains disabled.
    elif name.startswith('assign-'):
        o = f.parse(5375757584, p, True)
        event = []

        def capture():
            event.extend([f.reg(UC_X86_REG_RCX), f.reg(UC_X86_REG_RDX) & 255, f.reg(UC_X86_REG_R8)])
            f.ret()
        f.stubs[5386977872] = capture
        h = f.u.hook_add(UC_HOOK_CODE, f.hook, begin=5386977872, end=5386977872)
        f.invoke(5408044608, (0, o))
        f.u.hook_del(h)
        assert event == [73, 0 if name == 'assign-success' else 6, 0], event
    elif name == 'characters':
        o = f.parse(5375791888, p, True)
        assert f.get(o + 32) == 42 and f.get(o + 56, 'Q') == 1
        a = f.get(o + 48, 'Q')
        assert f.get(a, 'Q') == 51 and f.get(a + 8, 'Q') == 29 << 58 and (f.get(a + 16, 'Q') == 981) and (f.get(a + 24, 'Q') == 2 << 58)
        assert f.read(a + 342, 4) == bytes([1, 1, 0, 80]) and f.read(a + 36, 11) == b'Nativeproof'
        assert f.read(a + 0x55, 6) == b'Atlas\0'
        # Follow the decoded response into the actual store cache and the two
        # getters used by VASCharacterSelectBlockBase.CheckEnable.
        events = []
        def capture_character_list_event():
            events.append('character-list')
            f.ret()
        f.stubs[0x14117c550] = capture_character_list_event
        event_hook = f.u.hook_add(UC_HOOK_CODE, f.hook, begin=0x14117c550, end=0x14117c550)
        f.put(0x1431f75a8, 0)
        f.put(0x1431f75b0, 0, 'Q')
        f.put(0x1431f75b8, 0)
        f.put(0x1431f7828, 42)
        f.put(0x1431f782e, 0, 'B')
        f.invoke(0x141a4ac00, (o,))
        f.u.hook_del(event_hook)
        assert events == ['character-list'] and f.get(0x1431f75a8) == 1
        realms = f.obj + 0x10000
        f.invoke(0x141a47a10, (realms,))
        assert f.get(realms + 8, 'Q') == 1
        realm = f.get(realms, 'Q')
        assert f.read(f.get(realm, 'Q'), 6) == b'Atlas\0'
        characters = f.obj + 0x11000
        f.invoke(0x141a463a0, (characters, realm))
        assert f.get(characters + 8, 'Q') == 1
        checks.append('store-cache-and-character-selector-getters')
    else:
        raise AssertionError(name)
    checks.append(name)
report = {'clientBuild': '3.4.3.54261', 'clientExecutableSha256': EXPECTED_CLIENT_SHA256,
    'snapshotSectionSha256': section_hashes, 'packetFixtureSha256': hashlib.sha256(args.packets.read_bytes()).hexdigest(),
    'method': 'Actual client request conversion, response parsers, VAS classification and store-cache getters emulated on emitted Hermes packets; allocation/memory helpers, transport submission and event dispatch are replaced by host stubs.',
    'nameChangeCharacterRequestHex': requests[0], 'checks': checks,
    'limitations': 'No game UI, network session, or server consumption is validated by this protocol test.'}
args.report.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
