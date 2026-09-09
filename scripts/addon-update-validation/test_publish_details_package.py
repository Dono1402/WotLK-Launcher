import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("details_publish", Path(__file__).with_name("publish_details_package.py"))
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


def fixture():
    fields = {"id": "details", "name": "Details!", "version": publisher.VERSION,
              "interface": "30403", "url": "https://animeclub.fr/wotlk/addons/packages/" + publisher.FILENAME,
              "size": 4959885, "sha256": publisher.PACKAGE_SHA, "installHash": publisher.PACKAGE_SHA,
              "stripPrefix": "", "components": [], "tokenReplacements": {}, "folders": ["Details"],
              "sourceUrl": "https://www.curseforge.com/wow/addons/details", "knownLimitations": "unchanged Lua"}
    old = {**fields, "version": "20250228.13407.162", "description": "preserve description", "category": "Combat"}
    catalog = {"schemaVersion": 1, "clientInterface": "30403", "generatedAtUtc": "preserve this",
               "addons": [old] + [{"id": f"other-{i}", "custom": {"nested": [i, "keep"]}} for i in range(13)]}
    return catalog, fields


class CatalogPublicationTests(unittest.TestCase):
    def test_only_details_changes_and_source_is_not_mutated(self):
        before, fields = fixture()
        snapshot = copy.deepcopy(before)
        result = publisher.update_catalog(before, fields)
        self.assertEqual(before, snapshot)
        self.assertEqual(result["addons"][1:], before["addons"][1:])
        self.assertEqual(result["generatedAtUtc"], before["generatedAtUtc"])
        self.assertEqual(result["addons"][0]["description"], "preserve description")
        self.assertEqual(result["addons"][0]["version"], publisher.VERSION)

    def test_changed_prior_version_is_refused(self):
        before, fields = fixture()
        before["addons"][0]["version"] = "different"
        with self.assertRaisesRegex(ValueError, "changed since approval"):
            publisher.update_catalog(before, fields)

    def test_membership_change_is_refused(self):
        before, fields = fixture()
        before["addons"].pop()
        with self.assertRaisesRegex(ValueError, "membership"):
            publisher.update_catalog(before, fields)

    def test_duplicate_target_is_refused(self):
        before, fields = fixture()
        before["addons"][1]["id"] = "details"
        with self.assertRaisesRegex(ValueError, "membership"):
            publisher.update_catalog(before, fields)

    def test_wrong_descriptor_is_refused(self):
        for key, value in (("url", "https://example.com/a.zip"), ("size", 1), ("sha256", "0" * 64),
                           ("installHash", "1" * 64), ("folders", ["Unrelated"]), ("components", [{}])):
            before, fields = fixture()
            fields[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                publisher.update_catalog(before, fields)

    def test_new_validation_claim_is_not_carried_over(self):
        before, fields = fixture()
        before["addons"][0]["atlasValidation"] = {"version": "older"}
        with self.assertRaisesRegex(ValueError, "fresh review"):
            publisher.update_catalog(before, fields)

    def test_unexpected_metadata_field_is_refused(self):
        before, fields = fixture()
        fields["unreviewed"] = True
        with self.assertRaisesRegex(ValueError, "fresh review"):
            publisher.update_catalog(before, fields)


if __name__ == "__main__":
    unittest.main()
