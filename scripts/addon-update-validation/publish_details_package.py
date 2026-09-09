"""Explicit, one-package Atlas publication. Run on Atlas only after approval.

Preserves every non-Details entry, refuses stale catalog state, backs up the
catalog outside the web root, publishes atomically and never restarts services.
"""

import argparse
import copy
import datetime
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import uuid
import zipfile

ROOT = Path("/var/www/wotlk-launcher/launcher/addons")
EXPECTED_CATALOG_SHA = "cc4d48f80ac5dae76ec58fbb80f0ffe254196361d30ba26d42cf3f494c032b79"
PACKAGE_SHA = "ca2ffad679c0647a0f4345f57675ef9e1198514d22022e5cfb380a34186c688b"
FILENAME = "Details-Details.20240115.12220.155-atlas-30403.zip"
VERSION = "20240115.12220.155"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def checked_root(path):
    if not path.is_dir() or path.resolve() != path:
        raise ValueError("Expected an existing canonical directory")
    for parent in (path, *path.parents):
        if parent.is_symlink():
            raise ValueError("Linked publication path refused")


def update_catalog(original, metadata):
    if original.get("schemaVersion") != 1 or original.get("clientInterface") != "30403":
        raise ValueError("Unexpected catalog schema")
    entries = original["addons"]
    matches = [i for i, entry in enumerate(entries) if entry.get("id") == "details"]
    if len(entries) != 14 or len(matches) != 1:
        raise ValueError("Unexpected catalog membership")
    index = matches[0]
    if entries[index]["version"] != "20250228.13407.162":
        raise ValueError("Details version changed since approval")
    if metadata["id"] != "details" or metadata["version"] != VERSION:
        raise ValueError("Unexpected replacement")
    if (metadata["url"] != "https://animeclub.fr/wotlk/addons/packages/" + FILENAME
            or metadata["size"] != 4959885 or metadata["sha256"] != PACKAGE_SHA
            or metadata["installHash"] != PACKAGE_SHA or metadata["interface"] != "30403"
            or metadata["folders"] != entries[index]["folders"]
            or metadata["components"] or metadata["tokenReplacements"] or metadata["stripPrefix"]):
        raise ValueError("Unexpected package metadata")
    allowed = {"id", "name", "version", "interface", "url", "size", "sha256", "installHash",
               "stripPrefix", "components", "tokenReplacements", "folders", "sourceUrl", "knownLimitations"}
    if set(metadata) != allowed or "atlasValidation" in entries[index]:
        raise ValueError("Metadata requires a fresh review")
    updated = copy.deepcopy(original)
    updated["addons"][index].update(metadata)
    if any(before != after for i, (before, after) in enumerate(zip(entries, updated["addons"])) if i != index):
        raise ValueError("Unrelated addon changed")
    if {k: v for k, v in original.items() if k != "addons"} != {k: v for k, v in updated.items() if k != "addons"}:
        raise ValueError("Catalog metadata changed")
    return updated


def write_new(path, data, mode):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def state():
    output = subprocess.run(["systemctl", "show", "wotlk-launcher-api.service", "-p",
                             "ActiveState,SubState,MainPID,NRestarts"], check=True, capture_output=True, text=True).stdout
    return dict(line.split("=", 1) for line in output.splitlines() if "=" in line)


