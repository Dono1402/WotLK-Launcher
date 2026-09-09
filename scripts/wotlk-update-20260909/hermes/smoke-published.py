#!/usr/bin/env python3
"""Controlled synthetic published-package smoke; explicit reviewed invocation required.

Run only after root review, inside a dedicated systemd unit with:
  PrivateNetwork=yes PrivateIPC=yes ProtectSystem=strict ProtectHome=yes PrivateDevices=yes
  NoNewPrivileges=yes KillMode=control-group TimeoutStopSec=15 RuntimeMaxSec=120
  User=root Group=root UMask=0077 CapabilityBoundingSet=CAP_DAC_READ_SEARCH AmbientCapabilities=
  InaccessiblePaths=-/root -/home -/run/user -/dev/shm -/run/mysqld -/var/run/mysqld -/run/mariadb
    -/opt/hermesproxy-wotlk -/opt/arthas-next
  ReadWritePaths=<SMOKE_ROOT> /tmp
  BindPaths=<SMOKE_ROOT>/tmp:/tmp
  Nice=19 CPUQuota=100% MemoryMax=1G MemorySwapMax=0
  ExecStopPost=/usr/bin/python3 THIS_SCRIPT --record-unit-result

Before starting the unit, separately verify SMOKE_ROOT is absent and create only
SMOKE_ROOT and its tmp child, both root:root 0700. No other directory may be writable in the unit.
The unit runs /usr/bin/python3 THIS_SCRIPT --run-reviewed --host-netns HOST_NET --host-ipcns HOST_IPC.
Capture those two public namespace identifiers outside the unit, before execution. No package manager,
SDK, private certificate, database, real account, game, or live service is used.

Wire facts read from frozen f859d0c sources:
  Framework/Proto/RpcTypes.cs Header fields 1/2/3/5/6 varint, 11 fixed32.
  Framework/Proto/ConnectionService.cs ConnectRequest field 3 bool;
    ConnectResponse field 7 bool. Services/Connection.cs is Unauthorized.
  BnetTcpSession.cs prefix uint16 big-endian; malformed header closes,
    malformed payload drops one frame; empty ack and keepalive are harmless.
  SSLSocket.cs TLS1.2. Http.cs responses use platform line endings.
  Rest POST login/srp returns 404 before reading a body when bridge is disabled.
  WorldSocket.cs sends the banner before any client auth handshake.
"""

import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import signal
import socket
import ssl
import struct
import subprocess
import sys
import time
import shutil

CANDIDATE = Path("/opt/hermesproxy-candidates/hermes-update-20260909")
SMOKE_ROOT = CANDIDATE / "smoke-published-343-v1"
PACKAGE = CANDIDATE / "package-linux-x64"
MANIFEST = CANDIDATE / "validation/files.sha256"
ARCHIVE = CANDIDATE / "hermes-f859d0c-linux-x64-native.tar.gz"
ARCHIVE_SHA = "61785e2db59b0586874fe055c2e94549d2f9de5d3375b4d73fb295eab3e38d61"
MANIFEST_SHA = "3431950d9704608f6b9a33638ce56eb533cc634db20cb6804806b6b2d8c7d8fa"
COMMIT = "f859d0c59696b62483a98133b1e064c15dcb5604"
PORTS = {"rest": 29181, "bnet": 29119, "realm": 29184, "instance": 29186}
UNUSED_PORTS = {29398, 29399}  # Dummy legacy auth and disabled launcher-ticket port.
CONNECTION_SERVICE = 0x65446991
MARKER = "ATLAS_SYNTHETIC_SMOKE_MARKER_20260909"
BANNER = b"WORLD OF WARCRAFT CONNECTION - SERVER TO CLIENT - V2\n"

