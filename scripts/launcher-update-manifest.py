#!/usr/bin/env python3
"""Create and verify signed Atlas Launcher update manifests.

Signing uses OpenSSL ECDSA P-256 with SHA-256. The private key is supplied by
the release environment and is never written to the repository or public root.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit


DOMAIN = "atlas-launcher-update-manifest-v1"
SCHEMA_VERSION = 1
ALLOWED_HOST = "animeclub.fr"
PACKAGE_ROOT = "/wotlk/launcher/releases/"
PACKAGE_NAME = "WotLK-Launcher.exe"
MAX_PACKAGE_BYTES = 1024 * 1024 * 1024
KEY_ID = re.compile(r"^[a-z0-9][a-z0-9._-]{2,63}$", re.ASCII)
VERSION = re.compile(r"^[0-9]{1,5}(\.[0-9]{1,5}){1,3}$", re.ASCII)
SHA256 = re.compile(r"^[0-9a-f]{64}$", re.ASCII)
PUBLISHED_AT = re.compile(r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", re.ASCII)
FIELDS = {
    "schemaVersion",
    "keyId",
    "version",
    "url",
    "size",
    "sha256",
    "publishedAt",
    "signature",
}


def fail(message: str) -> None:
    raise SystemExit(message)


def read_json_strict(path: Path) -> dict:
    def no_duplicates(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                fail(f"duplicate manifest field: {key}")
            result[key] = value
        return result

    try:
        value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=no_duplicates)
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        fail(f"invalid manifest: {type(exc).__name__}")
    if not isinstance(value, dict) or set(value) != FIELDS:
        fail("manifest fields do not match schema 1")
    return value


def require_ascii(value: object, name: str) -> str:
    if not isinstance(value, str) or not value or any(ord(character) > 0x7F for character in value):
        fail(f"invalid ASCII field: {name}")
    if "\r" in value or "\n" in value:
        fail(f"line break forbidden in field: {name}")
    return value


def validate_uri(url: str, version: str) -> None:
    lowered = url.lower()
    if "\\" in url or ".." in url or any(token in lowered for token in ("%2e", "%2f", "%5c")):
        fail("ambiguous package URL")
    parsed = urlsplit(url)
    try:
        port = parsed.port
    except ValueError:
        fail("invalid package port")
    expected_path = f"{PACKAGE_ROOT}{version}/{PACKAGE_NAME}"
    expected_url = f"https://{ALLOWED_HOST}{expected_path}"
    if (
        url != expected_url
        or parsed.scheme != "https"
        or parsed.hostname != ALLOWED_HOST
        or parsed.username is not None
        or parsed.password is not None
        or port not in (None, 443)
        or parsed.path != expected_path
        or parsed.query
        or parsed.fragment
    ):
        fail("package URL rejected by Atlas allowlist")


def validate_fields(manifest: dict, require_signature: bool = True) -> None:
    if manifest.get("schemaVersion") != SCHEMA_VERSION:
        fail("unsupported manifest schema")
    key_id = require_ascii(manifest.get("keyId"), "keyId")
    version = require_ascii(manifest.get("version"), "version")
    sha256 = require_ascii(manifest.get("sha256"), "sha256")
    url = require_ascii(manifest.get("url"), "url")
    published_at = require_ascii(manifest.get("publishedAt"), "publishedAt")
    if not KEY_ID.fullmatch(key_id):
        fail("invalid keyId")
    if not VERSION.fullmatch(version):
        fail("invalid version")
    if not SHA256.fullmatch(sha256):
        fail("invalid SHA-256")
    size = manifest.get("size")
    if isinstance(size, bool) or not isinstance(size, int) or not 0 < size <= MAX_PACKAGE_BYTES:
        fail("invalid package size")
    if not PUBLISHED_AT.fullmatch(published_at):
        fail("invalid publishedAt")
    try:
        datetime.strptime(published_at, "%Y-%m-%dT%H:%M:%SZ")
    except ValueError:
        fail("invalid publishedAt")
    validate_uri(url, version)
    if require_signature:
        require_ascii(manifest.get("signature"), "signature")


def canonical_payload(manifest: dict) -> bytes:
    for name in ("keyId", "version", "sha256", "url", "publishedAt"):
        require_ascii(manifest.get(name), name)
    size = manifest.get("size")
    schema = manifest.get("schemaVersion")
    if isinstance(size, bool) or not isinstance(size, int):
        fail("invalid canonical size")
    if isinstance(schema, bool) or not isinstance(schema, int):
        fail("invalid canonical schema")
    value = "\n".join(
        (
            DOMAIN,
            f"schemaVersion={schema}",
            f"keyId={manifest['keyId']}",
            f"version={manifest['version']}",
            f"size={size}",
            f"sha256={manifest['sha256']}",
            f"url={manifest['url']}",
            f"publishedAt={manifest['publishedAt']}",
            "",
        )
    )
    return value.encode("utf-8", errors="strict")


def run_openssl(arguments: list[str]) -> None:
    try:
        subprocess.run(["openssl", *arguments], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    except FileNotFoundError:
        fail("openssl is required")
    except subprocess.CalledProcessError:
        fail("OpenSSL signature operation failed")


def require_p256_public_key(public_key: Path) -> None:
    try:
        result = subprocess.run(
            ["openssl", "pkey", "-pubin", "-in", str(public_key), "-text", "-noout"],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
    except FileNotFoundError:
        fail("openssl is required")
    except subprocess.CalledProcessError:
        fail("invalid public signing key")
    description = result.stdout + result.stderr
    if "prime256v1" not in description and "P-256" not in description:
        fail("release signing key must use ECDSA P-256")


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
    except OSError as exc:
        fail(f"package unavailable: {type(exc).__name__}")
    return digest.hexdigest()


def verify_signature(manifest: dict, public_key: Path) -> None:
    require_p256_public_key(public_key)
    try:
        signature = base64.b64decode(manifest["signature"], validate=True)
    except (ValueError, TypeError):
        fail("invalid signature encoding")
    with tempfile.TemporaryDirectory(prefix="atlas-launcher-signature-") as temp:
        payload_path = Path(temp) / "payload.txt"
        signature_path = Path(temp) / "signature.der"
        payload_path.write_bytes(canonical_payload(manifest))
        signature_path.write_bytes(signature)
        run_openssl(
            [
                "dgst",
                "-sha256",
                "-verify",
                str(public_key),
                "-signature",
                str(signature_path),
                str(payload_path),
            ]
        )


def verify_package(manifest: dict, package: Path) -> None:
    try:
        size = package.stat().st_size
    except OSError as exc:
        fail(f"package unavailable: {type(exc).__name__}")
    digest = file_sha256(package)
    if size != manifest["size"] or digest != manifest["sha256"]:
        fail("package does not match signed manifest")


def create_manifest(args) -> None:
    package = args.package.resolve(strict=True)
    private_key = args.private_key.resolve(strict=True)
    public_key = args.public_key.resolve(strict=True)
    output = args.output.resolve()
    version = args.version
    published_at = args.published_at or datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    package_size = package.stat().st_size
    manifest = {
        "schemaVersion": SCHEMA_VERSION,
        "keyId": args.key_id,
        "version": version,
        "url": f"https://{ALLOWED_HOST}{PACKAGE_ROOT}{version}/{PACKAGE_NAME}",
        "size": package_size,
        "sha256": file_sha256(package),
        "publishedAt": published_at,
        "signature": "pending",
    }
    validate_fields(manifest, require_signature=False)

    with tempfile.TemporaryDirectory(prefix="atlas-launcher-signature-") as temp:
        payload_path = Path(temp) / "payload.txt"
        signature_path = Path(temp) / "signature.der"
        payload_path.write_bytes(canonical_payload(manifest))
        run_openssl(
            [
                "dgst",
                "-sha256",
                "-sign",
                str(private_key),
                "-out",
                str(signature_path),
                str(payload_path),
            ]
        )
        manifest["signature"] = base64.b64encode(signature_path.read_bytes()).decode("ascii")

    validate_fields(manifest)
    verify_signature(manifest, public_key)
    verify_package(manifest, package)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(f".{output.name}.next")
    temporary.write_text(json.dumps(manifest, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    temporary.replace(output)
    print(f"signed manifest ready: version={version} keyId={args.key_id}")


def verify_manifest(args) -> None:
    manifest = read_json_strict(args.manifest)
    validate_fields(manifest)
    if args.expected_key_id and manifest["keyId"] != args.expected_key_id:
        fail("manifest keyId does not match release configuration")
    verify_signature(manifest, args.public_key.resolve(strict=True))
    verify_package(manifest, args.package.resolve(strict=True))
    print(f"signed manifest verified: version={manifest['version']} keyId={manifest['keyId']}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subcommands = parser.add_subparsers(dest="command", required=True)

    create = subcommands.add_parser("create")
    create.add_argument("--package", type=Path, required=True)
    create.add_argument("--version", required=True)
    create.add_argument("--key-id", required=True)
    create.add_argument("--private-key", type=Path, required=True)
    create.add_argument("--public-key", type=Path, required=True)
    create.add_argument("--published-at")
    create.add_argument("--output", type=Path, required=True)
    create.set_defaults(handler=create_manifest)

    verify = subcommands.add_parser("verify")
    verify.add_argument("--manifest", type=Path, required=True)
    verify.add_argument("--package", type=Path, required=True)
    verify.add_argument("--public-key", type=Path, required=True)
    verify.add_argument("--expected-key-id")
    verify.set_defaults(handler=verify_manifest)
    return parser


def main() -> None:
    args = build_parser().parse_args()
    args.handler(args)


if __name__ == "__main__":
    main()
