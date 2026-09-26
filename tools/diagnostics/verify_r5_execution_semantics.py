#!/usr/bin/env python3
"""Compare the exported plan evaluator with Python control flow, without devices or exports.

The small programs below exercise language boundaries which corpus coverage cannot prove:
branch fallthrough versus return, operand-valued short circuiting, local arguments, and signals.
All generated inputs remain in an ignored temporary runtime directory.
"""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / "src/Alas.Server/bin/Release/net10.0/Alas.Server.exe"


def lit(value):
    return {"literal": value}


def ret(value):
    return {"kind": "return", "value": value}


def branch(expr, body, other=()):
    return {"kind": "branch", "test": {"expr": expr}, "body": body, "orelse": list(other)}


def call(op, *args, kind="call", **kwargs):
    return {"kind": kind, "op": op, "args": {"positional": list(args), "keyword": kwargs}}


GRID = {"__grid__": [0, 0]}
LOCAL = {"__local__": "boss", "__index__": 0}
SELECT = {**call("map.select", is_boss=True), "kind": "assign", "target": "boss"}
TRUE_RETURN = ret(True)


def cases():
    # Expected return values are evaluated by Python, not by a second plan interpreter.
    programs = [
        ("branch_fallthrough", "if True:\n    pass\nreturn True", [
            branch(lit(True), [{"kind": "log", "text": "branch"}]), TRUE_RETURN], []),
        ("branch_none_return", "if True:\n    return\nreturn True", [
            branch(lit(True), [ret(None)]), TRUE_RETURN], []),
        ("nested_fallthrough", "if True:\n    if True:\n        pass\nreturn True", [
            branch(lit(True), [branch(lit(True), [{"kind": "log", "text": "nested"}])]), TRUE_RETURN], []),
        ("and_keeps_operand", "value = 2 and 7\nreturn value == 7", [
            {"kind": "state_set", "name": "value", "expr": {"and": [lit(2), lit(7)]}},
            branch({"compare": {"left": {"state": "value"}, "op": "==", "right": lit(7)}}, [ret(True)], [ret(False)])], []),
        ("or_keeps_operand", "value = 0 or 9\nreturn value == 9", [
            {"kind": "state_set", "name": "value", "expr": {"or": [lit(0), lit(9)]}},
            branch({"compare": {"left": {"state": "value"}, "op": "==", "right": lit(9)}}, [ret(True)], [ret(False)])], []),
        ("empty_string_false", "return bool('')", [branch(lit(""), [ret(True)], [ret(False)])], []),
        ("bool_numeric_equality", "return True == 1", [
            branch({"compare": {"left": lit(True), "op": "==", "right": lit(1)}}, [ret(True)], [ret(False)])], []),
        ("none_equality", "return None == None", [
            branch({"compare": {"left": lit(None), "op": "==", "right": lit(None)}}, [ret(True)], [ret(False)])], []),
        ("short_circuit_skips_missing", "return bool(False and missing)", [
            branch({"and": [lit(False), {"state": "missing"}]}, [ret(True)], [ret(False)])], []),
        ("grid_bool_expression", "return True", [
            branch({"and": [lit(True), {"grid_attr": {"grid": GRID, "name": "is_boss"}}]},
                [ret(True)], [ret(False)])], []),
        ("ensure_current_returns_false", "return False", [call("fleet_ensure", 1, kind="terminal")], []),
        ("call_local_index", "return True", [SELECT, call("goto", LOCAL), TRUE_RETURN], ["goto(A1)"]),
        ("terminal_local_index", "return None", [SELECT, call("goto", LOCAL, kind="terminal")], ["goto(A1)"]),
        ("assign_local_index", "return True", [SELECT,
            {**call("goto", LOCAL, kind="assign"), "target": "moved"}, TRUE_RETURN], ["goto(A1)"]),
        ("call_scalar_grid", "return True", [SELECT,
            {"kind": "local_set", "target": "boss", "expr": {"local_index": {"name": "boss", "index": 0}}},
            call("goto", {"__local__": "boss"}), TRUE_RETURN], ["goto(A1)"]),
        ("branch_action_once", "if True:\n    pass\nreturn True", [
            branch(lit(True), [call("goto", GRID)]), TRUE_RETURN], ["goto(A1)"]),
        ("empty_membership", "return 0 in ()", [
            {"kind": "branch", "test": {"battle_count_in": []}, "body": [ret(True)], "orelse": [ret(False)]}], []),
        ("initial_none", "value = None\nreturn bool(value)", [
            branch({"state": "initial_none"}, [ret(True)], [ret(False)])], []),
        ("initial_bool", "value = True\nreturn bool(value)", [
            branch({"state": "initial_bool"}, [ret(True)], [ret(False)])], []),
        ("default_override", "return True", [call("battle_default", kind="terminal")], ["goto(A1)"]),
        ("goto_is_not_true", "def goto():\n    pass\nif goto():\n    return True\nreturn False", [
            call("goto", GRID, kind="conditional"), ret(False)], ["goto(A1)"]),
        ("assign_goto_none", "def goto():\n    pass\nvalue = goto()\nreturn value == None", [
            {**call("goto", GRID, kind="assign"), "target": "value"},
            branch({"compare": {"left": {"local": "value"}, "op": "==", "right": lit(None)}},
                [ret(True)], [ret(False)])], ["goto(A1)"]),
        ("switch_none", "def switch_to():\n    pass\nreturn switch_to()", [
            call("switch_to", kind="terminal")], []),
        ("condition_override", "return True", [
            branch({"call": {"op": "battle_default"}}, [ret(True)], [ret(False)])], ["goto(A1)"]),
    ]
    for name, source, steps, actions in programs:
        namespace = {}
        exec("def reference():\n" + "\n".join("    " + line for line in source.splitlines()), namespace)
        yield name, steps, namespace["reference"](), actions


