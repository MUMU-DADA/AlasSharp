#!/usr/bin/env python3
"""审计导出计划中不完整的战役钩子。

全部 plan_complete=false 条目参与事实计数；源体语句数大于一的子集只用于
保留历史棘轮，不能用这个子集代替全部缺口。只解析文件，不执行上游模块。
"""
from __future__ import annotations

import ast
import collections
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
UPSTREAM = Path(os.environ.get("ALAS_REPO") or ROOT / ".runtime" / "engine").resolve()
DATA = Path(os.environ.get("ALAS_DATA") or ROOT / "data") / "campaign"
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-incomplete-hooks.md"

# 保留历史棘轮：只衡量源体语句数 >1 的不完整 battle_*，不随新快照改阈值。
BASELINE = 5


def is_self_call(node: ast.AST) -> bool:
    return (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
            and isinstance(node.func.value, ast.Name) and node.func.value.id == "self")


def stmt_kind(node: ast.AST) -> str:
    return {
        ast.Return: "return", ast.If: "if", ast.Assign: "assign", ast.AnnAssign: "assign",
        ast.AugAssign: "augassign", ast.For: "for", ast.While: "while", ast.Expr: "expr",
        ast.Raise: "raise", ast.Break: "break", ast.Continue: "continue",
    }.get(type(node), type(node).__name__)


def cond_kind(test: ast.AST) -> str:
    if is_self_call(test):
        return "self_call"
    if isinstance(test, ast.UnaryOp) and isinstance(test.op, ast.Not) and is_self_call(test.operand):
        return "not_self_call"
    if isinstance(test, ast.Name):
        return "local_name"
    if isinstance(test, ast.Compare):
        return "compare"
    if isinstance(test, ast.BoolOp):
        return "boolean_expression"
    return "other"


def source_inventory() -> tuple[dict[str, dict[str, ast.FunctionDef]], set[str]]:
    methods: dict[str, dict[str, ast.FunctionDef]] = {}
    campaign_sources: set[str] = set()
    source_paths = sorted((UPSTREAM / "campaign").rglob("*.py"))
    if not source_paths:
        raise FileNotFoundError("缺少上游 campaign 源文件，不能将空扫描当作通过")
    for path in source_paths:
        level = path.relative_to(UPSTREAM / "campaign").with_suffix("").as_posix()
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=level)
        classes = [node for node in ast.walk(tree)
                   if isinstance(node, ast.ClassDef) and node.name == "Campaign"]
        if not classes:
            continue
        campaign_sources.add(level)
        methods[level] = {
            item.name: item for cls in classes for item in cls.body
            if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef))
        }
    return methods, campaign_sources


def load_incomplete(methods: dict[str, dict[str, ast.FunctionDef]],
                    campaign_sources: set[str]) -> tuple[list[dict[str, object]], int]:
    exports = sorted(DATA.rglob("*.json"))
    if not exports:
        raise FileNotFoundError("缺少章节导出，不能将空扫描当作通过")
    records: list[dict[str, object]] = []
    plans_total = 0
    seen_sources: set[str] = set()
    for path in exports:
        level = path.relative_to(DATA).with_suffix("").as_posix()
        payload = json.loads(path.read_text(encoding="utf-8"))
        for battle in (payload.get("campaign") or {}).get("battles") or []:
            plans_total += 1
            if type(battle.get("plan_complete")) is not bool or not battle.get("method"):
                raise ValueError(f"导出钩子缺少 method/plan_complete：campaign/{level}.json")
            if not battle["plan_complete"]:
                method = str(battle.get("method") or "<missing method>")
                records.append({"level": level, "method": method, "battle": battle,
                                "source": methods.get(level, {}).get(method)})
        if level in campaign_sources:
            seen_sources.add(level)
    missing = sorted(campaign_sources - seen_sources)
    if missing:
        raise FileNotFoundError(f"缺少章节导出 campaign/{missing[0]}.json")
    if not plans_total:
        raise ValueError("导出中没有钩子条目，不能完成不完整钩子审计")
    return records, plans_total


def source_statement_count(node: ast.AST | None, battle: dict[str, object]) -> int:
    if node is not None:
        body = list(getattr(node, "body", []))
        if (body and isinstance(body[0], ast.Expr) and isinstance(body[0].value, ast.Constant)
                and isinstance(body[0].value.value, str)):
            body = body[1:]
        return len(body)
    raise ValueError("未定位不完整钩子源体，不能用导出的 stmt_count 猜测棘轮")


def cell(value: object) -> str:
    return str(value).replace("|", "\\|").replace("\n", " ").replace("\r", " ")


