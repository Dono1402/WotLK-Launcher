# Hermes published-package smoke — reviewed plan, not launched

Scope: frozen Hermes `f859d0c59696b62483a98133b1e064c15dcb5604`, synthetic configuration, no real authentication, database, private certificate, production service action or game/client launch. Wait until the six-job compilation phase has finished, then recheck available memory before a separate go-ahead.

## Preconditions

- `/usr/bin/python3` must be Python 3.11+ with `ssl` available. Read-only preflight confirmed Python 3.13.5 and OpenSSL 3.5.7; Hermes was not started.
- Verify the script hash after any transfer and before the run. Re-read its tiny receipt function, strict capability/umask check, and namespace arguments.
- Verify `/opt/hermesproxy-candidates/hermes-update-20260909/smoke-published-343-v1` is absent before creating that exact directory and its empty `tmp` child, both **root:root 0700**. No directory reuse or deletion, no permission or ownership change to the existing candidate/package.
- Capture the production Hermes/world service PIDs, start timestamps and restart counts read-only before and after. Do not restart them.
- No launch while the heavy compilation runs. `MemoryMax=1G`, no swap and one CPU cap apply to this separate smoke only.
- Live permission checks found the candidate parent `debian:debian 0700` and canonical package `root:root 0700`: user `debian` cannot read the package. Root with all capabilities removed cannot traverse the candidate parent. The approved targeted identity is therefore **root with only CAP_DAC_READ_SEARCH**, no ambient or inheritable capabilities. A read-only `setpriv` preflight confirmed `CapPrm=CapEff=CapBnd=0x4`, `CapInh=CapAmb=0`, successful read-only opens of apphost/manifest/archive, and no write bypass on the candidate parent. No file content or production configuration was read in that preflight.
- Root with restricted capabilities cannot read PID 1's namespace links. Capture the two public host IDs before the unit and pass them explicitly; never grant CAP_SYS_PTRACE just for this check. Revalidate them outside the unit while running. The already observed IDs are not hardcoded for later execution.

## Exact launch command, for execution only after the separate go-ahead

```bash
smoke_host_netns=$(sudo readlink /proc/1/ns/net)
smoke_host_ipcns=$(sudo readlink /proc/1/ns/ipc)
sudo systemd-run --unit=atlas-hermes-smoke-20260909-v1 --wait \
  --property=Type=exec \
  --property=Restart=no \
  --property=User=root \
  --property=Group=root \
  --property=UMask=0077 \
  --property=CapabilityBoundingSet=CAP_DAC_READ_SEARCH \
  --property=AmbientCapabilities= \
  --property=Nice=19 \
  --property=CPUQuota=100% \
  --property=MemoryMax=1G \
  --property=MemorySwapMax=0 \
  --property=RuntimeMaxSec=120 \
  --property=TimeoutStopSec=15 \
  --property=KillMode=control-group \
  --property=SendSIGKILL=yes \
  --property=PrivateNetwork=yes \
  --property=PrivateIPC=yes \
  --property=ProtectSystem=strict \
  --property=ProtectHome=yes \
  --property=PrivateDevices=yes \
  --property=NoNewPrivileges=yes \
  --property='InaccessiblePaths=-/root -/home -/run/user -/dev/shm -/run/mysqld -/var/run/mysqld -/run/mariadb -/opt/hermesproxy-wotlk -/opt/arthas-next' \
  --property=BindPaths=/opt/hermesproxy-candidates/hermes-update-20260909/smoke-published-343-v1/tmp:/tmp \
  --property='ReadWritePaths=/opt/hermesproxy-candidates/hermes-update-20260909/smoke-published-343-v1 /tmp' \
  --property='ExecStopPost=/usr/bin/python3 /opt/hermesproxy-candidates/hermes-update-20260909/smoke-published.py --record-unit-result' \
  /usr/bin/python3 /opt/hermesproxy-candidates/hermes-update-20260909/smoke-published.py \
  --run-reviewed --host-netns "$smoke_host_netns" --host-ipcns "$smoke_host_ipcns"
```