def main():
    if not SERVER.is_file():
        raise SystemExit("Build Alas.Server before running semantic regression checks")
    runtime = ROOT / ".runtime"
    runtime.mkdir(exist_ok=True)
    failures = []
    checked = 0
    with tempfile.TemporaryDirectory(prefix="execution-semantics-", dir=runtime) as work:
        work = Path(work)
        campaign_dir = work / "campaign" / "semantics"
        campaign_dir.mkdir(parents=True)

        def run(name, steps, command="r5-exec", *, config=None, extra_hooks=(), grids=None):
            plan = {"campaign": {"plan_complete": True,
                "initial_state": {"initial_none": None, "initial_bool": True},
                "battles": [{"method": "battle_0", "plan_complete": True, "steps": steps}, *extra_hooks]}}
            (campaign_dir / "case.json").write_text(json.dumps(plan), encoding="utf-8")
            fixture = work / "fixture.json"
            fixture.write_text(json.dumps({"cases": [{"name": name, "chapter": "semantics", "level": "case",
                "hook": "battle_0", "config": config or {}, "grids": grids if grids is not None else
                [{"location": "A1", "is_boss": True}]}]}), encoding="utf-8")
            result = subprocess.run([str(SERVER), command, "--data", str(work), "--fixture", str(fixture)],
                cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
            if result.returncode:
                # Never print stack traces containing private checkout paths.
                failures.append(f"{name}: command exited {result.returncode}")
                return None
            return json.loads(result.stdout)["cases"][0]

        for name, steps, expected, actions in cases():
            checked += 1
            value = run(name, steps, extra_hooks=[{"method": "battle_default", "plan_complete": True,
                "steps": [call("goto", GRID), TRUE_RETURN]}])
            if value is None:
                continue
            if not value["completed"] or value["return"] != expected or value["actions"] != actions:
                failures.append(f"{name}: completed={value['completed']}, return={value['return']!r}, "
                    f"actions={value['actions']!r}; Python return={expected!r}, actions={actions!r}")

        for nested in (False, True):
            checked += 1
            steps = [{"kind": "raise", "signal": "CampaignEnd"}]
            if nested:
                steps = [branch(lit(True), steps)]
            steps += [call("goto", GRID), TRUE_RETURN]
            value = run(f"campaign_end_{nested}", steps, "r5-loop")
            if value and (value["outcome"] != "Ended" or value["actions"]):
                failures.append(f"campaign_end_{nested}: signal did not unwind all subsequent actions")

        for handle_error in (False, True):
            checked += 1
            value = run(f"enemy_moved_{handle_error}", [{"kind": "raise", "signal": "MapEnemyMoved"}],
                "r5-loop", config={"error_handle_error": handle_error})
            expected = "Ended" if handle_error else "ScriptError"
            if value and (value["outcome"] != expected
                    or len([line for line in value["logs"] if "重试" in line]) != 10):
                failures.append(f"enemy_moved_{handle_error}: expected 10 retries then {expected}, got {value['outcome']}")

        enemies = [
            {"location": "A1", "is_enemy": True, "enemy_scale": 1, "enemy_genre": "Light", "weight": 30, "cost": 10},
            {"location": "B1", "is_enemy": True, "enemy_scale": 2, "enemy_genre": "Main", "weight": 10, "cost": 30}]
        selection = [
            ("scale_list", {"scale": [1, 2]}, "A1"),
            ("scale_tuple", {"scale": {"__tuple__": [1, 2]}}, "B1"),
            ("genre_list", {"genre": ["light", "main"]}, "A1"),
            ("genre_tuple", {"genre": {"__tuple__": ["light", "main"]}}, "B1"),
            ("sort_tuple", {"sort": {"__tuple__": ["cost"]}}, "A1"),
            ("empty_sort_tuple", {"sort": {"__tuple__": []}}, "A1"),
            ("nearby", {"nearby": True}, "A1"),
            ("ignore", {"ignore": {"__grids__": [[1, 0]]}}, "A1"),
        ]
        for name, kwargs, target in selection:
            checked += 1
            value = run(name, [call("clear_enemy", kind="terminal", **kwargs)], grids=enemies)
            expected_action = f"clear_chosen_enemy({target}, expected=)"
            if value and (not value["completed"] or value["actions"] != [expected_action]):
                failures.append(f"{name}: expected {expected_action}, got {value['actions']}")

        checked += 1
        value = run("accessibility_false", [call("clear_enemy", kind="terminal", is_accessible=False)],
            grids=[{**enemies[0], "cost": 9999}])
        if value and (not value["completed"] or len(value["actions"]) != 1):
            failures.append("accessibility_false: explicit disabled filter was ignored")

        checked += 1
        value = run("nested_signal", [call("battle_1")], "r5-loop", config={"error_handle_error": False},
            extra_hooks=[{"method": "battle_1", "plan_complete": True,
                "steps": [{"kind": "raise", "signal": "MapEnemyMoved"}]}])
        if value and value["outcome"] != "ScriptError":
            failures.append("nested_signal: exception type was lost across hook calls")

        checked += 1
        value = run("signal_name_is_not_signal", [call("MapEnemyMoved_unimplemented")], "r5-loop")
        if value and (value["outcome"] != "Blocked" or any("重试" in line for line in value["logs"])):
            failures.append("signal_name_is_not_signal: a diagnostic substring triggered retries")

        checked += 1
        value = run("local_ignore_collection", [
            {"kind": "local_set", "target": "ignored", "expr": {"grids": [GRID]}},
            call("clear_all_mystery", ignore={"__local_grids__": "ignored"}), ret(True)],
            grids=[{"location": "A1", "is_mystery": True, "cost": 0},
                   {"location": "B1", "is_mystery": True, "cost": 1}])
        # This recorder has no post-action frames: B1 remains a target. Ignoring
        # A1 must hold on every iteration, and the incomplete loop must not return.
        if value and (value["completed"] or value["return"] is not None
                or "clear_all_mystery" not in (value["blocked"] or "")
                or value["actions"] != ["clear_chosen_mystery(B1)"] * 100):
            failures.append("local_ignore_collection: ignore/budget refusal was lost")

        checked += 1
        value = run("tuple_refocus", [call("handle_boss_appear_refocus", {"__tuple__": [1, -1]})])
        if value and (not value["completed"] or value["actions"] !=
                ["update_map()", "ensure_edge_insight()", "focus_to(<未记录>)"]):
            failures.append("tuple_refocus: tuple preset was not decoded")

        for name, steps, reason in [
            ("missing_initial_state", [branch({"state": "undefined"}, [ret(True)])], "没有初值"),
            ("unsupported_scalar_return", [ret(2)], "标量返回尚未迁移"),
        ]:
            checked += 1
            value = run(name, steps)
            if value and (value["completed"] or reason not in (value["blocked"] or "") or value["actions"]):
                failures.append(f"{name}: expected explicit rejection without actions")

        # SelectedGrids.select uses exact Python type and value equality, including bool versus int.
        select_grids = [*enemies, {"location": "C1", "is_enemy": False, "enemy_scale": 3,
            "enemy_genre": None, "cost": 9999}]
        for kwargs in ({"enemy_scale": 3}, {"enemy_genre": "Main"}, {"enemy_genre": None},
                {"is_enemy": True}, {"is_enemy": False}, {"is_enemy": 1}, {"is_enemy": "x"},
                {"enemy_scale": True}, {"cost": 9999}, {"enemy_genre": "main"}):
            checked += 1
            name = f"map_select_{kwargs!r}"
            selected = [grid for grid in select_grids if all(type(grid[key]) is type(expected)
                and grid[key] == expected for key, expected in kwargs.items())]
            value = run(name, [{**call("map.select", **kwargs), "kind": "assign", "target": "picked"},
                branch({"local": "picked"}, [call("goto", {"__local__": "picked", "__index__": 0})]),
                ret(True)], grids=select_grids)
            expected_actions = [f"goto({selected[0]['location']})"] if selected else []
            if value and (not value["completed"] or value["actions"] != expected_actions):
                failures.append(f"{name}: expected {expected_actions}, got {value['actions']}")

        for kwargs in ({"enemy_scale": [3]}, {"unknown_attribute": True}):
            checked += 1
            value = run(f"map_select_rejected_{kwargs!r}", [
                {**call("map.select", **kwargs), "kind": "assign", "target": "picked"}, ret(True)])
            if value and (value["completed"] or value["actions"]):
                failures.append(f"map.select silently accepted unsupported input {kwargs!r}")

        invocation_cases = [
            ("composite_entries", [call("battle_default", kind="terminal")], {},
                {"battle_default", "clear_enemy"}),
            ("override_entry", [call("battle_default", kind="terminal")], {"extra_hooks": [
                {"method": "battle_default", "plan_complete": True, "steps": [call("goto", GRID), ret(True)]}]},
                {"goto"}),
            ("short_circuit_entry", [branch({"and": [lit(False), {"call": {"op": "clear_enemy"}}]},
                [ret(True)]), ret(False)], {}, set()),
            ("argument_decode_entry", [call("goto", {"__grid__": [10, 10]})], {}, set()),
            ("loop_variant_entries", [], {"command": "r5-loop", "config": {
                "poor_map_data": True, "error_handle_error": False}, "grids": []},
                {"battle_with_poor_map_data", "fleet_2_break_siren_caught", "clear_all_mystery",
                 "clear_siren", "clear_enemy"}),
        ]
        for name, steps, options, expected in invocation_cases:
            checked += 1
            value = run(name, steps, **options)
            if value and ("invoked_ops" not in value or set(value["invoked_ops"]) != expected):
                failures.append(f"{name}: expected actual implementation entries {sorted(expected)}, "
                    f"got {value.get('invoked_ops')}")

    print(f"[r5 execution semantics] {checked} checks; {len(failures)} failures (offline)")
    for failure in failures:
        print(f"  FAIL {failure}")
    return bool(failures)


if __name__ == "__main__":
    raise SystemExit(main())