CONFIG = {
    "ClientOptions": {"ClientBuild": "V3_4_3_54261", "SeedHex": "0" * 32,
                      "ReportedOS": "Win", "ReportedPlatform": "x64", "RequireDeathKnightLevel": True},
    "LegacyServerOptions": {"Build": "V3_3_5a_12340", "Address": "127.0.0.1", "Port": 29398},
    "ProxyNetworkOptions": {"ExternalAddress": "127.0.0.1", "RestPort": PORTS["rest"],
                            "BNetPort": PORTS["bnet"], "RealmPort": PORTS["realm"],
                            "InstancePort": PORTS["instance"], "CertificatePfxPath": None,
                            "RestCertificatePfxPath": None, "CertificatePfxPassword": None,
                            "RestCertificatePfxPassword": None},
    "AzerothCoreBridgeOptions": {"Enabled": False, "ConnectionString": "",
                                 "InternalTicketPort": 29399, "InternalTicketSharedSecret": ""},
    "LoggingOptions": {"MinimumLevel": "Information", "ServerLevel": "Information",
                       "NetworkLevel": "Information", "StorageLevel": "Information",
                       "PacketLevel": "Warning", "ConsoleLevel": "Information",
                       "ToFile": False, "Directory": "Logs"},
    "DiagnosticsOptions": {"PacketsLog": False, "EnableMetrics": False,
                           "EnableVersionCheck": False, "ForwardTransportsV343": True},
    "ThrottlingOptions": {"PartyMemberStateMinIntervalMs": 200},
}


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha(path):
    with path.open("rb") as handle:
        return hashlib.file_digest(handle, "sha256").hexdigest()


def checked_manifest():
    require(sha(MANIFEST) == MANIFEST_SHA, "Unexpected canonical manifest")
    result = {}
    for line in MANIFEST.read_text().splitlines():
        digest, relative = line.split("  ", 1)
        name = PurePosixPath(relative)
        require(not name.is_absolute() and ".." not in name.parts, "Unsafe manifest path")
        result[name.as_posix()] = digest
    require(len(result) == 233, "Unexpected canonical file count")
    return result


def verify_files(root, entries, exact=False):
    for path in root.rglob("*"):
        require(not path.is_symlink() and (path.is_dir() or path.is_file()), "Non-regular package entry")
    if exact:
        require({p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file()} == set(entries),
                "Unexpected canonical package files")
    for relative, digest in entries.items():
        require(sha(root / relative) == digest, "Hash mismatch: " + relative)


def listeners():
    result = set()
    for name in ("tcp", "tcp6"):
        for line in Path("/proc/net/" + name).read_text().splitlines()[1:]:
            parts = line.split()
            if parts[3] == "0A":
                address, port = parts[1].rsplit(":", 1)
                result.add((address, int(port, 16)))
    return result


def require_isolation(host_netns, host_ipcns):
    require(sys.platform == "linux", "Linux only")
    require(os.geteuid() == 0, "Reviewed root-with-read-capability identity required")
    status = dict(line.split(":", 1) for line in Path("/proc/self/status").read_text().splitlines())
    require(all(int(status[name].strip(), 16) == 4 for name in ("CapPrm", "CapEff", "CapBnd")),
            "Capabilities must be exactly CAP_DAC_READ_SEARCH")
    require(all(int(status[name].strip(), 16) == 0 for name in ("CapInh", "CapAmb")),
            "Inherited and ambient capabilities must be empty")
    require(int(status["Umask"].strip(), 8) == 0o77, "UMask must be 0077")
    require(CANDIDATE.resolve(strict=True) == CANDIDATE, "Candidate path is not canonical")
    require(PACKAGE.resolve(strict=True) == PACKAGE, "Package path is not canonical")
    require(os.statvfs(PACKAGE).f_flag & os.ST_RDONLY, "Canonical package filesystem is not read-only")
    require(SMOKE_ROOT.resolve(strict=True) == SMOKE_ROOT, "Smoke path is not canonical")
    require(SMOKE_ROOT.stat().st_uid == 0 and SMOKE_ROOT.stat().st_gid == 0
            and SMOKE_ROOT.stat().st_mode & 0o777 == 0o700, "Smoke root must be root:root 0700")
    require({p.name for p in SMOKE_ROOT.iterdir()} == {"tmp"}, "Smoke root is not fresh")
    require((SMOKE_ROOT / "tmp").is_dir() and not (SMOKE_ROOT / "tmp").is_symlink(), "Unsafe private tmp")
    require(not any((SMOKE_ROOT / "tmp").iterdir()), "Private tmp is not fresh")
    require(os.stat("/tmp").st_ino == os.stat(SMOKE_ROOT / "tmp").st_ino
            and os.stat("/tmp").st_dev == os.stat(SMOKE_ROOT / "tmp").st_dev, "Missing candidate-only tmp bind")
    for kind, host_namespace in (("net", host_netns), ("ipc", host_ipcns)):
        require(host_namespace.startswith(kind + ":[") and host_namespace.endswith("]")
                and host_namespace[len(kind) + 2:-1].isascii()
                and host_namespace[len(kind) + 2:-1].isdigit(), "Invalid public host namespace ID")
        require(os.readlink("/proc/self/ns/" + kind) != host_namespace, "Host " + kind + " namespace")
    interfaces = {line.split(":", 1)[0].strip() for line in Path("/proc/net/dev").read_text().splitlines() if ":" in line}
    require(interfaces == {"lo"}, "Network is not loopback-only")
    require(not listeners(), "Existing listener in supposedly fresh network namespace")
    require(shutil.disk_usage(SMOKE_ROOT).free >= 8 * 1024**3, "Less than 8 GiB disk free")


