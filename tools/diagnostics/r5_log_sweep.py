#!/usr/bin/env python3
"""R5 历史日志扫描：把真实运行日志按关卡切分，逐段做决策层对照（离线，无设备）。

用法：
    python tools/diagnostics/r5_log_sweep.py

做的事：
  1. 扫 `data/*.log`，按**关卡头**（上游 `logger.hr(self.ENTRANCE, level=2)` 打的 `----- 1-4 -----`）
     把日志切成一段一段（一次批量运行会包含多关）；
  2. 每段写进忽略目录 `.runtime/r5-probe/log-segments/`，跑
     `r5-shadow --chapter campaign_main --level campaign_<a>_<b> --log <段> [--variant …] --json`；
  3. 变体**不由日志自身推断**（否则是循环论证）：由 <DECLARED_VARIANT> 按"这次运行用了什么配置"显式声明
     （依据是运行命令/文件名，不是日志里的 `Using function:`）；没声明的按默认变体。
  4. 报告写 `docs/archive/reports/r5-log-sweep.md`（脚本重建，不手写）。

口径：只证明这些**历史运行**的决策层一致，不代表其他关卡或其他配置。
只读：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import json
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-log-sweep.md"
SEGMENTS = ROOT / ".runtime" / "r5-probe" / "log-segments"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"

# 这次运行用的配置（**人声明**，不是从日志内容推断）：文件名 → 变体。
DECLARED_VARIANT = {
    "s3_native_2_1_clearall.log": "clear_all",
}
CHAPTER = "campaign_main"
ENTRANCE = re.compile(r"^-{5,}\s*(\d+)-(\d+)\s*-{5,}\s*$")


def segments(text: str) -> list[tuple[str, str]]:
    """按关卡头切分：返回 [(关卡, 该段文本)]。没有关卡头就整段当作"关卡未知"。"""
    current_level: str | None = None
    buffer: list[str] = []
    result: list[tuple[str, str]] = []
    for line in text.replace("\r", "").split("\n"):
        match = ENTRANCE.match(line.strip())
        if match:
            if current_level is not None and buffer:
                result.append((current_level, "\n".join(buffer)))
            current_level = f"campaign_{match.group(1)}_{match.group(2)}"
            buffer = [line]
            continue
        if current_level is not None:
            buffer.append(line)
    if current_level is not None and buffer:
        result.append((current_level, "\n".join(buffer)))
    return result


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    SEGMENTS.mkdir(parents=True, exist_ok=True)
    rows: list[dict] = []
    skipped_logs: list[tuple[str, str]] = []
    for log in sorted((ROOT / "data").glob("*.log")):
        text = log.read_text(encoding="utf-8", errors="replace")
        if "Using function:" not in text:
            # 没有出击决策的日志（加载/观测/相机验证类）没有可比对的决策，如实列出原因
            skipped_logs.append((log.name, "日志里没有 `Using function:`（不含出击决策，无可比对）"))
            continue
        variant = DECLARED_VARIANT.get(log.name, "default_hooks")
        found = segments(text)
        if not found:
            skipped_logs.append((log.name, "没有关卡头，无法归属到具体关卡（不猜）"))
            continue
        for index, (level, body) in enumerate(found):
            if (ROOT / "data" / "campaign" / CHAPTER / f"{level}.json").is_file() is False:
                rows.append({"log": log.name, "level": level, "verdict": "关卡导出里没有这一关"})
                continue
            segment = SEGMENTS / f"{log.stem}-{level}-{index}.log"
            segment.write_text(body, encoding="utf-8", newline="\n")
            completed = subprocess.run(
                [str(SERVER), "r5-shadow", "--chapter", CHAPTER, "--level", level,
                 "--log", str(segment), "--variant", variant, "--json"],
                cwd=ROOT, capture_output=True, text=True, timeout=600, encoding="utf-8", errors="replace")
            payload_text = completed.stdout + completed.stderr
            start = payload_text.find("{")
            if start < 0:
                rows.append({"log": log.name, "level": level, "verdict": "命令没有输出 JSON"})
                continue
            payload = json.loads(payload_text[start:])
            rows.append({
                "log": log.name,
                "level": level,
                "variant": variant,
                "rounds": payload["matched"] + payload["mismatched"] + payload["skipped"],
                "matched": payload["matched"],
                "mismatched": payload["mismatched"],
                "skipped": payload["skipped"],
                "verdict": "一致" if payload["mismatched"] == 0 and payload["matched"] else "有漂移或未比对",
            })

    lines = ["# R5 历史日志扫描（决策层对照）", "",
             "> 本报告由 `tools/diagnostics/r5_log_sweep.py` 重建，不手写。",
             "> 日志来自本机忽略目录 `data/`，按关卡头切分；只证明这些**历史运行**，不代表其他关卡或配置。",
             "> 变体由运行配置**显式声明**（不是从日志的 `Using function:` 推断，避免循环论证）。", ""]
    if rows:
        lines += ["| 日志 | 关卡 | 声明变体 | 轮数 | 一致 | 不一致 | 跳过 | 结论 |",
                  "| --- | --- | --- | --- | --- | --- | --- | --- |"]
        for row in rows:
            lines.append("| {log} | {level} | {variant} | {rounds} | {matched} | {mismatched} | {skipped} | {verdict} |"
                         .format(**{key: row.get(key, "—") for key in
                                    ("log", "level", "variant", "rounds", "matched", "mismatched",
                                     "skipped", "verdict")}))
        total_rounds = sum(row.get("rounds", 0) for row in rows)
        total_mismatch = sum(row.get("mismatched", 0) for row in rows)
        lines += ["", f"小结：{len(rows)} 段合计 {total_rounds} 轮出击，决策层不一致 {total_mismatch} 轮。", ""]
    else:
        lines += ["当前检出里没有可用日志——本报告为空。", ""]
    if skipped_logs:
        lines += ["## 跳过的日志（如实列出原因，不当作通过）", "", "| 日志 | 原因 |", "| --- | --- |"]
        lines += [f"| {name} | {reason} |" for name, reason in skipped_logs]
        lines.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}（{len(rows)} 段）")
    for row in rows:
        print(f"  {row['log']:<32}{row['level']:<20}{row.get('verdict')}"
              f"（一致 {row.get('matched', '—')}/不一致 {row.get('mismatched', '—')}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