The `--wait` client may be run through a yielding terminal session; do not use a blocking model wait over 60 seconds. No `--collect`: retain any failed unit for diagnostics. No automatic retry.

## Read-only sandbox verification while the unit runs

Capture `systemctl show atlas-hermes-smoke-20260909-v1.service` fields `MainPID`, `ControlGroup`, `User`, `Group`, `UMask`, `CapabilityBoundingSet`, `AmbientCapabilities`, `PrivateNetwork`, `PrivateIPC`, `ProtectSystem`, `ProtectHome`, `PrivateDevices`, `InaccessiblePaths`, `BindPaths`, `ReadWritePaths`, `KillMode`, `SendSIGKILL`, `TimeoutStopUSec`, `RuntimeMaxUSec`, `MemoryMax`, `MemorySwapMax` and `CPUQuotaPerSecUSec`.

Use that specific main PID only to compare `/proc/<pid>/ns/net` and `/proc/<pid>/ns/ipc` with PID 1, inspect its mount table, and verify its network contains loopback only. The script independently rejects a host network/IPC namespace using the freshly supplied IDs, checks exact capability masks/umask, and checks `/tmp` is precisely the private candidate `tmp` bind. The canonical `package-linux-x64` must return `ST_RDONLY`; only its copied test clone is writable. `ProtectHome=yes` hides `/home`, `/root`, `/run/user`; explicit inaccessible mounts additionally cover those locations, `/dev/shm`, all production Hermes files/account data under `/opt/hermesproxy-wotlk`, and all Arthas configurations under `/opt/arthas-next`. None of them is a write exception. The child receives a minimal explicit environment with no inherited HOME or production credentials. No extra mount layout is introduced beyond the already reviewed tmp bind and standard sandbox protections.

## Fail-closed completion criteria

All conditions are mandatory:

1. The `systemd-run --wait` command exits zero. Capture the unit's result/status if still loaded: `Result=success`, `ExecMainCode=1` (the numeric `CLD_EXITED`, displayed as `code=exited` by status/journal), `ExecMainStatus=0`, `MainPID=0`, inactive/dead. A garbage-collected successful unit is not itself proof; use the retained exact receipt and journal.
2. `unit-result.json` parses completely and is exactly `{"SERVICE_RESULT":"success","EXIT_CODE":"exited","EXIT_STATUS":"0"}`. It is written with exclusive create by the tiny `ExecStopPost` function. Missing, truncated, unexpected or signal-based status is failure/incomplete, never a pass. If the receipt function itself fails, the unit fails too.
3. `smoke-result.json` parses completely and has `success=true`, the frozen source commit, all intended probes passed, child exit zero, `forcedStop=false`, no remaining listeners, unchanged canonical/copy hashes, empty synthetic `AccountData`, marker absent, all expected injected-fault diagnostics and no fatal-startup marker. Do not equate the receipt alone with a complete smoke result.
4. The captured unit cgroup is empty or has been removed, including all descendant cgroups; the recorded test PID is gone. The isolated network namespace has no remaining process to retain it. If a namespace-holder process remains, inspect its listeners and mark the run failed; no port-forwarding or host-network fallback. Confirm none of the four test ports was exposed on the host.
5. Production service PIDs/start times/restart counts match the before snapshot. No application restart/deployment or real login was performed.

`KillMode=control-group` covers the Python runner and its Hermes descendant despite `start_new_session=True`. On timeout/external stop, SIGTERM targets the group, then `SendSIGKILL=yes` permits final group termination after `TimeoutStopSec=15`. An unhandled SIGTERM can prevent Python's `finally` from running: the independent receipt records `killed/TERM`, exits nonzero, and the absent/incomplete smoke report remains unvalidated. It must never be summarized as a completed smoke merely because all application probes had passed earlier.

Primary references: [systemd ExecStopPost semantics](https://github.com/systemd/systemd/blob/main/man/systemd.service.xml), [Debian systemd exit-result documentation](https://manpages.debian.org/trixie/systemd/systemd.exec.5.en.html). These describe the status variables; the deployed unit's actual properties and result must still be checked.
