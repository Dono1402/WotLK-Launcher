"""Read-only MPQ inspection with an explicitly supplied, trusted StormLib DLL.

The archive is opened read-only with sector-CRC checks. No downloaded content
is executed. Optional extraction is limited to named assets in a separate root.
"""

import argparse
import collections
import ctypes
import hashlib
import json
import pathlib
import struct


MAX_FILE_BYTES = 64 * 1024**2
STORMLIB_V940_X64_SHA256 = "93321f6f030be5d7d79eb4cbf433d6ef7e83ce20d47d8963717f207d54afbe16"


def safe_asset_path(name):
    path = pathlib.PurePosixPath(name.replace("\\", "/"))
    reserved = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}
    if (path.is_absolute() or ".." in path.parts or any(c in name for c in ':<>"|?*')
            or any(ord(c) < 32 for c in name)
            or any(part.rstrip(" .") != part or part.split(".")[0].upper() in reserved for part in path.parts)):
        raise ValueError("Unsafe asset path")
    if path.suffix.lower() not in (".m2", ".skin", ".anim", ".blp", ".dbc", ".db2"):
        raise ValueError("Only model-related data formats can be extracted")
    return path


class Mpq:
    def __init__(self, dll_path, archive_path):
        self.archive_path = archive_path.resolve(strict=True)
        dll = dll_path.resolve(strict=True)
        if hashlib.sha256(dll.read_bytes()).hexdigest() != STORMLIB_V940_X64_SHA256:
            raise ValueError("Expected the verified official StormLib v9.40 x64 DLL")
        self.lib = ctypes.WinDLL(str(dll), use_last_error=True)
        signatures = {
            "SFileOpenArchive": (ctypes.c_bool, [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p)]),
            "SFileCloseArchive": (ctypes.c_bool, [ctypes.c_void_p]),
            "SFileOpenFileEx": (ctypes.c_bool, [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p)]),
            "SFileGetFileSize": (ctypes.c_uint32, [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32)]),
            "SFileReadFile": (ctypes.c_bool, [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32), ctypes.c_void_p]),
            "SFileCloseFile": (ctypes.c_bool, [ctypes.c_void_p]),
        }
        for name, (result, arguments) in signatures.items():
            function = getattr(self.lib, name)
            function.restype, function.argtypes = result, arguments
        self.handle = ctypes.c_void_p()
        # MPQ_OPEN_READ_ONLY | MPQ_OPEN_CHECK_SECTOR_CRC, from the v9.40 header.
        if not self.lib.SFileOpenArchive(str(self.archive_path), 0, 0x00100100, ctypes.byref(self.handle)):
            raise ctypes.WinError(ctypes.get_last_error())

    def read(self, name):
        file_handle = ctypes.c_void_p()
        if not self.lib.SFileOpenFileEx(self.handle, name.replace("/", "\\").encode("utf-8"), 0, ctypes.byref(file_handle)):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            high = ctypes.c_uint32()
            low = self.lib.SFileGetFileSize(file_handle, ctypes.byref(high))
            length = (high.value << 32) | low
            if length > MAX_FILE_BYTES:
                raise ValueError("MPQ entry exceeds the pilot size limit")
            buffer = ctypes.create_string_buffer(length)
            count = ctypes.c_uint32()
            if not self.lib.SFileReadFile(file_handle, buffer, length, ctypes.byref(count), None):
                raise ctypes.WinError(ctypes.get_last_error())
            if count.value != length:
                raise ValueError("Truncated MPQ entry")
            return buffer.raw[:length]
        finally:
            self.lib.SFileCloseFile(file_handle)

    def close(self):
        if self.handle:
            self.lib.SFileCloseArchive(self.handle)
            self.handle = None


def summary(data):
    result = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(),
              "magic": data[:4].decode("ascii", errors="replace")}
    if data[:4] == b"MD20" and len(data) >= 8:
        result["m2Version"] = struct.unpack_from("<I", data, 4)[0]
    elif data[:4] == b"MD21":
        chunks, offset = [], 0
        while offset < len(data):
            if offset + 8 > len(data):
                raise ValueError("Truncated chunk header")
            tag, size = struct.unpack_from("<4sI", data, offset)
            if offset + 8 + size > len(data):
                raise ValueError("Chunk exceeds file bounds")
            chunks.append({"tag": tag.decode("ascii", errors="replace"), "bytes": size})
            offset += 8 + size
        result["chunks"] = chunks
    return result


def extract_asset(root, name, data):
    root = root.resolve(strict=True)
    relative = safe_asset_path(name)
    destination = root.joinpath(*relative.parts)
    if not destination.resolve().is_relative_to(root):
        raise ValueError("Extraction path leaves output directory")
    destination.parent.mkdir(parents=True, exist_ok=True)
    # Never replace an existing local file, including an earlier pilot output.
    with destination.open("xb") as output:
        output.write(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stormlib", type=pathlib.Path, required=True)
    parser.add_argument("--archive", type=pathlib.Path, required=True)
    parser.add_argument("--asset", action="append", default=[])
    parser.add_argument("--output-dir", type=pathlib.Path)
    arguments = parser.parse_args()
    output_root = arguments.output_dir.resolve(strict=True) if arguments.output_dir else None
    if output_root and (not output_root.is_dir() or output_root == output_root.parent
                        or arguments.archive.resolve().is_relative_to(output_root)):
        raise ValueError("Use an existing separate output folder, not the archive or a drive root")
    if len(arguments.asset) > 256:
        raise ValueError("Too many pilot assets requested")
    for name in arguments.asset:
        safe_asset_path(name)
    archive = Mpq(arguments.stormlib, arguments.archive)
    try:
        listing = archive.read("(listfile)").decode("utf-8-sig")
        names = sorted({name for name in listing.splitlines() if name})
        counts = collections.Counter(pathlib.PureWindowsPath(name).suffix.lower() for name in names)
        results = {"entryCount": len(names), "extensions": dict(sorted(counts.items())),
                   "humanMaleEntries": [name for name in names if name.lower().startswith("character\\human\\male\\")],
                   "databaseEntries": [name for name in names if name.lower().endswith((".dbc", ".db2"))],
                   "assets": {}, "archiveReadOnly": True, "gameLaunched": False}
        for name in arguments.asset:
            data = archive.read(name)
            results["assets"][name] = summary(data)
            if output_root:
                extract_asset(output_root, name, data)
        print(json.dumps(results, indent=2))
    finally:
        archive.close()


if __name__ == "__main__":
    main()
