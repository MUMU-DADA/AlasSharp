#!/usr/bin/env python3
"""R5 端到端干跑对拍：真机帧（或识别 JSON）→ 识别 → 引擎状态 → 关卡循环（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_run.py

做的事：
  1. **可复现用例**（不依赖本机帧）：`campaign_1_1` + 识别夹具
     `tools/diagnostics/fixtures/detection-sample.json`（把 F1 标成敌人、G1 标成 boss）
     → 期望先 `battle_0` 打 F1、再 `battle_1` 打 G1，最后按 `Error_HandleError` 撤退结束；
  2. **真机帧用例**（帧在忽略目录 `data/fixtures/` 里，检出可能没有）：`campaign_3_1` + `inmap_3-1.png`
     → 期望识别出 28 格、首轮选 `battle_0`、动作里出现 `clear_chosen_enemy(...)`；缺帧时**跳过并说明**，
     不把"没有帧"当成失败。

只做离线决策与干跑记录：不连设备、不点任何东西。
"""
from __future__ import annotations

import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
DETECTION = ROOT / "tools" / "diagnostics" / "fixtures" / "detection-sample.json"
FRAME = ROOT / "data" / "fixtures" / "inmap_3-1.png"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"


def run(*extra: str) -> str:
    completed = subprocess.run([str(SERVER), "r5-run", *extra],
                               cwd=ROOT, capture_output=True, text=True, timeout=600,
                               encoding="utf-8", errors="replace")
    return completed.stdout + completed.stderr


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    problems: list[str] = []
    skipped: list[str] = []

    # 1) 可复现：识别夹具驱动
    text = run("--chapter", "campaign_main", "--level", "campaign_1_1",
               "--detection", str(DETECTION), "--fleet-1", "A1")
    hooks = re.findall(r"第 \d+ 轮 battle_count=\d+ → (\S+)：", text)
    if hooks[:2] != ["battle_0", "battle_1"]:
        problems.append(f"识别夹具驱动的钩子序列应为 battle_0 → battle_1，实际 {hooks}")
    if "clear_chosen_enemy(F1" not in text:
        problems.append("识别到 F1 有敌人，干跑动作里应出现 clear_chosen_enemy(F1")
    if "clear_chosen_enemy(G1, expected=boss)" not in text:
        problems.append("识别到 G1 是 boss，干跑动作里应出现 clear_chosen_enemy(G1, expected=boss)")
    if "[结论   ] Ended" not in text:
        problems.append(f"应以上游语义（撤退）结束，实际输出：{text.strip().splitlines()[-3:]}")

    # 2) 真机帧驱动（帧缺失时跳过）
    if FRAME.is_file():
        frame_text = run("--chapter", "campaign_main", "--level", "campaign_3_1",
                         "--frame", str(FRAME), "--fleet-1", "A1")
        if "识别到 28 格" not in frame_text:
            problems.append("inmap_3-1.png 应识别出 28 格（与既有识别对照一致）")
        frame_hooks = re.findall(r"第 \d+ 轮 battle_count=\d+ → (\S+)：", frame_text)
        if not frame_hooks or frame_hooks[0] != "battle_0":
            problems.append(f"帧驱动时首轮应选 battle_0，实际 {frame_hooks}")
        if "clear_chosen_enemy(" not in frame_text:
            problems.append("帧驱动时应有 clear_chosen_enemy 动作（帧上有敌人）")
        frame_note = f"帧用例：{len(frame_hooks)} 轮，动作 {'有' if 'clear_chosen_enemy(' in frame_text else '无'}"
    else:
        skipped.append(f"缺 {FRAME.relative_to(ROOT)}（忽略目录），跳过真机帧用例")
        frame_note = "帧用例：跳过"

    print(f"[r5-run] 识别夹具用例通过；{frame_note}")
    for note in skipped:
        print(f"  跳过：{note}")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 识别 → 状态 → 关卡循环 → 原语动作 这条链在离线输入上闭合")
    return 0


if __name__ == "__main__":
    sys.exit(main())
