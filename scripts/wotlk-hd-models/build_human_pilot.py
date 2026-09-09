"""Build a NON-INSTALLABLE, offline HumanMale format pilot for Classic 54261.

This deliberately supports one SHA-pinned source model, not arbitrary M2 files.
No client path, game executable, network download or installation is involved.
The output remains an experimental asset set until runtime and DB2 work is done.
"""

import argparse
import hashlib
import importlib.util
import json
import math
import pathlib
import struct

_spec = importlib.util.spec_from_file_location("inspect_mpq", pathlib.Path(__file__).with_name("inspect_mpq.py"))
_mpq = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_mpq)
Mpq, extract_asset, safe_asset_path = _mpq.Mpq, _mpq.extract_asset, _mpq.safe_asset_path


MODEL = "Character/Human/Male/HumanMale.m2"
SOURCE_SHA = "b059ed5972a90664a751d0fb6e4375117b2f8ff5959de218742f6b0303e01682"
SECTIONS_SHA = "300c32ed09442f055a0bbfbe84f208d9bceb1de678f0e6a5eaa30810ee9edb14"
LISTFILE_SHA = "fdb3ddea306322f33b65416abf2e889e1b7bddc2d62738b84c66fe9f16398225"
PREFIX = "Character/Human/Male/HumanMale"
MAX_BYTES = 64 * 1024**2


def sha(data):
    return hashlib.sha256(data).hexdigest()


def array(data, header, width, label):
    if header < 0 or header + 8 > len(data):
        raise ValueError(f"{label}: missing array header")
    count, offset = struct.unpack_from("<II", data, header)
    if count > MAX_BYTES // width or (count and offset + count * width > len(data)):
        raise ValueError(f"{label}: array outside its owning file")
    return count, offset


def sequence_rows(model):
    count, offset = array(model, 28, 64, "sequences")
    return [struct.unpack_from("<HH8xI", model, offset + i * 64) for i in range(count)]


def external_sequences(model):
    return [(i, anim, sub) for i, (anim, sub, flags) in enumerate(sequence_rows(model))
            if flags & 0x170 == 0]


def explicit_textures(model):
    count, offset = array(model, 80, 16, "textures")
    result = []
    for i in range(count):
        kind = struct.unpack_from("<I", model, offset + i * 16)[0]
        length, start = array(model, offset + i * 16 + 8, 1, "texture name")
        name = model[start:start + length].rstrip(b"\0").decode("utf-8") if length else ""
        if kind == 0 and not name:
            raise ValueError("Explicit texture without a filename")
        if kind == 0:
            safe_asset_path(name)
        result.append(name.replace("\\", "/") if kind == 0 else None)
    return result


def human_sections(dbc):
    """Inventory old customization resources; NEVER emit a pretend Classic DB2."""
    if len(dbc) < 20 or dbc[:4] != b"WDBC":
        raise ValueError("Expected CharSections WDBC")
    count, fields, record_size, string_size = struct.unpack_from("<4I", dbc, 4)
    start = 20 + count * record_size
    if fields != 10 or record_size != 40 or start + string_size != len(dbc):
        raise ValueError("Unexpected CharSections schema/size")
    strings = dbc[start:]
    rows = []
    for index in range(count):
        record = struct.unpack_from("<10I", dbc, 20 + index * 40)
        if record[1:3] != (1, 0):
            continue
        textures = []
        for offset in record[4:7]:
            if offset >= len(strings) or b"\0" not in strings[offset:]:
                raise ValueError("CharSections string outside table")
            name = strings[offset:strings.index(b"\0", offset)].decode("utf-8").replace("\\", "/")
            if name:
                safe_asset_path(name)
                textures.append(name)
        rows.append({"id": record[0], "baseSection": record[3], "textures": textures,
                     "flags": record[7], "variation": record[8], "color": record[9]})
    return rows


def resolve_ids(listfile, wanted):
    wanted = {name.lower().replace("\\", "/") for name in wanted}
    result = {}
    with listfile.open(encoding="utf-8-sig") as stream:
        for line in stream:
            parts = line.rstrip("\r\n").split(";", 1)
            if len(parts) != 2 or parts[1].lower() not in wanted:
                continue
            key, value = parts[1].lower(), int(parts[0])
            if not 0 < value < 0x80000000 or (key in result and result[key] != value):
                raise ValueError("Ambiguous or invalid FileDataID")
            result[key] = value
    return result


