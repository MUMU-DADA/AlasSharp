#!/usr/bin/env python3
"""R5 域级开关自检：默认不改行为、`csharp` 需要二次闸门（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_switch.py

做的事（都用环境变量驱动 `Alas.Server r5-switch --json`，不连设备）：
  1. **默认**：`loop=Shadow`（仍跑上游、只多记一份 C# 决策）、`path` / `primitives` = `Upstream` 且标注"未接生产路径"；
  2. `ALAS_ENGINE_LOOP=csharp` 但**没有闸门** → 必须**拒绝**并退回 `Shadow`，理由里点名缺哪个变量；
  3. 加上 `ALAS_ENGINE_ALLOW_CSHARP=1` → 才允许 `csharp`；
  4. 非法取值（如 `yes`）→ 退回默认并说明合法取值——不猜。

只做开关解析与核对：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import json
import os
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"


def switch(env: dict[str, str]) -> dict:
    full = {key: value for key, value in os.environ.items()
            if not key.startswith("ALAS_ENGINE_")}
    full.update(env)
    completed = subprocess.run([str(SERVER), "r5-switch", "--json"],
                               cwd=ROOT, capture_output=True, text=True, timeout=120,
                               encoding="utf-8", errors="replace", env=full)
    if completed.returncode != 0:
        print(completed.stdout, completed.stderr)
        raise SystemExit(f"r5-switch 退出码 {completed.returncode}")
    text = completed.stdout + completed.stderr
    return json.loads(text[text.find("{"):])


def modes(payload: dict) -> dict[str, str]:
    return {domain["domain"]: domain["mode"] for domain in payload["domains"]}


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    problems: list[str] = []

    default = switch({})
    if modes(default) != {"loop": "Shadow", "path": "Upstream", "primitives": "Upstream"}:
        problems.append(f"默认模式应为 loop=Shadow / path=Upstream / primitives=Upstream，实际 {modes(default)}")
    unwired = [domain["domain"] for domain in default["domains"] if not domain["wired"]]
    if sorted(unwired) != ["path", "primitives"]:
        problems.append(f"未接生产路径的域应为 path/primitives，实际 {unwired}")

    refused = switch({"ALAS_ENGINE_LOOP": "csharp"})
    if modes(refused)["loop"] != "Shadow":
        problems.append(f"缺闸门时 csharp 必须被拒绝并退回 Shadow，实际 {modes(refused)['loop']}")
    if "ALAS_ENGINE_ALLOW_CSHARP" not in json.dumps(refused["domains"], ensure_ascii=False):
        problems.append("拒绝理由里应点名缺少的闸门变量")

    allowed = switch({"ALAS_ENGINE_LOOP": "csharp", "ALAS_ENGINE_ALLOW_CSHARP": "1"})
    if modes(allowed)["loop"] != "CSharp":
        problems.append(f"有闸门时应允许 csharp，实际 {modes(allowed)['loop']}")
    if not allowed["allow_csharp"]:
        problems.append("allow_csharp 应为真")

    invalid = switch({"ALAS_ENGINE_LOOP": "yes"})
    if modes(invalid)["loop"] != "Shadow":
        problems.append(f"非法取值应退回默认 Shadow，实际 {modes(invalid)['loop']}")
    if "取值非法" not in json.dumps(invalid["domains"], ensure_ascii=False):
        problems.append("非法取值应给出说明")

    print(f"[r5-switch] 默认={modes(default)}；缺闸门 csharp → {modes(refused)['loop']}；"
          f"有闸门 → {modes(allowed)['loop']}；非法值 → {modes(invalid)['loop']}")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 域级开关默认不改行为，csharp 需要二次闸门，非法取值退回默认")
    return 0


if __name__ == "__main__":
    sys.exit(main())