def main() -> int:
    methods, campaign_sources = source_inventory()
    records, plans_total = load_incomplete(methods, campaign_sources)
    missing_source = [row for row in records if row["source"] is None]
    if missing_source:
        names = ", ".join(f"{row['level']}:{row['method']}" for row in missing_source)
        raise ValueError(f"未定位不完整钩子源体，不能完成棘轮：{names}")
    battle_records = [row for row in records if str(row["method"]).startswith("battle_")]
    substantive = [row for row in records
                   if source_statement_count(row["source"], row["battle"]) > 1]
    substantive_battle = [row for row in substantive
                          if str(row["method"]).startswith("battle_")]
    shapes: collections.Counter[tuple[str, str]] = collections.Counter()
    reasons: collections.Counter[str] = collections.Counter()
    examples: list[tuple[str, str, str, str]] = []
    for row in records:
        battle = row["battle"]
        for reason in battle.get("unparsed") or []:
            reasons[str(reason).split("@", 1)[0]] += 1
        node = row["source"]
        if node is None:
            continue
        for sub in ast.walk(node):
            if isinstance(sub, ast.If):
                body_kinds = "+".join(sorted({stmt_kind(item) for item in sub.body})) or "empty"
                shapes[(cond_kind(sub.test), body_kinds)] += 1
                if len(examples) < 12:
                    examples.append((str(row["level"]), str(row["method"]),
                                     ast.unparse(sub.test)[:64], body_kinds))

    lines = [
        "# R5 不完整钩子普查（事实计数与棘轮）", "",
        "> 本报告由 `tools/diagnostics/r5_incomplete_hooks.py` 重建，不手写。",
        "> 计数首先来自导出 JSON 的 `plan_complete=false`；源代码形态只用于解释和历史棘轮。",
        "> 本审计不宣称生产战役可由静态计划替代；生产路径仍由上游 `Campaign.run()` 负责。", "",
        f"- 导出钩子条目总数：**{plans_total}**",
        f"- `plan_complete=false` 条目：**{len(records)}**",
        f"- 其中 `battle_*`：**{len(battle_records)}**；其它钩子/方法：**{len(records) - len(battle_records)}**",
        f"- 源体语句数 > 1（解释性统计）：**{len(substantive)}**；其中 `battle_*`：**{len(substantive_battle)}**",
        f"- 历史棘轮基线（`battle_*` 源体语句数 > 1）：**{BASELINE}**；当前值高于基线即失败，不能调高或调低掩盖变化。", "",
        "## 不完整原因（仅来自导出 `unparsed`）", "",
        "| 原因前缀 | 次数 |", "| --- | ---: |",
    ]
    lines += [f"| `{cell(reason)}` | {count} |" for reason, count in reasons.most_common()]
    lines += ["", "## 源体条件形态（解释性统计）", "",
              "| 条件形态 | 语句体形态 | 次数 |", "| --- | --- | ---: |"]
    lines += [f"| {condition} | {body} | {count} |"
              for (condition, body), count in shapes.most_common()]
    lines += ["", "## 例子", ""]
    lines += [f"- `{level}` `{method}`：`if {test}` → {body}"
              for level, method, test, body in examples]
    lines += ["", "## 全部不完整条目", "",
              "| 模块 | 方法 | 源体语句数（不含 docstring） | 导出原因 |",
              "| --- | --- | ---: | --- |"]
    lines += [f"| `{cell(row['level'])}` | `{cell(row['method'])}` | "
              f"{source_statement_count(row['source'], row['battle'])} | "
              f"{cell('; '.join(row['battle'].get('unparsed') or ['未记录原因']))} |"
              for row in records]
    lines += ["", "## 口径边界", "",
              "- 全部条目都保留在事实计数中；单语句、继承方法和非 `battle_*` 条目不会被静默删掉。",
              "- `battle_*` 棘轮只衡量历史上用于计划语言回归的源体语句数；它不等价于全部缺口，也不产生“0 gap/0 blocked”的结论。",
              "- 本报告不按地图名称给出迁移价值判断。每个未解析原因仍需结合上游调用链、设备状态和真实证据处理。", ""]
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}：plan_complete=false {len(records)}（battle_* {len(battle_records)}），"
          f"源体语句数 >1 {len(substantive)}（battle_* {len(substantive_battle)}），棘轮 {BASELINE}")
    if len(substantive_battle) > BASELINE:
        print(f"FAIL: battle_* 棘轮超出基线 {len(substantive_battle) - BASELINE} 条；保留基线，不降低标准")
        return 1
    print("PASS: 不完整钩子棘轮未超基线")
    return 0


if __name__ == "__main__":
    sys.exit(main())
