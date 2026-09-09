"""Read a public MediaFire ZIP inventory and small text files without extracting it.

This diagnostic never starts a game, runs archive contents, writes game files,
or bypasses authentication. A refused HTTP request or ignored Range aborts it.
The output is an inventory, not proof that the full package is safe/compatible.
"""

import argparse
import hashlib
import html.parser
import io
import json
import os
import pathlib
import struct
import subprocess
import tempfile
import threading
import urllib.parse
import urllib.request
import zipfile


class DownloadLinkParser(html.parser.HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.links = []

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if tag == "a" and values.get("id") == "downloadButton":
            self.links.append(values.get("href", ""))


def validate_mediafire_url(url):
    parts = urllib.parse.urlsplit(url)
    if (
        parts.scheme != "https"
        or parts.username is not None
        or parts.password is not None
        or parts.port not in (None, 443)
        or not parts.hostname
        or not (parts.hostname == "mediafire.com" or parts.hostname.endswith(".mediafire.com"))
    ):
        raise ValueError("Expected an HTTPS MediaFire URL without credentials")


class MediaFireRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        validate_mediafire_url(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def resolve_download(page_url, opener):
    validate_mediafire_url(page_url)
    with opener.open(page_url, timeout=25) as response:
        page = response.read(2 * 1024 * 1024 + 1)
    if len(page) > 2 * 1024 * 1024:
        raise ValueError("Download page exceeds the diagnostic size limit")
    parser = DownloadLinkParser()
    parser.feed(page.decode("utf-8"))
    if len(parser.links) != 1:
        raise ValueError("Public download button missing or ambiguous; no fallback attempted")
    validate_mediafire_url(parser.links[0])
    return parser.links[0]


class HttpRangeReader(io.RawIOBase):
    MAX_READ = 8 * 1024 * 1024
    MAX_TRANSFER = 32 * 1024 * 1024

    def __init__(self, url, opener):
        self.url = url
        self.opener = opener
        with opener.open(urllib.request.Request(url, method="HEAD"), timeout=25) as response:
            self.size = int(response.headers["Content-Length"])
            self.etag = response.headers.get("ETag")
        if not 22 <= self.size <= 4 * 1024**3:
            raise ValueError("Unexpected archive size")
        self.position = 0
        self.transferred = 0
        self.requests = 0

    def readable(self):
        return True

    def seekable(self):
        return True

    def tell(self):
        return self.position

    def seek(self, offset, whence=io.SEEK_SET):
        base = {io.SEEK_SET: 0, io.SEEK_CUR: self.position, io.SEEK_END: self.size}[whence]
        position = base + offset
        if not 0 <= position <= self.size:
            raise ValueError("Seek outside archive")
        self.position = position
        return position

    def read(self, size=-1):
        size = self.size - self.position if size < 0 else min(size, self.size - self.position)
        if not size:
            return b""
        if size > self.MAX_READ or self.transferred + size > self.MAX_TRANSFER:
            raise ValueError("Diagnostic HTTP range budget exceeded")
        end = self.position + size - 1
        headers = {"Range": f"bytes={self.position}-{end}", "Accept-Encoding": "identity"}
        if self.etag:
            headers["If-Match"] = self.etag
        request = urllib.request.Request(self.url, headers=headers)
        with self.opener.open(request, timeout=25) as response:
            expected_range = f"bytes {self.position}-{end}/{self.size}"
            if response.status != 206 or response.headers.get("Content-Range") != expected_range:
                raise ValueError("Server refused or ignored the exact partial download")
            if self.etag and response.headers.get("ETag") != self.etag:
                raise ValueError("Archive ETag changed")
            data = response.read(size + 1)
        if len(data) != size:
            raise ValueError("Truncated or oversized partial response")
        self.position += size
        self.transferred += size
        self.requests += 1
        return data


def validate_archive_entry(entry):
    name = entry.filename.replace("\\", "/")
    if (name.startswith("/") or ".." in name.split("/") or ":" in name
            or "\x00" in entry.orig_filename):
        raise ValueError("Unsafe archive path; diagnostic aborted")
    if entry.flag_bits & 1:
        raise ValueError("Encrypted archive entry; no password attempted")
    return name


def run_bounded(command, output_limit, timeout=25):
    """Bound decoder output as it is produced, not after capture_output buffers it."""
    with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL) as process:
        expired = threading.Event()

        def stop():
            expired.set()
            if process.poll() is None:
                process.kill()

        timer = threading.Timer(timeout, stop)
        timer.daemon = True
        timer.start()
        try:
            output = process.stdout.read(output_limit + 1)
            if len(output) > output_limit:
                raise ValueError("Decoder exceeded the declared text size")
            return_code = process.wait(timeout=timeout)
            if expired.is_set():
                raise TimeoutError("Text decoder exceeded the time limit")
            if return_code:
                raise subprocess.CalledProcessError(return_code, command)
            return output
        finally:
            timer.cancel()
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)


