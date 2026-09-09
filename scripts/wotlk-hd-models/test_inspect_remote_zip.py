"""Offline tests: no network, game, launcher, or third-party archive execution."""

import importlib.util
import io
import pathlib
import subprocess
import sys
import unittest
import urllib.request
import zipfile

spec = importlib.util.spec_from_file_location(
    "inspect_remote_zip", pathlib.Path(__file__).with_name("inspect_remote_zip.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Response(io.BytesIO):
    def __init__(self, payload=b"", status=200, **headers):
        super().__init__(payload)
        self.status = status
        self.headers = headers


class Opener:
    def __init__(self, *responses):
        self.responses = iter(responses)
        self.requests = []

    def open(self, request, timeout):
        self.requests.append(request)
        return next(self.responses)


class DiagnosticTests(unittest.TestCase):
    def reader(self, response, etag=None):
        headers = {"Content-Length": "100"}
        if etag:
            headers["ETag"] = etag
        opener = Opener(Response(**headers), response)
        return module.HttpRangeReader("https://download1.mediafire.com/public.zip", opener), opener

    def test_https_mediafire_allowlist(self):
        for url in ("https://mediafire.com/file/a", "https://download1.mediafire.com/a"):
            module.validate_mediafire_url(url)
        for url in ("http://mediafire.com/a", "https://mediafire.com.evil.test/a",
                    "https://evilmediafire.com/a", "https://user:pass@mediafire.com/a",
                    "https://mediafire.com:123/a", "file:///C:/example"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                module.validate_mediafire_url(url)

    def test_redirect_cannot_leave_allowlist(self):
        with self.assertRaises(ValueError):
            module.MediaFireRedirectHandler().redirect_request(
                urllib.request.Request("https://mediafire.com/a"), None, 302, "", {},
                "https://example.com/a")

    def test_public_download_button(self):
        opener = Opener(Response(b'<a id="downloadButton" href="https://download1.mediafire.com/a">file</a>'))
        self.assertEqual(module.resolve_download("https://mediafire.com/a", opener),
                         "https://download1.mediafire.com/a")

    def test_missing_or_ambiguous_download_button(self):
        for page in (b"Please sign in", b'<a id="downloadButton"></a>' * 2):
            with self.subTest(page=page), self.assertRaises(ValueError):
                module.resolve_download("https://mediafire.com/a", Opener(Response(page)))

    def test_large_page_aborts(self):
        with self.assertRaises(ValueError):
            module.resolve_download("https://mediafire.com/a", Opener(Response(b"x" * (2 * 1024**2 + 1))))

    def test_range_and_etag(self):
        reader, opener = self.reader(Response(b"abc", 206, **{
            "Content-Range": "bytes 10-12/100", "ETag": '"same"'}), '"same"')
        reader.seek(10)
        self.assertEqual(reader.read(3), b"abc")
        self.assertEqual((reader.tell(), reader.transferred, reader.requests), (13, 3, 1))
        headers = {key.lower(): value for key, value in opener.requests[-1].header_items()}
        self.assertEqual(headers["range"], "bytes=10-12")
        self.assertEqual(headers["if-match"], '"same"')
        self.assertEqual(headers["accept-encoding"], "identity")

    def test_refused_or_wrong_range(self):
        for status, content_range in ((200, "bytes 0-2/100"), (206, "bytes 1-3/100")):
            reader, _ = self.reader(Response(b"abc", status, **{"Content-Range": content_range}))
            with self.subTest(status=status), self.assertRaises(ValueError):
                reader.read(3)

    def test_truncated_or_oversized_response(self):
        for payload in (b"ab", b"abcd"):
            reader, _ = self.reader(Response(payload, 206, **{"Content-Range": "bytes 0-2/100"}))
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                reader.read(3)

    def test_changed_etag(self):
        reader, _ = self.reader(Response(b"abc", 206, **{
            "Content-Range": "bytes 0-2/100", "ETag": '"changed"'}), '"same"')
        with self.assertRaises(ValueError):
            reader.read(3)

    def test_seek_boundaries_and_empty_end_read(self):
        reader, opener = self.reader(Response())
        self.assertEqual(reader.seek(-3, io.SEEK_END), 97)
        self.assertEqual(reader.seek(3, io.SEEK_CUR), 100)
        self.assertEqual(reader.read(1), b"")
        self.assertEqual(len(opener.requests), 1)
        for offset in (-1, 101):
            with self.subTest(offset=offset), self.assertRaises(ValueError):
                reader.seek(offset)

    def test_transfer_budgets(self):
        reader, opener = self.reader(Response())
        reader.MAX_READ = 2
        with self.assertRaises(ValueError):
            reader.read(3)
        reader.MAX_READ = 100
        reader.transferred = reader.MAX_TRANSFER
        with self.assertRaises(ValueError):
            reader.read(1)
        self.assertEqual(len(opener.requests), 1)

    def test_archive_size_limit(self):
        for size in (21, 4 * 1024**3 + 1):
            with self.subTest(size=size), self.assertRaises(ValueError):
                module.HttpRangeReader("https://mediafire.com/a", Opener(Response(**{"Content-Length": str(size)})))

    def test_archive_paths(self):
        self.assertEqual(module.validate_archive_entry(zipfile.ZipInfo("models\\human.m2")), "models/human.m2")
        for name in ("../file", "x/../file", "/root", "C:/file", "file:stream", "a\x00b", "\\\\host\\file"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                module.validate_archive_entry(zipfile.ZipInfo(name))

    def test_encrypted_entry(self):
        entry = zipfile.ZipInfo("info.txt")
        entry.flag_bits = 1
        with self.assertRaises(ValueError):
            module.validate_archive_entry(entry)

    def test_standard_text_and_crc(self):
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("info.txt", b"diagnostic")
        with zipfile.ZipFile(data) as archive:
            self.assertEqual(module.read_text_entry(archive, data, archive.getinfo("info.txt"), None, None),
                             b"diagnostic")

    def test_declared_text_limit(self):
        entry = zipfile.ZipInfo("info.txt")
        entry.file_size = 1024**2 + 1
        with self.assertRaises(ValueError):
            module.read_text_entry(None, None, entry, None, None)

    def test_deflate64_requires_explicit_decoder(self):
        entry = zipfile.ZipInfo("info.txt")
        entry.compress_type = 9
        with self.assertRaises(ValueError):
            module.read_text_entry(None, None, entry, None, None)

    def test_decoder_output_limit(self):
        with self.assertRaises(ValueError):
            module.run_bounded([sys.executable, "-I", "-B", "-c", "print('x' * 10000)"], 10)

    def test_decoder_failure(self):
        with self.assertRaises(subprocess.CalledProcessError):
            module.run_bounded([sys.executable, "-I", "-B", "-c", "raise SystemExit(2)"], 10)

    def test_decoder_timeout(self):
        with self.assertRaises(TimeoutError):
            module.run_bounded([sys.executable, "-I", "-B", "-c", "import time; time.sleep(10)"], 10, timeout=0.2)


if __name__ == "__main__":
    unittest.main()
