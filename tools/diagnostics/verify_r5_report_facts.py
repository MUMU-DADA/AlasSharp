#!/usr/bin/env python3
"""Offline counterexamples for incomplete-hook and state-write report facts."""
from __future__ import annotations

import ast
import contextlib
import io
import json
from pathlib import Path
import tempfile
import textwrap
import unittest
from unittest.mock import patch

import r5_incomplete_hooks as incomplete
import r5_state_mutation_audit as mutation

ROOT = Path(__file__).resolve().parents[2]


class ReportFacts(unittest.TestCase):
    def setUp(self):
        local = ROOT / ".runtime/verification"
        local.mkdir(parents=True, exist_ok=True)
        self.folder = tempfile.TemporaryDirectory(prefix="report-facts-", dir=local)
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(textwrap.dedent(text).lstrip("\n"), encoding="utf-8")
        return path

    def inventory(self, source, entries):
        self.write("campaign/stage.py", source)
        self.write("data/stage.json", json.dumps({"campaign": {"battles": entries}}))

    def audit_incomplete(self):
        report = self.root / "incomplete.md"
        with patch.multiple(incomplete, UPSTREAM=self.root, DATA=self.root / "data", REPORT=report), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            code = incomplete.main()
        return code, report.read_text(encoding="utf-8"), output.getvalue()

    def test_single_statement_and_other_hooks_are_counted(self):
        self.inventory("""
            class Campaign:
                def battle_0(self):
                    return unresolved()
                def handle_in_stage(self):
                    return unknown()
                def battle_1(self):
                    first()
                    return unknown()
            """, [{"method": name, "plan_complete": False, "unparsed": [reason]}
                  for name, reason in [("battle_0", "Return(expr)@3: unresolved()"),
                                       ("handle_in_stage", "Return(expr)@5: unknown()"),
                                       ("battle_1", "Expr@7: first()")]])
        code, report, _ = self.audit_incomplete()
        self.assertEqual(code, 0)
        self.assertIn("`plan_complete=false` 条目：**3**", report)
        self.assertIn("其中 `battle_*`：**2**", report)
        self.assertIn("源体语句数 > 1（解释性统计）：**1**", report)
        self.assertIn("| `Return(expr)` | 2 |", report)
        self.assertIn("`handle_in_stage`", report)

    def test_ratchet_does_not_expand_to_current_snapshot(self):
        names = [f"battle_{n}" for n in range(incomplete.BASELINE + 1)]
        self.inventory("class Campaign:\n" + "".join(
            f"    def {name}(self):\n        unknown()\n        return False\n" for name in names),
            [{"method": name, "plan_complete": False} for name in names])
        code, report, output = self.audit_incomplete()
        self.assertEqual(incomplete.BASELINE, 5)
        self.assertEqual(code, 1)
        self.assertIn("**5**", report)
        self.assertIn("超出基线 1 条", output)

    def test_complete_nonempty_snapshot_can_reach_zero(self):
        self.inventory("class Campaign:\n    def battle_0(self):\n        return True\n",
                       [{"method": "battle_0", "plan_complete": True}])
        code, report, _ = self.audit_incomplete()
        self.assertEqual(code, 0)
        self.assertIn("`plan_complete=false` 条目：**0**", report)

    def test_missing_plan_flag_and_missing_source_cannot_look_complete(self):
        self.inventory("class Campaign:\n    def battle_0(self):\n        return True\n",
                       [{"method": "battle_0"}])
        with self.assertRaisesRegex(ValueError, "plan_complete"):
            self.audit_incomplete()
        self.write("data/stage.json", json.dumps({"campaign": {"battles": [
            {"method": "not_in_source", "plan_complete": False, "stmt_count": 0}]}}))
        with self.assertRaisesRegex(ValueError, "未定位"):
            self.audit_incomplete()

    def test_only_docstrings_are_removed_from_statement_count(self):
        numeric = ast.parse("def battle_0(self):\n    1\n    unknown()\n").body[0]
        documented = ast.parse('def battle_0(self):\n    "docs"\n    unknown()\n').body[0]
        self.assertEqual(incomplete.source_statement_count(numeric, {}), 2)
        self.assertEqual(incomplete.source_statement_count(documented, {}), 1)

    def scan(self, source):
        path = self.write("method.py", source)
        method = ast.parse(path.read_text(encoding="utf-8")).body[0]
        return mutation.scan((str(path), method.lineno, method.end_lineno))

    def test_append_ammo_set_and_every_write_are_reported(self):
        hits = self.scan("""
            def fixture(self, grid):
                self.picked_flare.append(grid)
                self.ammo_count -= 2
                self.fleet_ammo += 2
                grid.is_enemy = False
                grid.is_enemy = True
                self.map.weight_data = 'changed'
                self.map[grid] = replacement
                self.map.select(is_enemy=True).set(is_enemy=False)
                grid.wipe_out()
            """)
        text = "\n".join(line for _, line in hits)
        for expected in ("picked_flare.append", "ammo_count -= 2", "fleet_ammo += 2",
                         "is_enemy = False", "is_enemy = True", "weight_data",
                         "self.map[grid]", ".set(", "wipe_out"):
            self.assertIn(expected, text)
        self.assertEqual(len(hits), 9)

    def test_comments_comparisons_strings_and_local_increment_are_not_writes(self):
        self.assertEqual(self.scan("""
            def fixture(self, grid):
                # self.ammo_count -= 2
                text = 'self.picked_flare.append(grid)'
                local = 1
                local += 1
                return self.fleet_1_location == grid.location
            """), [])

    def test_nested_definition_is_not_outer_method_execution(self):
        self.assertEqual(self.scan("""
            def fixture(self):
                def later():
                    self.ammo_count = 0
                return later
            """), [])

    def test_tuple_annotation_delete_dynamic_and_multiline_are_seen(self):
        hits = self.scan("""
            def fixture(self):
                self.first, self.second = pair
                self.ammo_count: int = 3
                del self.map['old']
                setattr(self, 'round', 1)
                self.picked_flare.append(
                    grid)
            """)
        self.assertEqual(len(hits), 5)
        self.assertIn("动态属性修改 setattr", [kind for kind, _ in hits])

    def test_discovery_does_not_import_sources_and_retains_overrides(self):
        registry = self.write("registry.cs", '["pick_up_ammo"] = new CampaignPrimitive(')
        self.write("module/map/map.py", """
            raise RuntimeError('must not import')
            class Map:
                def pick_up_ammo(self):
                    self.ammo_count -= 1
            """)
        self.write("campaign/family.py", """
            raise RuntimeError('must not import')
            class Campaign:
                def pick_up_ammo(self):
                    self.ammo_count -= 2
                def battle_0(self):
                    self.picked_flare.append(grid)
            """)
        report = self.root / "mutation.md"
        with patch.multiple(mutation, UPSTREAM=self.root, REGISTRY=registry, REPORT=report), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(len(mutation.upstream_methods()["pick_up_ammo"]), 2)
            self.assertEqual(mutation.main(), 1)
        text = report.read_text(encoding="utf-8")
        self.assertIn("ammo_count -= 1", text)
        self.assertIn("ammo_count -= 2", text)
        self.assertIn("picked_flare.append", text)
        self.assertIn("生产战役仍由上游", text)
        self.assertNotIn("已核对", text)

    def test_hook_report_uses_actual_write_line(self):
        self.write("campaign/a.py", """
            class Campaign:
                def battle_0(self):
                    self.ammo_count -= 1
                    self.picked_flare.append(grid)
            """)
        with patch.object(mutation, "UPSTREAM", self.root):
            hits, count = mutation.hook_mutation_scan()
        self.assertEqual(count, 1)
        self.assertEqual([row[2] for row in hits], [3, 4])


if __name__ == "__main__":
    unittest.main()
