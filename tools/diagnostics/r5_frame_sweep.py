#!/usr/bin/env python3
"""R5 真机帧扫描：把本地可识别的帧逐个跑「识别 → 状态 → 关卡循环」，生成证据表。

用法：
    python tools/diagnostics/r5_frame_sweep.py

做的事（全部离线，不连设备）：
  1. 扫描 `data/fixtures/*.png`，按文件名推断关卡（`inmap_3-1` → `campaign_main/campaign_3_1`、
     `map_hard_1_4` → `campaign_main/campaign_1_4` …）；
  2. 对每一对跑 `r5-run`（录制宿主干跑）与 `r5-device`（真机宿主 + 录制渠道）；
  3. 记录：识别到的格数、循环结论、轮数、干跑动作摘要、上游调用数，以及**两个宿主的序列是否一致**；
  4. 报告写 `docs/archive/reports/r5-frame-sweep.md`（由本脚本重建，不手写）。

**这些帧在忽略目录里**（检出可能没有）：一个都没有时脚本会打印说明并正常退出（不当失败）。
只读：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import json
import pathlib
import re
import subprocess
import sys
from verify_r5_device import device_sequence, recording_sequence

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "data" / "fixtures"
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-frame-sweep.md"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"

# 文件名 → (章, 关)。只列能确定的；认不出的帧跳过（不猜关卡）。
NAME_HINTS = {
    r"^inmap_(\d+)-(\d+)": lambda m: ("campaign_main", f"campaign_{m.group(1)}_{m.group(2)}"),
    r"^map_(\d+)_(\d+)": lambda m: ("campaign_main", f"campaign_{m.group(1)}_{m.group(2)}"),
    r"^map_hard_(\d+)_(\d+)": lambda m: ("campaign_main", f"campaign_{m.group(1)}_{m.group(2)}"),
}


def level_for(frame: pathlib.Path) -> tuple[str, str] | None:
    for pattern, build in NAME_HINTS.items():
        match = re.match(pattern, frame.stem)
        if match:
            chapter, level = build(match)
            if (ROOT / "data" / "campaign" / chapter / f"{level}.json").is_file():
                return chapter, level
    return None


def run(command: str, chapter: str, level: str, frame: pathlib.Path) -> dict | None:
    completed = subprocess.run([str(SERVER), command, "--chapter", chapter, "--level", level,
                                "--frame", str(frame), "--json"],
                               cwd=ROOT, capture_output=True, text=True, timeout=900,
                               encoding="utf-8", errors="replace")
    text = completed.stdout + completed.stderr
    start = text.find("{")
    if completed.returncode != 0 or start < 0:
        return None
    try:
        return json.loads(text[start:])
    except json.JSONDecodeError:
        return None


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    frames = sorted(FIXTURES.glob("*.png")) if FIXTURES.is_dir() else []
    rows: list[dict] = []
    for frame in frames:
        target = level_for(frame)
        if target is None:
            continue
        chapter, level = target
        dry = run("r5-run", chapter, level, frame)
        device = run("r5-device", chapter, level, frame)
        if dry is None or device is None:
            rows.append({"frame": frame.name, "level": f"{chapter}/{level}", "detected": "识别失败"})
            continue
        dry_sequence = recording_sequence(dry)
        device_actions = device_sequence(device)
        hosts_match = (bool(dry_sequence) and device_actions == dry_sequence
                       and dry.get("rounds") == device.get("rounds")
                       and dry.get("outcome") == device.get("outcome"))
        rows.append({
            "frame": frame.name,
            "level": f"{chapter}/{level}",
            "detected": dry.get("detection") or "未叠加识别",
            "outcome": dry.get("outcome"),
            "rounds": len(dry.get("rounds") or []),
            "actions": len(dry_sequence),
            "calls": len(device_actions),
            "hosts_match": hosts_match,
            "detail": dry.get("detail"),
        })

    lines = ["# R5 真机帧扫描（识别 → 状态 → 关卡循环）", "",
             "> 本报告由 `tools/diagnostics/r5_frame_sweep.py` 重建，不手写。",
             "> 帧来自本机忽略目录 `data/fixtures/`，检出可能没有；**只证明这些帧**，不代表整类地图。", ""]
    if not rows:
        lines += ["当前检出里没有可识别的帧（`data/fixtures/*.png` 缺失或文件名认不出关卡）——本报告为空。", ""]
    else:
        lines += ["| 帧 | 关卡 | 识别 | 结论 | 轮数 | 干跑动作 | 上游调用 | 两宿主一致 |",
                  "| --- | --- | --- | --- | --- | --- | --- | --- |"]
        for row in rows:
            lines.append("| {frame} | {level} | {detected} | {outcome} | {rounds} | {actions} | {calls} | {match} |"
                         .format(frame=row["frame"], level=row["level"], detected=row.get("detected", "-"),
                                 outcome=row.get("outcome", "-"), rounds=row.get("rounds", "-"),
                                 actions=row.get("actions", "-"), calls=row.get("calls", "-"),
                                 match="是" if row.get("hosts_match") else ("—" if "hosts_match" not in row else "**否**")))
        matched = sum(1 for row in rows if row.get("hosts_match"))
        lines += ["", f"小结：{len(rows)} 帧中 {matched} 帧的两宿主原语序列、轮次和结论一致。",
                  "口径：固定地图夹具和显式成功计数反馈；比较完整动作（舰队路径、目标、expected）、逐轮状态及结论。只省略未改变当前舰队的 ensure 查询，不证明实战效果。", ""]

    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}（{len(rows)} 帧）")
    for row in rows:
        print(f"  {row['frame']:<22}{row['level']:<34}{row.get('detected', row.get('detected', '-'))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
