#!/usr/bin/env python3
"""R5 不完整钩子普查：把 `plan_complete=false` 的钩子按**成因**分类，并给出先决条件。

用法：
    python tools/diagnostics/r5_incomplete_hooks.py

背景：导出器只归一"能完整表示"的方法体，表示不了就 `plan_complete=false` 且 `steps` 作废
（保真红线：绝不给一份少几步的"完整"计划）。引擎侧已经**拒绝执行**这些钩子并报原因
（`CampaignHookRunner` 的 tier C 守卫，`r5-exec` 有夹具用例断言）。本脚本回答两个问题：

  1. **还有多少、都是什么形态**：用 `ast` 看这些钩子体里的 `if`，按"条件形态 / 语句体形态"分类；
  2. **差什么才能表达**：把出现最多的形态列成先决条件清单（例如"局部变量 + `if <局部>` + 分支体"）。

**棘轮**：`BASELINE` 记下当前的"有真实语句的不完整钩子"数量；只允许下降，涨了就失败——
避免以后悄悄多出来一批不可执行的钩子。

只读：不导入设备侧、不执行游戏动作。
"""
from __future__ import annotations

import ast
import collections
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
UPSTREAM = ROOT / ".runtime" / "engine"
DATA = ROOT / "data" / "campaign"
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-incomplete-hooks.md"

# 棘轮基线：上次普查的"不完整且上游有 ≥2 条语句"的钩子数（只许下降）
BASELINE = 118      # `battle_*` 里"不完整且上游有 ≥2 条语句"的钩子数（实测；计划语言扩展后从 163 降下来）


def is_self_call(node) -> bool:
    return (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
            and isinstance(node.func.value, ast.Name) and node.func.value.id == "self")


def stmt_kind(node) -> str:
    return {ast.Return: "return", ast.If: "if", ast.Assign: "assign", ast.For: "for",
            ast.Expr: "expr", ast.Raise: "raise", ast.Break: "break"}.get(type(node), type(node).__name__)


def cond_kind(test) -> str:
    if is_self_call(test):
        return "self_call"
    if isinstance(test, ast.UnaryOp) and isinstance(test.op, ast.Not) and is_self_call(test.operand):
        return "not_self_call"
    if isinstance(test, ast.Name):
        return "local_name"
    if isinstance(test, ast.Compare):
        return "compare"
    return "other"


def main() -> int:
    hooks = 0
    incomplete_real = 0
    variants = 0
    shapes: collections.Counter = collections.Counter()
    examples: list[tuple[str, str, str, str]] = []
    plans_total = 0
    for path in sorted((UPSTREAM / "campaign").rglob("*.py")):
        level = str(path.relative_to(UPSTREAM / "campaign")).replace("\\", "/")[:-3]
        payload_path = DATA / f"{level}.json"
        if not payload_path.is_file():
            continue
        payload = json.loads(payload_path.read_text(encoding="utf-8"))
        battles = (payload.get("campaign") or {}).get("battles") or []
        incomplete = {b["method"] for b in battles if not b.get("plan_complete", True)}
        plans_total += len(battles)
        tree = ast.parse(path.read_text(encoding="utf-8", errors="replace"))
        for node in ast.walk(tree):
            if not isinstance(node, ast.ClassDef) or node.name != "Campaign":
                continue
            for item in node.body:
                if not isinstance(item, ast.FunctionDef):
                    continue
                body = list(item.body)
                if body and isinstance(body[0], ast.Expr) and isinstance(body[0].value, ast.Constant):
                    body = body[1:]
                if item.name not in incomplete:
                    continue
                if len(body) <= 1:
                    continue        # 只有 `pass`/单条 return 的"不完整"不算实质缺口
                incomplete_real += 1
                if item.name.startswith("battle_"):
                    hooks += 1
                else:
                    variants += 1
                for sub in ast.walk(item):
                    if isinstance(sub, ast.If):
                        body_kinds = "+".join(sorted({stmt_kind(s) for s in sub.body})) or "empty"
                        shapes[(cond_kind(sub.test), body_kinds)] += 1
                        if len(examples) < 10:
                            examples.append((level, item.name, ast.unparse(sub.test)[:48], body_kinds))

    lines = ["# R5 不完整钩子普查（`plan_complete=false` 的成因与先决条件）", "",
             "> 本报告由 `tools/diagnostics/r5_incomplete_hooks.py` 重建，不手写。",
             "> 这些钩子**有真实语句**但导出器表示不了；引擎侧会**拒绝执行并报原因**（tier C 守卫），",
             "> 所以是「少做」而不是「做错」。本报告只说清还差什么。", "",
             f"- 导出里的钩子条目：**{plans_total}**",
             f"- `plan_complete=false` 且上游**有 ≥2 条语句**的：**{incomplete_real}**"
             f"（其中 `battle_*` **{hooks}**、变体/其它 **{variants}**）",
             f"- 棘轮基线：**{BASELINE}**（只允许下降）", "",
             "## `if` 的形态分布（条件 / 语句体）", "",
             "口径：这些是**不完整钩子体内**所有的 `if`，包含那些**本身支持**的形态",
             "（`self_call` + 纯 `return True`）——不完整的成因在别的语句上。要看的行是",
             "`local_name`、`not_self_call/if`、`other/*` 这几类。", "",
             "| 条件形态 | 语句体形态 | 次数 |", "| --- | --- | --- |"]
    lines += [f"| {cond} | {body} | {count} |" for (cond, body), count in shapes.most_common(12)]
    lines += ["", "## 例子", ""]
    lines += [f"- `{level}` {method}：`if {test}` → 体内有 {body}" for level, method, test, body in examples]
    lines += ["", "## 先决条件（按出现频次）", "",
              "1. **局部变量 + `if <局部变量>:` + 分支体**：`boss = self.map.select(is_boss=True)` 这类「观察」，",
              "   以及 `branch` 步骤（条件为局部变量或一次原语调用，体内是步骤序列）；",
              "2. **局部变量的实参引用**：`check_accessibility(boss[0], fleet='boss')` 里的 `boss[0]`；",
              "3. 其它形态（`compare` 条件、`for` 循环、`raise` 体）另计，需要单独设计，不要硬塞进上面的结构。", ""]
    lines.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}：不完整且有真实语句 {incomplete_real} 个"
          f"（battle_* {hooks} / 其它 {variants}）/ 基线 {BASELINE}")
    if hooks > BASELINE:
        print(f"FAIL: battle_* 里比基线多了 {hooks - BASELINE} 个（棘轮只允许下降）")
        return 1
    print("PASS: 不完整钩子数量没有超过基线（当前成因与先决条件见报告）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