def validate_track(model, at, width, external, label, spline_width=True, values=True):
    size = 20 if values else 12
    if at + size > len(model):
        raise ValueError(f"{label}: truncated track")
    interpolation, global_sequence = struct.unpack_from("<Hh", model, at)
    loops, _ = array(model, 20, 4, "global sequences")
    if interpolation > 3 or global_sequence < -1 or global_sequence >= loops:
        raise ValueError(f"{label}: invalid interpolation/global sequence")
    if spline_width and interpolation in (2, 3):
        width *= 3
    checked = 0
    for header, element_size in [(at + 4, 4)] + ([(at + 12, width)] if values else []):
        count, offset = array(model, header, 8, label + " outer")
        if count > len(sequence_rows(model)):
            raise ValueError(f"{label}: excessive sequence count")
        for i in range(count):
            entries, start = struct.unpack_from("<II", model, offset + i * 8)
            owner = model if global_sequence != -1 else external.get(i, model)
            if entries > MAX_BYTES // element_size or (entries and start + entries * element_size > len(owner)):
                raise ValueError(f"{label}[{i}]: nested data outside owning file ({entries}@{start}/{len(owner)})")
            checked += bool(entries)
    return checked


def validate_model(model, external, modern=False):
    version = 274 if modern else 264
    if len(model) < 304 or model[:4] != b"MD20" or struct.unpack_from("<I", model, 4)[0] != version:
        raise ValueError("Unexpected M2 header/version")
    widths = {8: 1, 20: 4, 28: 64, 36: 2, 44: 88, 52: 2, 60: 48,
              72: 40, 80: 16, 88: 20, 96: 60, 104: 2, 112: 4, 120: 2,
              128: 2, 136: 2, 144: 2, 152: 2, 216: 2, 224: 12, 232: 12,
              240: 40, 248: 2, 256: 36, 264: 156, 272: 116 if modern else 100,
              280: 2, 288: 176, 296: 476}
    tables = {p: array(model, p, w, f"header {p}") for p, w in widths.items()}
    # Unsupported structures must fail closed, even if their top-level array fits.
    if any(tables[p][0] for p in (72, 96, 264, 288, 296)):
        raise ValueError("This pilot does not convert colors, UV tracks, lights or emitters")
    sequences = sequence_rows(model)
    for p, limit in ((36, len(sequences)), (52, tables[44][0]), (120, tables[44][0])):
        count, offset = tables[p]
        for i in range(count):
            value = struct.unpack_from("<H", model, offset + i * 2)[0]
            if value != 65535 and value >= limit:
                raise ValueError(f"Lookup {p} outside referenced table")
    bones, start = tables[44]
    tracks = 0
    for i in range(bones):
        at = start + i * 88
        parent = struct.unpack_from("<h", model, at + 8)[0]
        if parent < -1 or parent >= bones or parent == i:
            raise ValueError("Invalid bone parent")
        for delta, width in ((16, 12), (36, 8), (56, 12)):
            tracks += validate_track(model, at + delta, width, external, f"bone {i}/{delta}")
    for i in range(tables[60][0]):
        at = tables[60][1] + i * 48
        if any(weight and bone >= bones for weight, bone in zip(model[at+12:at+16], model[at+16:at+20])):
            raise ValueError("Vertex references missing bone")
        if not all(math.isfinite(f) for f in struct.unpack_from("<3f", model, at)):
            raise ValueError("Non-finite vertex")
    for p, stride, track_offset, width, values in ((88, 20, 0, 2, True), (240, 40, 20, 1, True), (256, 36, 24, 0, False)):
        for i in range(tables[p][0]):
            tracks += validate_track(model, tables[p][1] + i * stride + track_offset,
                                     width, external, f"track {p}/{i}", values=values)
    for i in range(tables[272][0]):
        at = tables[272][1] + i * widths[272]
        for delta, width in ((12 if modern else 16, 12), (44 if modern else 48, 12), (76 if modern else 80, 4)):
            tracks += validate_track(model, at + delta, width, external, f"camera {i}/{delta}")
        if modern:
            tracks += validate_track(model, at + 96, 4, external, f"camera {i}/fov")
    return {"vertices": tables[60][0], "bones": bones, "sequences": len(sequences),
            "externalAnimations": len(external), "cameras": tables[272][0], "nonemptyTrackArraysChecked": tracks}


def append_aligned(data, extra):
    data.extend(b"\0" * (-len(data) % 4))
    offset = len(data)
    data.extend(extra)
    return offset


def constant_track(model, value):
    times = append_aligned(model, struct.pack("<I", 0))
    values = append_aligned(model, struct.pack("<f", value))
    times_outer = append_aligned(model, struct.pack("<II", 1, times))
    values_outer = append_aligned(model, struct.pack("<II", 1, values))
    return struct.pack("<HhIIII", 0, -1, 1, times_outer, 1, values_outer)


