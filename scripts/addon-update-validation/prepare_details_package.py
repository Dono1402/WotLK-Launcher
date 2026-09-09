"""Prepare the user-selected Details version; never installs or publishes it.

Only Wrath TOC Interface declarations change. All other payload bytes, including
every Lua file, must remain identical to the SHA-pinned user archive.
"""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import stat
import zipfile
import xml.etree.ElementTree as ET

VERSION = "20240115.12220.155"
SOURCE_SHA = "5a4419ff922916f1e7cfb6c2360a0675b0319a5daa810a980542113c79e74656"
FILENAME = f"Details-Details.{VERSION}-atlas-30403.zip"
ROOTS = ["Details", "Details_Compare2", "Details_DataStorage", "Details_EncounterDetails",
         "Details_RaidCheck", "Details_Streamer", "Details_TinyThreat", "Details_Vanguard"]
INTERFACE = re.compile(rb"(?m)^(## Interface:[ \t]*)(30401|30402)(?=\r?$)")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def safe_path(name):
    name = name.replace("\\", "/")
    path = PurePosixPath(name)
    if (path.is_absolute() or not path.parts or any(part in (".", "..") for part in name.split("/"))
            or any(c in name for c in ':<>"|?*') or any(ord(c) < 32 for c in name)
            or any(part.rstrip(" .") != part for part in path.parts)
            or path.parts[0] not in ROOTS):
        raise ValueError("Unsafe or unexpected archive path")
    return path.as_posix()


def read_archive(path, expected_sha=SOURCE_SHA):
    raw = path.read_bytes()
    if sha(raw) != expected_sha:
        raise ValueError("Not the user-selected, SHA-pinned archive")
    files = {}
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        if len(entries) > 2000 or sum(e.file_size for e in entries) > 100 * 1024**2:
            raise ValueError("Archive exceeds bounded Details package limits")
        seen = set()
        for entry in entries:
            name = safe_path(entry.filename)
            if name.casefold() in seen or stat.S_ISLNK(entry.external_attr >> 16) or entry.flag_bits & 1:
                raise ValueError("Duplicate, symbolic-link or encrypted entry")
            seen.add(name.casefold())
            if not entry.is_dir():
                files[name] = archive.read(entry)  # zipfile verifies the CRC.
    if {PurePosixPath(name).parts[0] for name in files} != set(ROOTS):
        raise ValueError("Unexpected Details folder set")
    return files


def adapt(files):
    result, changed = dict(files), []
    for name, data in files.items():
        path = PurePosixPath(name)
        if len(path.parts) == 2 and path.name.lower().endswith(("_wrath.toc", "-wrath.toc")):
            new, count = INTERFACE.subn(rb"\g<1>30403", data)
            if count > 1:
                raise ValueError("Ambiguous TOC interface declaration")
            if count:
                result[name] = new
                changed.append(name)
    for name, data in files.items():
        if name not in changed and result[name] != data:
            raise ValueError("Unexpected non-TOC change")
    return result, sorted(changed)


def verify_load_graph(files):
    index = {name.casefold(): name for name in files}
    roots, pending = [], []
    for root in ROOTS:
        candidates = [name for name, data in files.items() if PurePosixPath(name).parent == PurePosixPath(root)
                      and name.lower().endswith(("_wrath.toc", "-wrath.toc"))
                      and re.search(rb"(?m)^## Interface:[^\r\n]*\b30403\b", data)]
        if not candidates:
            raise ValueError(f"Launcher would reject {root}: no 30403 Wrath TOC")
        roots.extend(candidates)
        pending.extend(candidates)
    visited, references = set(), 0
    while pending:
        name = pending.pop()
        if name in visited:
            continue
        visited.add(name)
        text = files[name].decode("utf-8-sig")
        if name.lower().endswith(".toc"):
            refs = [line.strip() for line in text.splitlines()
                    if line.strip() and not line.lstrip().startswith("#")
                    and line.strip().lower().endswith((".lua", ".xml"))]
        else:
            xml = ET.fromstring(text)
            refs = [node.attrib["file"] for node in xml.iter()
                    if node.tag.split("}")[-1] in ("Script", "Include") and "file" in node.attrib]
        for ref in refs:
            parts = list(PurePosixPath(name).parent.parts)
            for part in ref.replace("\\", "/").split("/"):
                if part == "..":
                    if len(parts) <= 1:
                        raise ValueError("Load reference leaves addon folder")
                    parts.pop()
                elif part not in ("", "."):
                    parts.append(part)
            target = "/".join(parts)
            if target.casefold() not in index:
                raise ValueError(f"Missing load reference: {name} -> {target}")
            actual = index[target.casefold()]
            references += 1
            if actual.lower().endswith(".xml"):
                pending.append(actual)
    return {"wrathTocs": len(roots), "tocAndXmlDocuments": len(visited), "references": references}


def prepare(source, output):
    output = output.absolute()
    if not output.is_dir() or output == output.parent or any(output.iterdir()):
        raise ValueError("Use a separate existing EMPTY staging folder")
    if source.resolve().is_relative_to(output.resolve()):
        raise ValueError("Source cannot be inside output")
    for parent in (output, *output.parents):
        if parent.is_symlink() or (hasattr(parent, "is_junction") and parent.is_junction()):
            raise ValueError("Linked output path refused")
    original = read_archive(source)
    if b"#Details." + VERSION.encode() not in original["Details/Details_Wrath.toc"]:
        raise ValueError("Unexpected Details version")
    files, changed = adapt(original)
    graph = verify_load_graph(files)
    archive_path = output / FILENAME
    with zipfile.ZipFile(archive_path, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            info = zipfile.ZipInfo(name, (2026, 9, 9, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data, compresslevel=9)
    package_hash = sha(archive_path.read_bytes())
    reread = read_archive(archive_path, package_hash)
    if reread != files:
        raise ValueError("Packaged payload differs after ZIP reread")
    metadata = {"id": "details", "name": "Details!", "version": VERSION, "interface": "30403",
                "url": "https://animeclub.fr/wotlk/addons/packages/" + FILENAME,
                "size": archive_path.stat().st_size, "sha256": package_hash, "installHash": package_hash,
                "stripPrefix": "", "components": [], "tokenReplacements": {}, "folders": ROOTS,
                "sourceUrl": "https://www.curseforge.com/wow/addons/details",
                "knownLimitations": "Version choisie par Atlas. Seules les declarations Interface des TOC Wrath ont ete alignees sur 30403; code Lua d'origine inchange. Sauvegarder les reglages avant de revenir depuis une version plus recente."}
    report = {"status": "prepared-not-published", "sourceSha256": SOURCE_SHA,
              "sourceBytes": source.stat().st_size, "package": metadata, "files": len(files),
              "luaFilesUnchanged": sum(name.lower().endswith(".lua") for name in files),
              "changedTocFiles": changed, "allOtherPayloadBytesUnchanged": True, "loadGraph": graph,
              "sourceArchiveUnchanged": sha(source.read_bytes()) == SOURCE_SHA,
              "clientWritten": False, "savedVariablesTouched": False, "gameLaunched": False,
              "serverPublished": False, "runtimeValidatedByAgent": False}
    for name, content in (("details-package.json", metadata), ("details-preparation.json", report)):
        with (output / name).open("x", encoding="utf-8") as stream:
            json.dump(content, stream, indent=2, ensure_ascii=False)
            stream.write("\n")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(prepare(args.archive, args.output_dir), indent=2))
