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
from types import SimpleNamespace
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import *
EXPECTED_CLIENT_SHA256 = 'e240f9d88445643d2545ac54b3c3874b2f86336bc8947256584ef07bb5fd66a7'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--client-executable', type=Path, required=True)
parser.add_argument('--snapshot-root', type=Path, required=True)
parser.add_argument('--packets', type=Path, required=True)
parser.add_argument('--report', type=Path, required=True)
args = parser.parse_args()
with args.client_executable.open('rb') as stream:
    if hashlib.file_digest(stream, 'sha256').hexdigest() != EXPECTED_CLIENT_SHA256:
        raise RuntimeError('The parser addresses apply only to the pinned 3.4.3.54261 executable.')
metadata = json.loads((args.snapshot_root / 'image-sections.json').read_text())
if metadata['baseAddress'] != 5368709120:
    raise RuntimeError('This verification expects the recorded image base.')
size = max((section['rva'] + section['size'] for section in metadata['sections']))
if size > 268435456 or size < 77594624:
    raise RuntimeError('Unexpected snapshot size.')
image = bytearray(size)
section_hashes = {}
for section in metadata['sections']:
    index = section['index']
    if not isinstance(index, int) or not 0 <= index <= 6 or section['missing'] != 0:
        raise RuntimeError('Incomplete or invalid snapshot section.')
    data = (args.snapshot_root / f'section-{index}.bin').read_bytes()
    if len(data) != section['size'] or section['rva'] < 4096:
        raise RuntimeError('Snapshot section length mismatch.')
    image[section['rva']:section['rva'] + len(data)] = data
    section_hashes[section['name']] = hashlib.sha256(data).hexdigest()
n = SimpleNamespace(base=metadata['baseAddress'], image=bytes(image))
del image

class Fixture:

    def __init__(self):
        self.u = Uc(UC_ARCH_X86, UC_MODE_64)
        self.u.mem_map(n.base, len(n.image) + 4095 & ~4095)
        self.u.mem_write(n.base, n.image)
        self.heap = 8589934592
        self.stack = 8858370048
        self.obj = 9126805504
        self.buf = 9395240960
        self.data = self.buf + 4096
        self.end = 9663676416
        for a, z in ((self.heap, 33554432), (self.stack, 1048576), (self.obj, 2097152), (self.buf, 4194304), (self.end, 4096)):
            self.u.mem_map(a, z)
        self.cursor = self.heap
        self.stubs = {5371154752: self.allocate, 5371154960: self.free, 5408291408: self.memset, 5408289712: self.memcpy}
        for addr in self.stubs:
            self.u.hook_add(UC_HOOK_CODE, self.hook, begin=addr, end=addr)

    def read(self, a, size):
        return bytes(self.u.mem_read(a, size))

    def put(self, a, v, fmt='I'):
        self.u.mem_write(a, struct.pack('<' + fmt, v))

    def get(self, a, fmt='I'):
        return struct.unpack('<' + fmt, self.read(a, struct.calcsize(fmt)))[0]

    def reg(self, r):
        return self.u.reg_read(r)

    def ret(self, value=0):
        sp = self.reg(UC_X86_REG_RSP)
        self.u.reg_write(UC_X86_REG_RAX, value)
        self.u.reg_write(UC_X86_REG_RSP, sp + 8)
        self.u.reg_write(UC_X86_REG_RIP, self.get(sp, 'Q'))

    def allocate(self):
        size = self.reg(UC_X86_REG_RCX)
        assert size < 16777216
        addr = self.cursor
        self.cursor += size + 15 & ~15
        assert self.cursor < self.heap + 33554432
        self.ret(addr)

    def free(self):
        self.ret()

    def memset(self):
        a = self.reg(UC_X86_REG_RCX)
        count = self.reg(UC_X86_REG_R8)
        assert count < 16777216
        self.u.mem_write(a, bytes([self.reg(UC_X86_REG_RDX) & 255]) * count)
        self.ret(a)

    def memcpy(self):
        a = self.reg(UC_X86_REG_RCX)
        count = self.reg(UC_X86_REG_R8)
        assert count < 16777216
        self.u.mem_write(a, self.read(self.reg(UC_X86_REG_RDX), count))
        self.ret(a)

    def hook(self, uc, address, size, data):
        self.stubs[address]()

    def invoke(self, entry, args=()):
        sp = self.stack + 983048
        self.put(sp, self.end, 'Q')
        for r, v in zip((UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9), args):
            self.u.reg_write(r, v)
        self.put(sp + 40, 0)
        self.u.reg_write(UC_X86_REG_RSP, sp)
        try:
            self.u.emu_start(entry, self.end, timeout=5000000, count=1000000)
        except Exception as e:
            raise RuntimeError(f'{hex(entry)} stopped at {hex(self.reg(UC_X86_REG_RIP))}: {e}') from e
        assert self.reg(UC_X86_REG_RIP) == self.end, 'instruction/timeout limit reached'
        return self.reg(UC_X86_REG_RAX)

    def parse(self, entry, payload, constructor=False):
        self.u.mem_write(self.obj, bytes(2097152))
        self.u.mem_write(self.buf, bytes(4096))
        self.u.mem_write(self.data, payload)
        self.put(self.buf + 8, self.data, 'Q')
        self.put(self.buf + 20, len(payload))
        self.put(self.buf + 24, len(payload))
        self.put(self.buf + 28, 0)
        args = (self.obj, 0, 0, self.buf) if constructor else (self.obj, self.buf, 1)
        self.invoke(entry, args)
        consumed = self.get(self.buf + 28)
        assert consumed == len(payload), (hex(entry), consumed, len(payload))
        return self.obj
f = Fixture()
packets = json.loads(args.packets.read_text(encoding='utf-8'))
expected = {'distributions-0', 'distributions-1', 'distributions-2', 'distributions-100', 'consumed', 'revoked', 'products', 'purchases', 'states', 'assign-success', 'assign-rejected', 'characters', 'glue-enabled', 'glue-disabled'}
if set(packets) != expected:
    raise RuntimeError('The complete set of emitted packet fixtures is required.')
checks = []
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
    else:
        raise AssertionError(name)
    checks.append(name)
report = {'clientBuild': '3.4.3.54261', 'clientExecutableSha256': EXPECTED_CLIENT_SHA256, 'snapshotSectionSha256': section_hashes, 'method': 'Actual client parsers and VAS classification/response callback emulated on emitted Hermes packets; only allocator, free, memcpy and memset are replaced by host stubs.', 'checks': checks, 'limitations': 'No game UI, network session, or server consumption is validated by this protocol test.'}
args.report.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(report, indent=2))