def connect(port, tls=False):
    require(port in PORTS.values(), "Out-of-scope destination")
    raw = socket.create_connection(("127.0.0.1", port), timeout=3)
    if not tls:
        return raw
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    # The public embedded development certificate is deliberately not system-trusted.
    # This setting applies only to this in-memory loopback test connection.
    context.verify_mode = ssl.CERT_NONE
    context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_2
    try:
        return context.wrap_socket(raw, server_hostname="localhost")
    except BaseException:
        raw.close()
        raise


def recv_exact(stream, length):
    require(0 <= length <= 16384, "Unexpected response size")
    data = bytearray()
    while len(data) < length:
        block = stream.recv(length - len(data))
        require(block, "Unexpected EOF")
        data.extend(block)
    return bytes(data)


def http_request(stream, method="GET", path="/bnetserver/login/", body=b""):
    request = (f"{method} {path} HTTP/1.1\r\nHost: {MARKER}\r\nContent-Length: {len(body)}\r\n\r\n").encode() + body
    stream.sendall(request)
    data = bytearray()
    while b"\n\n" not in data.replace(b"\r\n", b"\n"):
        require(len(data) < 16384, "HTTP header too large")
        data.extend(recv_exact(stream, 1))
    lines = bytes(data).replace(b"\r\n", b"\n").decode("ascii").splitlines()
    code = int(lines[0].split()[1])
    headers = dict(line.split(":", 1) for line in lines[1:] if ":" in line)
    payload = recv_exact(stream, int(headers["Content-Length"].strip()))
    return code, json.loads(payload)


def expect_close(stream):
    try:
        require(stream.recv(1) == b"", "Faulted connection returned a trailing response")
    except (ConnectionResetError, ssl.SSLEOFError):
        pass  # A reset/EOF is closure; a timeout is deliberately NOT accepted.


def varint(value):
    result = bytearray()
    while value > 127:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)


def frame(token, payload=b"\x18\x01", method=1, service_id=0, service_hash=CONNECTION_SERVICE):
    header = b"\x08" + varint(service_id) + b"\x10" + varint(method) + b"\x18" + varint(token)
    header += b"\x28" + varint(len(payload))
    if service_hash:
        header += b"\x5d" + struct.pack("<I", service_hash)
    return struct.pack(">H", len(header)) + header + payload


def protobuf_fields(data):
    offset = 0
    fields = {}

    def read_varint():
        nonlocal offset
        value = 0
        for shift in range(0, 70, 7):
            require(offset < len(data), "Truncated protobuf varint")
            byte = data[offset]
            offset += 1
            value |= (byte & 127) << shift
            if byte < 128:
                return value
        raise RuntimeError("Invalid protobuf varint")

    while offset < len(data):
        tag = read_varint()
        number, wire = tag >> 3, tag & 7
        require(number > 0, "Invalid protobuf field")
        if wire == 0:
            value = read_varint()
        else:
            length = {1: 8, 5: 4}.get(wire)
            if wire == 2:
                length = read_varint()
            require(length is not None and offset + length <= len(data), "Unsupported/truncated protobuf field")
            value = data[offset:offset + length]
            offset += length
            if wire in (1, 5):
                value = int.from_bytes(value, "little")
        fields[number] = value
    return fields


