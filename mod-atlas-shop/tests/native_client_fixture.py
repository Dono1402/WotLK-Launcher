"""Private, pinned 3.4.3 CPU fixture; no executable, game UI or socket is launched."""
import hashlib
import json
import struct
from types import SimpleNamespace
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import *
EXPECTED_CLIENT_SHA256 = 'e240f9d88445643d2545ac54b3c3874b2f86336bc8947256584ef07bb5fd66a7'

def load_client_image(client_executable, snapshot_root):
    with client_executable.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest() != EXPECTED_CLIENT_SHA256:
            raise RuntimeError('The parser addresses apply only to the pinned 3.4.3.54261 executable.')
    metadata = json.loads((snapshot_root / 'image-sections.json').read_text())
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
        data = (snapshot_root / f'section-{index}.bin').read_bytes()
        if len(data) != section['size'] or section['rva'] < 4096:
            raise RuntimeError('Snapshot section length mismatch.')
        image[section['rva']:section['rva'] + len(data)] = data
        section_hashes[section['name']] = hashlib.sha256(data).hexdigest()
    n = SimpleNamespace(base=metadata['baseAddress'], image=bytes(image), section_hashes=section_hashes)
    del image

    return n

class Fixture:

    def __init__(self, n):
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
        for i, value in enumerate(args[4:]):
            self.put(sp + 40 + 8 * i, value, 'Q')
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