def read_text_entry(archive, reader, entry, seven_zip, scratch_dir):
    validate_archive_entry(entry)
    if not 0 <= entry.file_size <= 1024 * 1024:
        raise ValueError("Decoded text exceeds the diagnostic limit")
    if entry.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED, zipfile.ZIP_BZIP2, zipfile.ZIP_LZMA):
        with archive.open(entry) as stream:
            return stream.read(entry.file_size + 1)
    if entry.compress_type != 9 or seven_zip is None:
        raise ValueError("This text entry needs the installed 7-Zip CLI for Deflate64")
    if not 0 < entry.compress_size <= 1024 * 1024:
        raise ValueError("Compressed text exceeds the diagnostic limit")
    reader.seek(entry.header_offset)
    header = reader.read(30)
    if header[:4] != b"PK\x03\x04":
        raise ValueError("Unexpected ZIP local header")
    name_length, extra_length = struct.unpack_from("<HH", header, 26)
    local_name = reader.read(name_length)
    if local_name.decode("utf-8" if entry.flag_bits & 0x800 else "cp437") != entry.filename:
        raise ValueError("ZIP local name does not match the directory")
    reader.seek(extra_length, io.SEEK_CUR)
    compressed = reader.read(entry.compress_size)
    # Construct a bounded, single-member temporary ZIP; 7-Zip needs a seekable file.
    # Only diagnostic text is returned on stdout, no archive member is executed or extracted.
    name = b"diagnostic.txt"
    common = (9, 0, 0, entry.CRC, len(compressed), entry.file_size, len(name), 0)
    local = struct.pack("<4sHHHHHIIIHH", b"PK\x03\x04", 21, 0, *common) + name + compressed
    central = struct.pack("<4sHHHHHHIIIHHHHHII", b"PK\x01\x02", 21, 21, 0,
                          *common, 0, 0, 0, 0, 0) + name
    end = struct.pack("<4sHHHHIIH", b"PK\x05\x06", 0, 0, 1, 1, len(central), len(local), 0)
    if scratch_dir is None:
        raise ValueError("Deflate64 requires an explicit workspace scratch directory")
    root = pathlib.Path(scratch_dir).resolve(strict=True)
    handle, temporary_name = tempfile.mkstemp(prefix="leeviathan-text-", suffix=".zip", dir=root)
    temporary_path = pathlib.Path(temporary_name).resolve(strict=True)
    if temporary_path.parent != root:
        os.close(handle)
        raise ValueError("Temporary ZIP did not stay in the requested directory")
    try:
        with os.fdopen(handle, "wb") as temporary_file:
            temporary_file.write(local + central + end)
        data = run_bounded([seven_zip, "x", "-so", "-tzip", "-bd", "-bb0", str(temporary_path)],
                           entry.file_size)
    finally:
        # Delete only the unique file created above; never recursively remove a directory.
        temporary_path.unlink()
    if len(data) != entry.file_size or (zipfile.crc32(data) & 0xffffffff) != entry.CRC:
        raise ValueError("Decoded text size or ZIP CRC mismatch")
    return data


def inspect(page_url, seven_zip=None, scratch_dir=None):
    opener = urllib.request.build_opener(MediaFireRedirectHandler())
    reader = HttpRangeReader(resolve_download(page_url, opener), opener)
    with zipfile.ZipFile(reader) as archive:
        entries = archive.infolist()
        if len(entries) > 10000:
            raise ValueError("Unexpectedly large ZIP directory")
        files = []
        texts = []
        for entry in entries:
            name = validate_archive_entry(entry)
            files.append({
                "path": name,
                "bytes": entry.file_size,
                "compressedBytes": entry.compress_size,
                "compression": entry.compress_type,
                "crc32": f"{entry.CRC:08x}",
                "directory": entry.is_dir(),
            })
            if not entry.is_dir() and name.lower().endswith(".txt") and entry.file_size <= 1024 * 1024:
                if len(texts) >= 20:
                    raise ValueError("Too many small text files")
                if entry.compress_type not in (0, 8, 12, 14) and seven_zip is None:
                    texts.append({"path": name, "compression": entry.compress_type,
                                  "textRead": False, "reason": "Unsupported by Python; installed 7-Zip required"})
                    continue
                data = read_text_entry(archive, reader, entry, seven_zip, scratch_dir)
                texts.append({
                    "path": name,
                    "sha256": hashlib.sha256(data).hexdigest(),
                    "zipCrcVerified": True,
                    "text": data.decode("utf-8-sig", errors="replace"),
                })
    return {
        "sourcePage": page_url,
        "archiveBytes": reader.size,
        "archiveFullSha256": None,
        "httpBytesRead": reader.transferred,
        "rangeRequests": reader.requests,
        "etagAvailable": reader.etag is not None,
        "files": files,
        "textFiles": texts,
        "archiveExtracted": False,
        "archiveContentsExecuted": False,
        "clientOrLauncherOpened": False,
        "installed": False,
        "compatibilityValidated": False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--page-url", required=True)
    parser.add_argument("--seven-zip", help="Optional installed 7z.exe path for Deflate64 text only")
    parser.add_argument("--scratch-dir", help="Existing workspace directory for temporary single-text ZIPs")
    args = parser.parse_args()
    print(json.dumps(inspect(args.page_url, args.seven_zip, args.scratch_dir), indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
