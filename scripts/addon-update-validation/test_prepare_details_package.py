import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("details_prepare", Path(__file__).with_name("prepare_details_package.py"))
details = importlib.util.module_from_spec(spec)
spec.loader.exec_module(details)


def fixture():
    files = {}
    for root in details.ROOTS:
        files[root + "/" + root + "_Wrath.toc"] = b"## Interface: 30402\r\nmain.lua\r\n"
        files[root + "/main.lua"] = b"return 1\r\n"
    return files


class DetailsPreparationTests(unittest.TestCase):
    def test_only_wrath_interface_changes(self):
        files = fixture()
        files["Details/Details.toc"] = b"## Interface: 100200\n"
        files["Details/nested.toc"] = b"## Interface: 30402\n"
        result, changed = details.adapt(files)
        self.assertEqual(len(changed), 8)
        for name, data in files.items():
            expected = data.replace(b"30402", b"30403") if name in changed else data
            self.assertEqual(result[name], expected)

    def test_all_supported_root_graphs(self):
        converted, _ = details.adapt(fixture())
        self.assertEqual(details.verify_load_graph(converted),
                         {"wrathTocs": 8, "tocAndXmlDocuments": 8, "references": 8})

    def test_original_interface_is_rejected_by_launcher_rule(self):
        with self.assertRaisesRegex(ValueError, "Launcher would reject"):
            details.verify_load_graph(fixture())

    def test_main_30403_unchanged(self):
        files = {"Details/Details_Wrath.toc": b"## Interface: 30403\n"}
        self.assertEqual(details.adapt(files), (files, []))

    def test_unknown_interfaces_not_relabelled(self):
        files = {"Details/Details_Wrath.toc": b"## Interface: 100200\n"}
        self.assertEqual(details.adapt(files), (files, []))

    def test_metadata_does_not_change_version_or_saved_variables(self):
        original = b"## Interface: 30401\r\n## Version: test\r\n## SavedVariables: keep\r\n"
        result, _ = details.adapt({"Details/Details-Wrath.toc": original})
        self.assertEqual(result["Details/Details-Wrath.toc"], original.replace(b"30401", b"30403"))

    def test_multiple_interface_declarations_rejected(self):
        with self.assertRaisesRegex(ValueError, "Ambiguous"):
            details.adapt({"Details/Details_Wrath.toc": b"## Interface: 30402\n## Interface: 30401\n"})

    def test_missing_load_reference_rejected(self):
        files, _ = details.adapt(fixture())
        del files["Details/main.lua"]
        with self.assertRaisesRegex(ValueError, "Missing load reference"):
            details.verify_load_graph(files)

    def test_xml_graph_and_comments(self):
        files, _ = details.adapt(fixture())
        files["Details/Details_Wrath.toc"] += b"load.xml\n"
        files["Details/load.xml"] = b'<Ui><!-- <Script file="missing.lua"/> --><Script file="main.lua"/></Ui>'
        self.assertEqual(details.verify_load_graph(files)["references"], 10)

    def test_graph_cannot_leave_addon(self):
        files, _ = details.adapt(fixture())
        files["Details/Details_Wrath.toc"] += b"../outside.lua\n"
        with self.assertRaisesRegex(ValueError, "leaves addon"):
            details.verify_load_graph(files)

    def test_unsafe_archive_paths(self):
        for path in ("../Details/a.lua", "/Details/a.lua", "Details/../a.lua", "Details/a:stream.lua",
                     "Details/a?.lua", "SavedVariables/Details.lua", "Details/a /b.lua"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                details.safe_path(path)

    def test_wrong_source_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "wrong.zip"
            source.write_bytes(b"not the user archive")
            with self.assertRaisesRegex(ValueError, "SHA-pinned"):
                details.read_archive(source)

    def test_nonempty_output_refused_before_reading_archive(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "keep.txt").write_text("keep")
            with self.assertRaisesRegex(ValueError, "EMPTY"):
                details.prepare(root / "absent.zip", root)
            self.assertEqual((root / "keep.txt").read_text(), "keep")


if __name__ == "__main__":
    unittest.main()