def chunk(tag, data):
    return struct.pack("<4sI", tag, len(data)) + data


def convert_model(source, ids):
    model = bytearray(source)
    struct.pack_into("<I", model, 4, 274)
    # Experimental: AFM2 external animation chunks follow the stock Classic flag.
    struct.pack_into("<I", model, 16, struct.unpack_from("<I", source, 16)[0] | 0x200000)
    count, offset = array(source, 272, 100, "old cameras")
    cameras = []
    for i in range(count):
        camera = source[offset + i * 100:offset + (i + 1) * 100]
        fov = struct.unpack_from("<f", camera, 4)[0]
        if not math.isfinite(fov) or not 0 < fov < math.pi:
            raise ValueError("Invalid field of view")
        cameras.append(camera[:4] + camera[8:] + constant_track(model, fov))
    camera_offset = append_aligned(model, b"".join(cameras))
    struct.pack_into("<I", model, 276, camera_offset)
    txids = []
    _, textures_offset = array(source, 80, 16, "textures")
    for index, path in enumerate(explicit_textures(source)):
        txids.append(ids[path.lower()] if path else 0)
        struct.pack_into("<II", model, textures_offset + index * 16 + 8, 0, 0)
    skins = [ids[f"{PREFIX}{i:02d}.skin".lower()] for i in range(struct.unpack_from("<I", source, 68)[0])]
    afid = b"".join(struct.pack("<HHI", anim, sub, ids[f"{PREFIX}{anim:04d}-{sub:02d}.anim".lower()])
                    for _, anim, sub in external_sequences(source))
    result = chunk(b"MD21", model) + chunk(b"SFID", struct.pack(f"<{len(skins)}I", *skins))
    result += chunk(b"TXID", struct.pack(f"<{len(txids)}I", *txids)) + chunk(b"AFID", afid)
    return result, bytes(model)


def validate_skin(data, vertices, modern=False):
    if len(data) < (64 if modern else 48) or data[:4] != b"SKIN":
        raise ValueError("Invalid skin header")
    tables = {p: array(data, p, w, f"skin {p}") for p, w in ((4, 2), (12, 2), (20, 4), (28, 48), (36, 24))}
    if tables[12][0] % 3 or tables[4][0] != tables[20][0]:
        raise ValueError("Invalid triangle/property counts")
    for p, limit in ((4, vertices), (12, tables[4][0])):
        count, offset = tables[p]
        if count and max(struct.unpack_from(f"<{count}H", data, offset)) >= limit:
            raise ValueError("Skin index outside geometry")
    for i in range(tables[28][0]):
        _, level, start, count, tri, triangles = struct.unpack_from("<6H", data, tables[28][1] + i * 48)
        if start + count > tables[4][0] or (tri + (level << 16)) + triangles > tables[12][0]:
            raise ValueError("Submesh outside index tables")
    if modern:
        array(data, 48, 12, "shadow batches")
    return {"vertices": tables[4][0], "triangleIndices": tables[12][0], "submeshes": tables[28][0], "batches": tables[36][0]}


def convert_skin(source):
    target = bytearray(source[:48] + b"\0" * 16 + source[48:])
    for p in (4, 12, 20, 28, 36):
        count, offset = struct.unpack_from("<II", source, p)
        if count and offset < 48:
            raise ValueError("Skin array overlaps old header")
        struct.pack_into("<I", target, p + 4, offset + 16 if count else 0)
    struct.pack_into("<I", target, 44, 0)  # lodVertexBase; old max-bones field is not retained.
    # Empty shadow batches are intentional and remain a documented runtime gap.
    return bytes(target)


def create_output(root, paths):
    root = root.absolute()
    if not root.is_dir() or root == root.parent or any(root.iterdir()):
        raise ValueError("Output must be an existing EMPTY staging directory")
    for parent in (root, *root.parents):
        if parent.is_symlink() or (hasattr(parent, "is_junction") and parent.is_junction()):
            raise ValueError("Output must not traverse links or junctions")
    root = root.resolve(strict=True)
    for path in paths:
        resolved = path.resolve(strict=True)
        if resolved.is_relative_to(root):
            raise ValueError("Inputs must not be under output")
    return root


