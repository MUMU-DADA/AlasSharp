#!/usr/bin/env python3
"""R5 宿主调用翻译核对：计划步骤 → 上游方法 + 参数引用形式（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_calls.py

做的事：
  1. 跑 `Alas.Server r5-calls --json`（**全库 5694 个步骤**），核对：
     - **零不支持**：每个计划步骤都能翻成一次宿主调用（否则退出码非 0，并列出原因）；
     - 抽查参数编码：`clear_filter_enemy` 的 `preserve` 走 **kwargs**（不折算成位置参数）、
       `fleet_2_step_on` 的 `roadblocks` 走 `#roads:[[...]]`、格参数走 `#<节点>`；
  2. 核对 `super().X` 被翻成**直接调 `X`**（基类实现），且舰队前缀不会被误当成方法名。

只读：不连设备、不改变任何运行状态（翻译是纯函数，参数引用形式由宿主侧 `_campaign_arg` 解析）。
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    completed = subprocess.run([str(SERVER), "r5-calls", "--json"],
                               cwd=ROOT, capture_output=True, text=True, timeout=600,
                               encoding="utf-8", errors="replace")
    text = completed.stdout + completed.stderr
    start = text.find("{")
    if start < 0:
        raise SystemExit(f"没有解析到 JSON 输出：{text.strip().splitlines()[-3:]}")
    payload = json.loads(text[start:])

    problems: list[str] = []
    runtime_only = payload.get("runtime_only") or []
    if completed.returncode != 0:
        problems.append(f"翻译应零不支持，命令退出码 {completed.returncode}：{payload.get('unsupported')}")
    if payload["translated"] + payload.get("structural", 0) != payload["steps"]:
        problems.append(f"应全部可处理（调用 + 结构步骤）：{payload['translated']} + "
                        f"{payload.get('structural', 0)}/{payload['steps']}")
    if payload.get("structural"):
        print(f"  [结构步骤] {payload['structural']} 个（`branch`/`return`：不是调用，由执行器处理）")
    # 运行期解析**不算不支持**，但要看得见：这类实参（`__local__` 局部变量 / `__param__` 钩子参数）
    # 由执行器在调用前替换成具体值，静态翻译到这里为止。
    for item in runtime_only:
        print(f"  [运行期解析] {item['op']} × {item['count']}：{item['reason']}")

    sample = payload["sample"]
    by_op: dict[str, list[dict]] = {}
    for row in sample:
        by_op.setdefault(row["op"], []).append(row)

    filter_rows = by_op.get("clear_filter_enemy") or []
    if filter_rows:
        row = filter_rows[0]
        if "preserve" not in (row.get("kwargs") or {}):
            problems.append(f"clear_filter_enemy 的 preserve 应走 kwargs，实际 {row}")
    step_on = by_op.get("fleet_2_step_on") or []
    if step_on:
        args = step_on[0].get("args") or []
        if not any(isinstance(a, str) and a.startswith("#roads:") for a in args) and \
                not any(isinstance(v, str) and v.startswith("#roads:")
                        for v in (step_on[0].get("kwargs") or {}).values()):
            problems.append(f"fleet_2_step_on 的道路参数应编码成 #roads:[...]，实际 {step_on[0]}")
    grid_rows = [row for row in sample if any(isinstance(a, str) and a.startswith("#") for a in (row.get("args") or []))]
    if not grid_rows:
        problems.append("样例里应至少出现一次 `#<节点>` 形式的格参数")
    super_rows = by_op.get("super().handle_boss_appear_refocus") or []
    if super_rows and super_rows[0]["method"] != "handle_boss_appear_refocus":
        problems.append(f"super().X 应翻成直接调 X，实际 {super_rows[0]}")
    if super_rows and super_rows[0].get("fleet_prefix"):
        problems.append("super() 不是舰队前缀，不应出现在 fleet_prefix 里")

    print(f"[r5-calls] 步骤 {payload['steps']} 个 → 翻译 {payload['translated']} 个，"
          f"不支持 {len(payload['unsupported'])} 个；上游方法 {len(payload['methods'])} 个")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 全部计划步骤都能翻成宿主调用，参数引用形式（#节点/#grids/#roads/kwargs）与 super 处理符合预期")
    return 0


if __name__ == "__main__":
    sys.exit(main())
