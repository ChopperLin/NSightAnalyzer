import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("sanitize_bridge", Path(__file__).with_name("sanitize-bridge-output.py"))
sanitizer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sanitizer)


class ShaderIdentitySanitizationTests(unittest.TestCase):
    def test_hash_mapping_is_stable_distinct_and_case_insensitive(self):
        first = sanitizer.Mapper()
        a = first.shader_hash("0x0123456789abcdef")
        b = first.shader_hash("0xfedcba9876543210")
        self.assertNotEqual(a, b)
        self.assertEqual(16, len(a))
        self.assertEqual(a, first.shader_hash("0123456789ABCDEF"))
        reverse_order = sanitizer.Mapper()
        self.assertEqual(b, reverse_order.shader_hash("fedcba9876543210"))
        self.assertEqual(a, reverse_order.shader_hash("0123456789abcdef"))

    def test_semantic_hash_and_source_references_survive_pointer_redaction(self):
        original = "0x0123456789abcdef"
        document = {
            "models": [
                {"class": sanitizer.SHADER_MODEL, "export": {"nodes": [{"cells": [
                    {"column": 2, "display": "Pointer " + original},
                    {"column": 3, "display": original},
                    {"column": 4, "tooltip": "Could not find debug symbols: private-shader.pdb"},
                    {"column": 5, "display": "dxil (0123456789abcdef)"}]}]}},
                {"class": sanitizer.SOURCE_MODEL, "export": {"nodes": [{"cells": [
                    {"column": 3, "display": "dxil (0123456789abcdef)"}]}]}},
            ],
            "targetModelSelection": {"cells": ["Compute", "", "Shader", original]},
            "modelSelector": {"column": 3, "kind": "match", "value": original},
        }
        mapper = sanitizer.Mapper()
        result = sanitizer.walk(document, mapper)
        mapped = mapper.shader_hash(original)
        self.assertEqual("0x" + mapped, result["models"][0]["export"]["nodes"][0]["cells"][1]["display"])
        self.assertEqual("Pointer 0x0000000000000000", result["models"][0]["export"]["nodes"][0]["cells"][0]["display"])
        self.assertEqual("Could not find debug symbols: source01.pdb",
                         result["models"][0]["export"]["nodes"][0]["cells"][2]["tooltip"])
        self.assertEqual("dxil (" + mapped + ")",
                         result["models"][0]["export"]["nodes"][0]["cells"][3]["display"])
        self.assertEqual("dxil (" + mapped + ")", result["models"][1]["export"]["nodes"][0]["cells"][0]["display"])
        self.assertEqual("0x" + mapped, result["targetModelSelection"]["cells"][3])
        self.assertEqual("0x" + mapped, result["modelSelector"]["value"])

    def test_hash_collisions_fail_instead_of_merging_identities(self):
        with patch.object(sanitizer.hashlib, "sha256", return_value=SimpleNamespace(hexdigest=lambda: "a" * 64)):
            mapper = sanitizer.Mapper()
            mapper.shader_hash("0123456789abcdef")
            with self.assertRaisesRegex(ValueError, "collision"):
                mapper.shader_hash("fedcba9876543210")


if __name__ == "__main__":
    unittest.main()
