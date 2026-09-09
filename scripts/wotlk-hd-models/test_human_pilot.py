"""Synthetic tests: no proprietary assets, native DLLs, client or network needed."""

import importlib.util
import json
import pathlib
import struct
import tempfile
import unittest


def module(name):
    spec = importlib.util.spec_from_file_location(name, pathlib.Path(__file__).with_name(name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


pilot = module("build_human_pilot")
mpq = module("inspect_mpq")
patterns = module("inspect_loader_patterns")


def camera_model():
    data = bytearray(468)
    data[:4] = b"MD20"
    struct.pack_into("<I", data, 4, 264)
    struct.pack_into("<I", data, 68, 1)
    struct.pack_into("<II", data, 28, 1, 404)
    struct.pack_into("<I", data, 416, 0x20)
    struct.pack_into("<II", data, 272, 1, 304)
    struct.pack_into("<Ifff", data, 304, 0, 0.785398, 1000, 0.01)
    for delta in (16, 48, 80):
        struct.pack_into("<Hh", data, 304 + delta, 0, -1)
    return data


def skin():
    data = bytearray(60)
    data[:4] = b"SKIN"
    for p, count, offset in ((4, 1, 48), (12, 3, 50), (20, 1, 56)):
        struct.pack_into("<II", data, p, count, offset)
    struct.pack_into("<I", data, 44, 256)
    return data


class ModelTests(unittest.TestCase):
    def test_camera_append_keeps_original_data_offsets(self):
        source = camera_model()
        result, inner = pilot.convert_model(source, {(pilot.PREFIX + "00.skin").lower(): 471055})
        self.assertEqual(result[:4], b"MD21")
        self.assertEqual(struct.unpack_from("<I", inner, 4)[0], 274)
        self.assertEqual(inner[304:404], source[304:404])
        self.assertEqual(pilot.validate_model(source, {})["cameras"], 1)
        self.assertEqual(pilot.validate_model(inner, {}, modern=True)["cameras"], 1)
        _, offset = pilot.array(inner, 272, 116, "cameras")
        self.assertGreaterEqual(offset, len(source))
        n, outer = pilot.array(inner, offset + 108, 8, "fov")
        self.assertEqual(n, 1)
        n, values = struct.unpack_from("<II", inner, outer)
        self.assertEqual(n, 1)
        self.assertAlmostEqual(struct.unpack_from("<f", inner, values)[0], 0.785398, places=6)

    def test_invalid_fov_rejected(self):
        source = camera_model()
        struct.pack_into("<f", source, 308, float("nan"))
        with self.assertRaisesRegex(ValueError, "field of view"):
            pilot.convert_model(source, {})

    def test_array_bounds(self):
        for count, offset in ((100, 8), (1, 100), (0xFFFFFFFF, 8)):
            with self.subTest(count=count), self.assertRaises(ValueError):
                pilot.array(struct.pack("<II", count, offset), 0, 4, "bad")

    def test_empty_array_offset_is_not_dereferenced(self):
        self.assertEqual(pilot.array(struct.pack("<II", 0, 999), 0, 4, "empty"), (0, 999))

    def test_unhandled_particle_fails_closed(self):
        data = camera_model() + bytearray(476)
        struct.pack_into("<II", data, 296, 1, 404)
        with self.assertRaisesRegex(ValueError, "does not convert"):
            pilot.validate_model(data, {})

    def test_sequence_external_flags_and_aliases(self):
        data = camera_model() + bytearray(5 * 64)
        struct.pack_into("<II", data, 28, 5, 404)
        for i, flag in enumerate((0, 0x10, 0x20, 0x100, 0x40)):
            struct.pack_into("<HH8xI", data, 404 + i * 64, 60 + i, 0, flag)
        self.assertEqual(pilot.external_sequences(data), [(0, 60, 0)])

    def test_nested_external_offsets_use_animation_file(self):
        data = bytearray(400)
        struct.pack_into("<II", data, 28, 1, 200)
        struct.pack_into("<HhIIII", data, 100, 0, -1, 1, 300, 1, 308)
        struct.pack_into("<IIII", data, 300, 1, 800, 1, 804)
        self.assertEqual(pilot.validate_track(data, 100, 4, {0: bytes(808)}, "test"), 2)
        with self.assertRaisesRegex(ValueError, "owning file"):
            pilot.validate_track(data, 100, 4, {0: bytes(807)}, "test")

    def test_wrong_model_version(self):
        data = camera_model()
        struct.pack_into("<I", data, 4, 272)
        with self.assertRaises(ValueError):
            pilot.validate_model(data, {})

    def test_animation_wrapper_keeps_payload(self):
        raw = bytes(range(256))
        wrapped = pilot.chunk(b"AFM2", raw)
        self.assertEqual(struct.unpack_from("<4sI", wrapped), (b"AFM2", 256))
        self.assertEqual(wrapped[8:], raw)


class SkinTests(unittest.TestCase):
    def test_relocation_preserves_geometry(self):
        source = skin()
        result = pilot.convert_skin(source)
        self.assertEqual(source[48:], result[64:])
        self.assertEqual(pilot.validate_skin(source, 1), pilot.validate_skin(result, 1, modern=True))
        self.assertEqual(struct.unpack_from("<I", result, 44)[0], 0)
        self.assertEqual(struct.unpack_from("<II", result, 48), (0, 0))

    def test_invalid_vertex_index(self):
        data = skin()
        struct.pack_into("<H", data, 48, 2)
        with self.assertRaisesRegex(ValueError, "index outside"):
            pilot.validate_skin(data, 1)

    def test_invalid_triangle_count(self):
        data = skin()
        struct.pack_into("<I", data, 12, 2)
        with self.assertRaisesRegex(ValueError, "triangle/property"):
            pilot.validate_skin(data, 1)

    def test_header_overlap_rejected(self):
        data = skin()
        struct.pack_into("<I", data, 8, 4)
        with self.assertRaisesRegex(ValueError, "overlaps"):
            pilot.convert_skin(data)


class AssetSafetyTests(unittest.TestCase):
    def test_unsafe_paths(self):
        for name in ("../a.m2", "/a.m2", "C:/a.m2", "a.exe", "a:stream.m2", "NUL.blp", "a /b.m2", "a\0.m2", "a?.blp", "a\\..\\b.skin"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                mpq.safe_asset_path(name)

    def test_safe_paths(self):
        self.assertEqual(str(mpq.safe_asset_path("Character\\Human\\Male\\HumanMale.m2")), pilot.MODEL)

    def test_no_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            mpq.extract_asset(root, "human/a.m2", b"original")
            with self.assertRaises(FileExistsError):
                mpq.extract_asset(root, "human/a.m2", b"changed")
            self.assertEqual((root / "human/a.m2").read_bytes(), b"original")

    def test_output_must_be_empty(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            self.assertEqual(pilot.create_output(root, []), root.resolve())
            (root / "keep").touch()
            with self.assertRaisesRegex(ValueError, "EMPTY"):
                pilot.create_output(root, [])

    def test_id_mapping_does_not_invent_ids(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "list.csv"
            path.write_text("119940;character/human/male/humanmale.m2\n", encoding="utf-8")
            ids = pilot.resolve_ids(path, [pilot.MODEL, "missing.m2"])
            self.assertEqual(ids, {pilot.MODEL.lower(): 119940})

    def test_ambiguous_id_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "list.csv"
            path.write_text("1;a.m2\n2;a.m2\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Ambiguous"):
                pilot.resolve_ids(path, ["a.m2"])

    def test_mpq_chunk_bounds(self):
        with self.assertRaisesRegex(ValueError, "exceeds"):
            mpq.summary(b"MD21" + struct.pack("<I", 100))
        self.assertEqual(mpq.summary(pilot.chunk(b"MD21", b"test"))["chunks"], [{"tag": "MD21", "bytes": 4}])

    def test_dbc_is_inventory_only(self):
        text = b"\0Character/Human/Male/test.blp\0"
        record = struct.pack("<10I", 1, 1, 0, 0, 1, 0, 0, 17, 0, 0)
        dbc = struct.pack("<4s4I", b"WDBC", 1, 10, 40, len(text)) + record + text
        result = pilot.human_sections(dbc)
        self.assertEqual(result[0]["textures"], ["Character/Human/Male/test.blp"])
        self.assertNotIn("db2", json.dumps(result))
        with self.assertRaises(ValueError):
            pilot.human_sections(dbc[:-1])


class PatternTests(unittest.TestCase):
    def test_wildcards_match_newlines(self):
        source = "LoadByFileId = { 0x48, -1, 1, 2, 3, 4, 5, 6 };"
        self.assertIsNotNone(patterns.compile_pattern(source, "LoadByFileId").search(b"H\n\1\2\3\4\5\6"))

    def test_short_or_missing_patterns_rejected(self):
        for source in ("", "LoadByFileId = { 1, 2 };"):
            with self.assertRaises(ValueError):
                patterns.compile_pattern(source, "LoadByFileId")

    def test_non_pe_rejected(self):
        with self.assertRaises(ValueError):
            patterns.pe_sections(bytes(64))

    def test_section_bounds_rejected(self):
        data = bytearray(128)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 60, 64)
        data[64:68] = b"PE\0\0"
        struct.pack_into("<HH", data, 68, 0x8664, 1)
        struct.pack_into("<II", data, 104, 1000, 100)
        with self.assertRaisesRegex(ValueError, "outside"):
            patterns.pe_sections(data)


if __name__ == "__main__":
    unittest.main()
