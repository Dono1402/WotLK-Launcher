"""Disable server-side quest auto-accept without restarting any service.

Run as root on Atlas only. Runtime activation is a separate, explicit step.
The full configuration backup stays private on the server, never in Git.
"""

import hashlib
import json
import pathlib
import subprocess


TARGET = pathlib.Path(
    "/opt/arthas-next/candidates/atlas-all-update-20260926/"
    "server/etc-production/worldserver.conf"
)
BACKUP_DIR = pathlib.Path("/opt/arthas-next/backups/manual-quests-20260927")
BACKUP = BACKUP_DIR / "worldserver.conf.before"
EXPECTED_SHA256 = "770dcd97377a2f421d302e7240830bb8c39705f09fddcf57d9f2b12aa9e85016"
OLD = b"Quests.IgnoreAutoAccept = 0\n"
NEW = b"Quests.IgnoreAutoAccept = 1\n"
PATCH = """--- worldserver.conf
+++ worldserver.conf
@@ -2935 +2935 @@
-Quests.IgnoreAutoAccept = 0
+Quests.IgnoreAutoAccept = 1
"""


def main():
    if TARGET.is_symlink() or TARGET.resolve() != TARGET:
        raise SystemExit("Unexpected configuration path; refusing to modify it.")
    before = TARGET.read_bytes()
    if hashlib.sha256(before).hexdigest() != EXPECTED_SHA256:
        raise SystemExit("Configuration changed since audit; re-audit before applying.")
    if before.count(OLD) != 1 or NEW in before:
        raise SystemExit("Expected exactly one disabled IgnoreAutoAccept setting.")
    if BACKUP_DIR.exists():
        raise SystemExit("Backup directory already exists; refusing to overwrite it.")
    metadata = TARGET.stat()
    command = ["patch", "--fuzz=0", "--forward", "--no-backup-if-mismatch", str(TARGET)]
    subprocess.run(command + ["--dry-run"], input=PATCH, text=True, check=True)
    subprocess.run(["install", "-d", "-m", "0700", str(BACKUP_DIR)], check=True)
    subprocess.run(["cp", "-p", "--", str(TARGET), str(BACKUP)], check=True)
    if BACKUP.read_bytes() != before:
        raise SystemExit("Backup verification failed; configuration was not changed.")
    subprocess.run(command, input=PATCH, text=True, check=True)
    after = TARGET.read_bytes()
    if after != before.replace(OLD, NEW, 1):
        raise SystemExit("Unexpected diff; inspect configuration and private backup.")
    current_metadata = TARGET.stat()
    assert (metadata.st_mode, metadata.st_uid, metadata.st_gid) == (
        current_metadata.st_mode, current_metadata.st_uid, current_metadata.st_gid
    ), "Configuration permissions changed unexpectedly."
    print(json.dumps({
        "configuration": str(TARGET),
        "backup": str(BACKUP),
        "before_sha256": EXPECTED_SHA256,
        "after_sha256": hashlib.sha256(after).hexdigest(),
        "ignore_auto_accept": 1,
        "only_requested_setting_changed": True,
        "runtime_activation": "pending",
        "services_restarted": False,
    }, indent=2))


if __name__ == "__main__":
    main()
