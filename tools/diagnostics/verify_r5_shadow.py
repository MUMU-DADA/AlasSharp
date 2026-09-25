#!/usr/bin/env python3
"""R5 影子模式对拍：C# 引擎"只算不执行"的决策 vs 上游实际运行日志（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_shadow.py

做的事：
  1. 用两份**确定性夹具日志**跑产品命令
     `Alas.Server r5-shadow --chapter <章> --level <关> --log <日志> --json`：
     · `fixtures/shadow-consistent.log`：上游每轮选的钩子与 C# 影子一致 → 期望 0 不一致、退出码 0；
     · `fixtures/shadow-drift.log`：第 1 轮漂移、第 2 轮走 `clear_all` 变体（未迁移，跳过）、
       第 3 轮没打成 → 期望恰好 1 个不一致 + 1 个跳过、退出码 1；
  2. 顺带核对 `No combat executed` 与 `Battle function exhausted` 这两个信号被解析出来。

夹具日志是按上游 `logger.hr(f'{FUNCTION_NAME_BASE}{battle_count}', level=2)` 的真实输出格式写的
（规则行 + 同名 INFO 行 + `Using function: …`），不是编造的字段。

只做决策比对：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tools" / "diagnostics" / "fixtures"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"

CASES = [
    {
        "name": "一致：上游 4 轮全部与影子选择相同",
        "log": "shadow-consistent.log",
        "chapter": "campaign_main",
        "level": "campaign_1_4",
        "expect": {"matched": 4, "mismatched": 0, "skipped": 0, "campaign_end": True,
                   "verdicts": ["一致", "一致", "一致", "一致"]},
    },
    {
        "name": "按上游模块名解析关卡（--chapter-module）",
        "log": "shadow-consistent.log",
        "chapter_module": "campaign.campaign_main.campaign_1_4",
        "expect": {"matched": 4, "mismatched": 0, "skipped": 0, "verdicts": ["一致", "一致", "一致", "一致"]},
    },
    {
        "name": "漂移：第 1 轮不一致、第 2 轮变体跳过、第 3 轮没打成",
        "log": "shadow-drift.log",
        "chapter": "campaign_main",
        "level": "campaign_1_4",
        "expect": {"matched": 0, "mismatched": 2, "skipped": 1, "exhausted": True,
                   "verdicts": ["不一致", "不一致", "跳过"]},
    },
    {
        "name": "变体：声明 clear_all 时，日志里的 clear_all 全部一致",
        "log": "shadow-variant.log",
        "chapter": "campaign_main",
        "level": "campaign_2_1",
        "variant": "clear_all",
        "expect": {"matched": 2, "mismatched": 0, "skipped": 0, "campaign_end": True,
                   "variant": "clear_all", "verdicts": ["一致", "一致"]},
    },
]


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    problems: list[str] = []
    for case in CASES:
        log = FIXTURES / case["log"]
        if not log.is_file():
            problems.append(f"{case['name']}: 缺少夹具日志 {log.name}")
            continue
        command = [str(SERVER), "r5-shadow"]
        if case.get("chapter_module"):
            command += ["--chapter-module", case["chapter_module"]]
        else:
            command += ["--chapter", case["chapter"], "--level", case["level"]]
        command += ["--log", str(log), "--json"]
        if case.get("variant"):
            command += ["--variant", case["variant"]]
        completed = subprocess.run(
            command,
            cwd=ROOT, capture_output=True, text=True, timeout=180, encoding="utf-8", errors="replace")
        if completed.returncode not in (0, 1):
            print(completed.stdout, completed.stderr)
            problems.append(f"{case['name']}: 命令退出码 {completed.returncode}")
            continue
        payload = json.loads(completed.stdout)
        expect = case["expect"]
        for key in ("matched", "mismatched", "skipped", "campaign_end", "exhausted", "variant"):
            if key in expect and payload.get(key) != expect[key]:
                problems.append(f"{case['name']}: {key}={payload.get(key)}，期望 {expect[key]}")
        verdicts = [row["verdict"] for row in payload["rows"]]
        if "verdicts" in expect and verdicts != expect["verdicts"]:
            problems.append(f"{case['name']}: 判定序列 {verdicts}，期望 {expect['verdicts']}")
        expected_code = 0 if expect.get("mismatched", 0) == 0 else 1
        if completed.returncode != expected_code:
            problems.append(f"{case['name']}: 退出码 {completed.returncode}，期望 {expected_code}"
                            "（有漂移就该非零退出）")

    print(f"[r5-shadow] 用例 {len(CASES)} 个（一致 / 漂移各一）")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 影子比对能识别一致与漂移（含变体跳过与失败轮次）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