def build(archive, listfile, root):
    source = archive.read(MODEL)
    if sha(source) != SOURCE_SHA:
        raise ValueError("This pilot only supports the audited Leeviathan HumanMale source")
    section_data = archive.read("DBFilesClient/CharSections.dbc")
    if sha(section_data) != SECTIONS_SHA:
        raise ValueError("Expected the audited CharSections table")
    with listfile.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != LISTFILE_SHA:
            raise ValueError("Expected the verified 202609081819 community listfile")
    sections = human_sections(section_data)
    textures = {name for row in sections for name in row["textures"]}
    textures.update(path for path in explicit_textures(source) if path)
    skin_names = [f"{PREFIX}{i:02d}.skin" for i in range(struct.unpack_from("<I", source, 68)[0])]
    anim_names = {i: f"{PREFIX}{a:04d}-{s:02d}.anim" for i, a, s in external_sequences(source)}
    required = [MODEL, *skin_names, *anim_names.values(), *sorted(textures)]
    ids = resolve_ids(listfile, required)
    missing_ids = sorted({name for name in required if name.lower() not in ids})
    mandatory = [MODEL, *skin_names, *anim_names.values(), *(p for p in explicit_textures(source) if p)]
    if any(name.lower() not in ids for name in mandatory):
        raise ValueError("Missing required geometry FileDataID; no invented IDs allowed")
    external = {i: archive.read(name) for i, name in anim_names.items()}
    before = validate_model(source, external)
    converted, inner = convert_model(source, ids)
    after = validate_model(inner, external, modern=True)
    vertex_count, vertex_offset = array(source, 60, 48, "vertices")
    if source[vertex_offset:vertex_offset + vertex_count * 48] != inner[vertex_offset:vertex_offset + vertex_count * 48]:
        raise ValueError("Conversion changed vertex/weight/UV data")
    outputs = [(MODEL, source, converted, "experimental-model")]
    skin_report = []
    for name in skin_names:
        raw = archive.read(name)
        old_stats = validate_skin(raw, vertex_count)
        new = convert_skin(raw)
        new_stats = validate_skin(new, vertex_count, modern=True)
        if old_stats != new_stats or raw[48:] != new[64:]:
            raise ValueError("Skin conversion changed geometry")
        skin_report.append({"path": name, **new_stats})
        outputs.append((name, raw, new, "experimental-skin"))
    for i, name in anim_names.items():
        raw = external[i]
        if raw[:4] == b"AFM2":
            raise ValueError("Expected raw 3.3.5 animation data")
        outputs.append((name, raw, chunk(b"AFM2", raw), "experimental-animation"))
    for name in sorted(textures):
        raw = archive.read(name)
        if raw[:4] not in (b"BLP1", b"BLP2"):
            raise ValueError("Texture is not a BLP file")
        outputs.append((name, raw, raw, "customization-resource-not-wired"))
    # Everything is read and validated before the first output is written.
    assets = []
    for name, raw, result, role in outputs:
        extract_asset(root, name, result)
        assets.append({"path": name, "fileDataId": ids.get(name.lower()), "role": role,
                       "sourceSha256": sha(raw), "outputSha256": sha(result), "bytes": len(result)})
    report = {"schemaVersion": 1, "status": "offline-format-prototype-not-installable", "targetBuild": "3.4.3.54261",
              "sourceModelSha256": SOURCE_SHA, "sourceValidation": before, "convertedValidation": after,
              "sourceCharSectionsSha256": SECTIONS_SHA, "communityListfileSha256": LISTFILE_SHA,
              "verticesWeightsUvsUnchanged": True, "skinGeometryUnchanged": True, "skins": skin_report,
              "customizationRows": len(sections), "customizationTextures": len(textures),
              "missingCustomizationFileDataIds": missing_ids, "assets": assets,
              "pending": ["Classic DB2 customization/material-resource mapping", "Shadow batches and LOD behavior",
                          "Chunked animation flags and animation/geoset behavior in the real client",
                          "Loader compatibility with the unpacked 54261 process", "Visual test and equipment coverage"],
              "runtimeValidated": False, "clientWritten": False, "gameLaunched": False}
    for name, content in (("pilot-manifest.json", report), ("legacy-customization-inventory.json", sections)):
        with (root / name).open("x", encoding="utf-8") as stream:
            json.dump(content, stream, indent=2, ensure_ascii=False)
            stream.write("\n")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stormlib", type=pathlib.Path, required=True)
    parser.add_argument("--archive", type=pathlib.Path, required=True)
    parser.add_argument("--listfile", type=pathlib.Path, required=True)
    parser.add_argument("--output-dir", type=pathlib.Path, required=True)
    args = parser.parse_args()
    root = create_output(args.output_dir, [args.archive, args.stormlib, args.listfile])
    archive = Mpq(args.stormlib, args.archive)
    try:
        report = build(archive, args.listfile, root)
        print(json.dumps({k: v for k, v in report.items() if k != "assets"}, indent=2))
    finally:
        archive.close()


if __name__ == "__main__":
    main()