def publish(stage):
    import fcntl  # Linux publication only; pure catalog tests also run on Windows.
    if os.geteuid() != 0:
        raise PermissionError("Publication requires the authorized Atlas administrator")
    checked_root(ROOT)
    checked_root(ROOT / "packages")
    checked_root(stage)
    if not str(stage).startswith("/var/tmp/atlas-details-"):
        raise ValueError("Use the fresh private Atlas Details staging directory")
    archive = stage / FILENAME
    package = archive.read_bytes()
    metadata = json.loads((stage / "details-package.json").read_text())
    if len(package) != 4959885 or digest(package) != PACKAGE_SHA:
        raise ValueError("Uploaded package differs from the approved preparation")
    with zipfile.ZipFile(archive) as test:
        if test.testzip() is not None or len(test.infolist()) != 490:
            raise ValueError("Package integrity failure")
    catalog_path = ROOT / "catalog.json"
    if catalog_path.is_symlink():
        raise ValueError("Linked catalog refused")
    lock_descriptor = os.open("/var/lock/atlas-addons-catalog.lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    with os.fdopen(lock_descriptor, "r+") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        old_bytes = catalog_path.read_bytes()
        if digest(old_bytes) != EXPECTED_CATALOG_SHA:
            raise ValueError("Catalog changed: stop and review before publishing")
        new_catalog = update_catalog(json.loads(old_bytes), metadata)
        new_bytes = (json.dumps(new_catalog, indent=2, ensure_ascii=False) + "\n").encode()
        old_stat = catalog_path.stat()
        before_state = state()
        if before_state["ActiveState"] != "active" or before_state["SubState"] != "running":
            raise ValueError("Launcher API is not healthy")
        destination = ROOT / "packages" / FILENAME
        if destination.exists() or destination.is_symlink():
            raise FileExistsError("Package destination already exists; never overwrite it blindly")
        backup_root = Path("/var/backups/atlas-launcher-addons")
        backup_root.mkdir(mode=0o700, exist_ok=True)
        checked_root(backup_root)
        if stat.S_IMODE(backup_root.stat().st_mode) & 0o077:
            raise PermissionError("Backup root must be private")
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        backup = backup_root / ("details-" + stamp + "-" + uuid.uuid4().hex[:8])
        backup.mkdir(mode=0o700)
        write_new(backup / "catalog.before.json", old_bytes, 0o600)
        if digest((backup / "catalog.before.json").read_bytes()) != EXPECTED_CATALOG_SHA:
            raise ValueError("Backup verification failed")
        # New immutable name first; then switch the catalog after all checks.
        temporary_package = destination.with_name(".details-" + uuid.uuid4().hex + ".upload")
        write_new(temporary_package, package, 0o644)
        os.chown(temporary_package, old_stat.st_uid, old_stat.st_gid)
        if digest(temporary_package.read_bytes()) != PACKAGE_SHA:
            raise ValueError("Package write verification failed")
        # Hard-link publication is atomic and refuses an existing target.
        os.link(temporary_package, destination, follow_symlinks=False)
        temporary_package.unlink()  # Only our verified temporary name; final file remains.
        package_directory = os.open(destination.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(package_directory)
        finally:
            os.close(package_directory)
        subprocess.run(["sudo", "-u", "wotlklauncher", "test", "-r", str(destination)], check=True)
        pending_catalog = ROOT / (".catalog-details-" + uuid.uuid4().hex + ".tmp")
        write_new(pending_catalog, new_bytes, stat.S_IMODE(old_stat.st_mode))
        os.chown(pending_catalog, old_stat.st_uid, old_stat.st_gid)
        subprocess.run(["sudo", "-u", "wotlklauncher", "test", "-r", str(pending_catalog)], check=True)
        if digest(catalog_path.read_bytes()) != EXPECTED_CATALOG_SHA:
            raise ValueError("Concurrent catalog change; candidate left unreferenced, no catalog replaced")
        os.replace(pending_catalog, catalog_path)
        directory_descriptor = os.open(ROOT, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory_descriptor)
        finally:
            os.close(directory_descriptor)
        if catalog_path.read_bytes() != new_bytes or digest(destination.read_bytes()) != PACKAGE_SHA:
            raise ValueError("Post-publication verification failed; inspect private backup before rollback")
        after_state = state()
        report = {"status": "published", "version": VERSION, "publishedAtUtc": stamp,
                  "catalogBeforeSha256": EXPECTED_CATALOG_SHA, "catalogAfterSha256": digest(new_bytes),
                  "packageSha256": PACKAGE_SHA, "packageBytes": len(package), "packageUrl": metadata["url"],
                  "addonCount": len(new_catalog["addons"]), "otherAddonsUnchanged": 13,
                  "backupDirectory": str(backup), "apiBefore": before_state, "apiAfter": after_state,
                  "apiStateUnchanged": before_state == after_state, "servicesRestarted": False,
                  "clientWritten": False, "savedVariablesTouched": False}
        write_new(backup / "publication.json", (json.dumps(report, indent=2) + "\n").encode(), 0o600)
        print(json.dumps(report, indent=2))
        if before_state != after_state:
            raise RuntimeError("Publication succeeded but API state changed; investigate without restarting")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", required=True, type=Path)
    args = parser.parse_args()
    publish(args.stage)
