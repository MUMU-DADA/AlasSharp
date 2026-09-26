"""Fail-closed regressions for the build-time MAP compiler; no product runtime."""
from __future__ import annotations

import contextlib
import copy
import io
from pathlib import Path
import tempfile
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/migration"))
import compile_campaign_maps as compiler


class CompilerChecks(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        self.source = "campaign/example/a.py"
        self.path = self.root / self.source
        self.path.parent.mkdir(parents=True)
        self.path.write_text("MAP = CampaignMap('A')\nMAP.shape = 'B2'\nMAP.map_data = '-- --\\n-- --'\n", encoding="utf-8")
        self.value = dict(name="A", values=dict(shape="B2", map_data="-- --\n-- --"), calls=[],
                          origins={"shape": dict(module="campaign.example.a", line=2),
                                   "map_data": dict(module="campaign.example.a", line=3)})

    def entry(self, **fields):
        value = copy.deepcopy(self.value)
        value["values"].update(fields)
        return compiler.map_entry(self.root, self.source, value)

    def test_unknown_field_class_and_call(self):
        for fields in (dict(new_mechanism=[]), dict(grid_class={"$ref": "unported.Grid"})):
            with self.subTest(fields=fields), self.assertRaises(ValueError):
                self.entry(**fields)
        value = copy.deepcopy(self.value)
        value["calls"] = [dict(method="new_action", args=["A1"], kwargs={})]
        with self.assertRaises(ValueError):
            compiler.map_entry(self.root, self.source, value)

    def test_typed_predicates(self):
        for name, value in (("is_siren", 1), ("enemy_scale", True), ("enemy_genre", 2)):
            with self.subTest(field=name), self.assertRaises(ValueError):
                compiler.ignored_prediction_expr([dict(method="ignore_prediction", args=["A1"], kwargs={name: value})], self.source)

    def test_invalid_sight(self):
        for sight in ([0, 0, -1, 1], [False, -1, 2, 2], [-3, -1, 3], [1, -1, 3, 2]):
            with self.subTest(sight=sight), self.assertRaises(ValueError):
                compiler.default_cameras(4, 4, sight)

    def test_native_empty_mechanism_groups(self):
        self.assertEqual(compiler.cell_group_expr([], self.source), "[]")
        self.assertIn("fortressEnemies: []", compiler.mechanism_expr(dict(fortress_data=[[], []]), self.source))

    def test_invalid_types_and_whitespace(self):
        for fields in (dict(camera_data=[1]), dict(spawn_data=False), dict(spawn_data_loop=False),
                       dict(portal_data=False), dict(map_data="--  --\n-- --"), dict(map_data_loop="--"),
                       dict(camera_data=None), dict(wall_data=False), dict(weight_data="1 2 3 4")):
            with self.subTest(fields=fields), self.assertRaises(ValueError):
                self.entry(**fields)
        self.assertNotIn("loopTiles", self.entry(map_data_loop=""))

    def test_overwrite_requires_explicit_migration(self):
        with self.path.open("a", encoding="utf-8") as output:
            output.write("MAP.shape = 'B2'\n")
        with self.assertRaisesRegex(ValueError, "repeated MAP setter"):
            self.entry()

    def test_initialization_order_requires_explicit_migration(self):
        value = copy.deepcopy(self.value)
        value["origins"]["camera_data"] = dict(module="campaign.example.a", line=1)
        value["values"]["camera_data"] = ["A1"]
        with self.assertRaisesRegex(ValueError, "precedes grid initialization"):
            compiler.map_entry(self.root, self.source, value)

    def test_no_json_and_dependency_output_inventory_drift(self):
        # A tiny real source tree exercises the static resolver without any
        # JSON export directory. Only source declarations are compiler inputs.
        for relative, text in {
            "module/map/map_base.py": "class CampaignMap:\n    pass\n",
            "module/map/utils.py": "# source dependency\n",
            "module/map_detection/grid_info.py": "class GridInfo:\n    pass\n",
        }.items():
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        self.path.write_text("from module.map.map_base import CampaignMap\n" + self.path.read_text(encoding="utf-8"), encoding="utf-8")
        output = self.root / "compiled.cs"
        with contextlib.redirect_stdout(io.StringIO()):
            compiler.generate(self.root, output)
            compiler.generate(self.root, output, check=True)
            self.assertFalse((self.root / "data").exists())
            original = output.read_text(encoding="utf-8")
            output.write_text(original + "// damaged\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "drift"):
                compiler.generate(self.root, output, check=True)
            output.write_text(original, encoding="utf-8")
            dependency = self.root / "module/map/utils.py"
            dependency.write_text("# changed\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "drift"):
                compiler.generate(self.root, output, check=True)
            dependency.write_text("# source dependency\n", encoding="utf-8")
            (self.path.parent / "helper.py").write_text("# newly added source\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "drift"):
                compiler.generate(self.root, output, check=True)


if __name__ == "__main__":
    unittest.main()
