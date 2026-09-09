"""Offline pattern evidence only: never opens a process or changes the executable."""

import argparse
import hashlib
import json
import pathlib
import re
import struct


NAMES = ("LoadByFileId", "LoadByFilePath", "CustomFileIdHook")


def compile_pattern(source, name):
    match = re.search(r"\b" + re.escape(name) + r"\s*=\s*\{([^}]+)\}", source)
    if not match:
        raise ValueError(f"Missing upstream pattern: {name}")
    parts = [part.strip() for part in match.group(1).split(",") if part.strip()]
    if not 8 <= len(parts) <= 256:
        raise ValueError("Unexpected pattern length")
    values = [int(part, 0) for part in parts]
    if any(value < -1 or value > 255 for value in values):
        raise ValueError("Unexpected pattern value")
    return re.compile(b"".join(b"." if value == -1 else re.escape(bytes([value]))
                               for value in values), re.DOTALL)


def pe_sections(data):
    if len(data) < 64 or data[:2] != b"MZ":
        raise ValueError("Expected a PE executable")
    offset = struct.unpack_from("<I", data, 60)[0]
    if offset + 24 > len(data) or data[offset:offset + 4] != b"PE\0\0":
        raise ValueError("Invalid PE header")
    machine, count = struct.unpack_from("<HH", data, offset + 4)
    optional_size = struct.unpack_from("<H", data, offset + 20)[0]
    table = offset + 24 + optional_size
    if machine != 0x8664 or count > 96 or table + count * 40 > len(data):
        raise ValueError("Expected bounded x64 PE sections")
    sections = []
    for index in range(count):
        entry = table + index * 40
        name = data[entry:entry + 8].rstrip(b"\0").decode("ascii", errors="replace")
        virtual_size, rva, raw_size, raw_offset = struct.unpack_from("<IIII", data, entry + 8)
        flags = struct.unpack_from("<I", data, entry + 36)[0]
        if raw_offset + raw_size > len(data):
            raise ValueError("PE section outside the file")
        sections.append({"name": name, "rva": rva, "rawOffset": raw_offset,
                         "rawBytes": raw_size, "virtualBytes": virtual_size,
                         "executable": bool(flags & 0x20000000)})
    return sections


def inspect(executable, patterns):
    if executable.stat().st_size > 256 * 1024**2 or patterns.stat().st_size > 1024**2:
        raise ValueError("Input exceeds offline diagnostic limits")
    data = executable.read_bytes()
    source_bytes = patterns.read_bytes()
    source = source_bytes.decode("utf-8-sig")
    sections = pe_sections(data)
    matches = {}
    for name in NAMES:
        found = []
        for match in compile_pattern(source, name).finditer(data):
            offset = match.start()
            section = next((s for s in sections if s["rawOffset"] <= offset
                            and offset + len(match[0]) <= s["rawOffset"] + s["rawBytes"]), None)
            found.append({"fileOffset": offset, "section": section["name"] if section else None,
                          "rva": section["rva"] + offset - section["rawOffset"] if section else None,
                          "executableSection": section["executable"] if section else False})
            if len(found) > 100:
                raise ValueError("Excessive ambiguous pattern matches")
        matches[name] = found
    return {"executableSha256": hashlib.sha256(data).hexdigest(),
            "upstreamPatternsSha256": hashlib.sha256(source_bytes).hexdigest(),
            "sections": sections, "patterns": matches,
            "uniqueExecutableMatches": all(len(values) == 1 and values[0]["executableSection"]
                                            for values in matches.values()),
            "runtimeCompatibilityValidated": False, "gameLaunched": False,
            "note": "On-disk matches do not validate live hooks; absent matches do not prove incompatibility with unpacked code."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=pathlib.Path, required=True)
    parser.add_argument("--patterns", type=pathlib.Path, required=True)
    arguments = parser.parse_args()
    print(json.dumps(inspect(arguments.executable, arguments.patterns), indent=2))