def expect_connect_response(stream, token):
    header_length = struct.unpack(">H", recv_exact(stream, 2))[0]
    require(0 < header_length < 2048, "Invalid BNet header length")
    header = protobuf_fields(recv_exact(stream, header_length))
    require(header.get(1) == 0xFE and header.get(2) == 1 and header.get(3) == token,
            "Wrong BNet response identity")
    require(header.get(6, 0) == 0 and header.get(11) == CONNECTION_SERVICE, "BNet non-success response")
    payload = protobuf_fields(recv_exact(stream, header.get(5, 0)))
    require(payload.get(7) == 1, "Connect handler did not return UseBindlessRpc=true")


def probe():
    with connect(PORTS["rest"], tls=True) as stream:
        code, form = http_request(stream)
        require(code == 200 and form.get("type") == "LOGIN_FORM", "Missing login form")
        require({item["input_id"] for item in form["inputs"]} == {"account_name", "password", "log_in_submit"},
                "Unexpected login form fields")
        require(not form.get("srp_url"), "Bridge unexpectedly enabled")
    with connect(PORTS["rest"], tls=True) as stream:
        code, result = http_request(stream, "POST", "/bnetserver/login/srp/", b'{"inputs":[]}')
        require(code == 404 and result == {}, "Disabled bridge endpoint did not reject the request")
    with connect(PORTS["rest"], tls=True) as stream:
        stream.sendall(b"GET /bnetserver/login/ HTTP/1.1\r\nAccept: a\r\nAccept: b\r\n\r\n")
        expect_close(stream)
    with connect(PORTS["rest"], tls=True) as stream:
        require(http_request(stream)[0] == 200, "REST listener did not recover after malformed request")

    with connect(PORTS["bnet"], tls=True) as stream:
        stream.sendall(frame(201))
        expect_connect_response(stream, 201)
        stream.sendall(frame(200, b"", service_id=0xFE, service_hash=0) + frame(202, b"", method=5) + frame(203))
        expect_connect_response(stream, 203)
        stream.sendall(frame(204, b"\x0f\x0f") + frame(205))
        expect_connect_response(stream, 205)
        stream.sendall(frame(206))  # A later TLS read must also remain alive.
        expect_connect_response(stream, 206)
    with connect(PORTS["bnet"], tls=True) as stream:
        stream.sendall(b"\x00\x03\x0f\x0f\x0f" + frame(207))
        expect_close(stream)
    with connect(PORTS["bnet"], tls=True) as stream:
        stream.sendall(frame(208))
        expect_connect_response(stream, 208)

    for role in ("realm", "instance"):
        with connect(PORTS[role]) as stream:
            require(recv_exact(stream, len(BANNER)) == BANNER, role + " banner mismatch")
            # Deliberately do not send CLIENT TO SERVER or an auth-session packet.


def record_unit_result():
    """Tiny independent receipt; a signal must not count as normal completion."""
    require(SMOKE_ROOT.resolve(strict=True) == SMOKE_ROOT, "Unsafe receipt destination")
    receipt = {name: os.environ.get(name) for name in ("SERVICE_RESULT", "EXIT_CODE", "EXIT_STATUS")}
    # Exclusive create: never reuse or overwrite an earlier invocation's receipt.
    with (SMOKE_ROOT / "unit-result.json").open("x", encoding="utf-8") as output:
        json.dump(receipt, output, indent=2)
        output.write("\n")
    require(receipt == {"SERVICE_RESULT": "success", "EXIT_CODE": "exited", "EXIT_STATUS": "0"},
            "Main execution was not a normal zero exit; smoke is not validated")


def main():
    args = sys.argv[1:]
    require(len(args) == 5 and args[0] == "--run-reviewed" and args[1] == "--host-netns"
            and args[3] == "--host-ipcns", "Review and explicit host namespace IDs required")
    require_isolation(args[2], args[4])
    require(sha(ARCHIVE) == ARCHIVE_SHA, "Unexpected canonical archive")
    entries = checked_manifest()
    verify_files(PACKAGE, entries, exact=True)
    run = SMOKE_ROOT / "run"
    shutil.copytree(PACKAGE, run, copy_function=shutil.copy2)
    verify_files(run, entries, exact=True)
    for relative in entries:
        require(os.stat(PACKAGE / relative).st_ino != os.stat(run / relative).st_ino, "Hardlinked test copy")
    config_path = run / "test-config.json"
    config_path.write_text(json.dumps(CONFIG, indent=2) + "\n", encoding="utf-8")
    (run / "AccountData").mkdir()
    env = {"PATH": "/usr/bin:/bin", "LC_ALL": "C.UTF-8", "TMPDIR": str(SMOKE_ROOT / "tmp"),
           "DOTNET_DbgEnableMiniDump": "0", "DOTNET_EnableDiagnostics": "0",
           "DOTNET_PROCESSOR_COUNT": "1", "DOTNET_ENVIRONMENT": "Production"}
    result = {"sourceCommit": COMMIT, "success": False, "productionAuthentication": False,
              "ports": PORTS, "runtimeOnly": True, "forcedStop": False}
    process = None
    failure = None
    try:
        with (SMOKE_ROOT / "application.log").open("wb") as output:
            process = subprocess.Popen([str(run / "HermesProxy"), "--config", str(config_path)],
                                       cwd=run, env=env, stdin=subprocess.DEVNULL,
                                       stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
            result["testPid"] = process.pid
            deadline = time.monotonic() + 30
            expected = {("0100007F", port) for port in PORTS.values()}
            while listeners() != expected:
                require(process.poll() is None, "Published application exited during startup")
                require(time.monotonic() < deadline, "Four exact loopback listeners not ready in 30 seconds")
                time.sleep(0.1)
            result["listenersObserved"] = sorted(listeners())
            require(not ({port for _, port in listeners()} & UNUSED_PORTS), "Unexpected auth/ticket listener")
            probe()
            require(process.poll() is None, "Application died after probes")
            result["probesPassed"] = True
    except BaseException as error:
        failure = error
        result["failure"] = str(error)
    finally:
        if process is not None and process.poll() is None:
            require(Path(f"/proc/{process.pid}/exe").resolve() == run / "HermesProxy", "Refusing to signal unexpected process")
            process.send_signal(signal.SIGTERM)
            try:
                process.wait(timeout=12)
            except subprocess.TimeoutExpired:
                result["forcedStop"] = True
                process.kill()  # Only this Popen child; never a service or name-wide kill.
                process.wait(timeout=3)
        result["exitCode"] = None if process is None else process.returncode
        result["listenersAfter"] = sorted(listeners())
        verify_files(run, entries)
        verify_files(PACKAGE, entries, exact=True)
        require(sha(ARCHIVE) == ARCHIVE_SHA, "Canonical archive changed")
        result["canonicalAndClonePackageHashesUnchanged"] = True
        result["accountDataEmpty"] = not any((run / "AccountData").iterdir())
        log = (SMOKE_ROOT / "application.log").read_text(errors="replace")
        result["syntheticMarkerAbsentFromLogs"] = MARKER not in log and MARKER.encode().hex().upper() not in log.upper()
        result["expectedFaultLogs"] = all(text in log for text in
            ("Request handling failed", "Dropping frame service", "Malformed frame header"))
        result["fatalMarkerAbsent"] = not any(text in log for text in
            ("Fatal startup error", "UnhandledException", "Hosting failed to start", "Failed to start "))
        result["success"] = bool(failure is None and result.get("probesPassed")
            and result["exitCode"] == 0 and not result["forcedStop"] and not result["listenersAfter"]
            and result["accountDataEmpty"] and result["syntheticMarkerAbsentFromLogs"]
            and result["expectedFaultLogs"] and result["fatalMarkerAbsent"])
        (SMOKE_ROOT / "smoke-result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(result, indent=2), flush=True)
    require(result["success"], "Smoke failed; inspect retained result/log, no automatic retry or product change")


if __name__ == "__main__":
    if sys.argv[1:] == ["--record-unit-result"]:
        record_unit_result()
    else:
        main()
