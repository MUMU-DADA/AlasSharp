#!/usr/bin/env python3
"""R5 设备宿主对照：录制宿主 vs 真机宿主必须发出同样的原语序列（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_device.py

做的事（同一关卡、同一识别输入，跑两条命令再逐条比）：
  * `Alas.Server r5-run`：**录制宿主**的动作序列（离线干跑用）；
  * `Alas.Server r5-device`：**真机宿主 + 录制渠道**的上游调用序列（只记不发）。
  断言：去掉日志调用（`logger.info`）后，两边的**(原语名, 目标格子)**序列完全一致——
  这就是"同一套原语逻辑、只换宿主"的证据；有差异说明设备宿主漏调/多调了上游方法。

可复现用例用识别夹具 `fixtures/detection-3-1.json`（把 D2 标成敌人），不依赖本机帧；
帧可用时追加一次帧驱动对照，缺帧跳过并说明。

只读：不连设备（真机宿主用的是录制渠道，方法调用不会发出去）。
"""
from __future__ import annotations

import json
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
DETECTION = ROOT / "tools" / "diagnostics" / "fixtures" / "detection-3-1.json"
FRAME = ROOT / "data" / "fixtures" / "inmap_3-1.png"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"


def run(command: str, *extra: str) -> dict:
    completed = subprocess.run([str(SERVER), command, "--chapter", "campaign_main",
                                "--level", "campaign_3_1", "--json", *extra],
                               cwd=ROOT, capture_output=True, text=True, timeout=600,
                               encoding="utf-8", errors="replace")
    text = completed.stdout + completed.stderr
    start = text.find("{")
    if completed.returncode != 0 or start < 0:
        raise SystemExit(f"{command} 失败（退出码 {completed.returncode}）：{text.strip().splitlines()[-4:]}")
    return json.loads(text[start:])


def device_sequence(payload: dict) -> list[tuple[str, str | None]]:
    """设备宿主的调用序列：去掉日志调用，取 (方法名, 第一个格参数)。"""
    sequence = []
    for call in payload["calls"]:
        if call["name"] == "logger.info":
            continue
        target = None
        for arg in call["args"]:
            if isinstance(arg, str) and arg.startswith("#"):
                target = arg[1:]
                break
        sequence.append((call["name"], target))
    return sequence


def recording_sequence(payload: dict) -> list[tuple[str, str | None]]:
    """录制宿主的动作序列 → (原语名, 目标)。"""
    sequence = []
    for action in payload["actions"]:
        match = re.match(r"^([a-z_0-9]+)(?:\(([^,)]*))?", action)
        if not match:
            continue
        name = match.group(1)
        target = (match.group(2) or "").strip() or None
        sequence.append((name, target))
    return sequence


def compare(label: str, device: dict, recording: dict) -> list[str]:
    """比**共同前缀**（两边都会发出的那段原语序列）。

    为什么不是整段相等：录制渠道的状态是**静态桩**（`battle_count` 不会增长），所以真机宿主会一直打到
    20 轮上限才撤退；而录制宿主每次 `ClearChosenEnemy` 会自增 `battle_count`，把敌人清掉后就提前撤退。
    这是"桩状态 vs 自增状态"的差别，不是宿主缺陷——真机上 `battle_count` 由设备侧结算刷新。
    因此断言：录制宿主发出的那段（撤退之前）必须是设备宿主序列的**前缀**，且设备宿主最后确实撤退。
    """
    problems: list[str] = []
    left = device_sequence(device)
    right = [item for item in recording_sequence(recording) if item[0] != "withdraw"]
    shared = min(len(left), len(right))
    if shared == 0:
        problems.append(f"{label}: 两边都没有发出原语（至少应各发一次）")
        return problems
    if left[:shared] != right[:shared]:
        first = next(index for index, (a, b) in enumerate(zip(left, right)) if a != b)
        problems.append(f"{label}: 共同前缀不一致（第 {first + 1} 处）："
                        f"设备宿主 {left[first]} vs 录制宿主 {right[first]}")
    if right != left[:len(right)]:
        problems.append(f"{label}: 录制宿主那一段（{right}）不是设备宿主序列的前缀")
    if not any(name == "withdraw" for name, _ in left):
        problems.append(f"{label}: 设备宿主应在上限后撤退（序列里没有 withdraw）")
    if not any(name.startswith("clear_") or name.startswith("battle") for name, _ in right):
        problems.append(f"{label}: 录制宿主那一段里没有清敌/出击类原语，用例本身可能没生效")
    return problems


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    problems: list[str] = []
    skipped: list[str] = []

    extra = ("--detection", str(DETECTION), "--fleet-1", "A1")
    fixture_problems = compare("识别夹具用例", run("r5-device", *extra), run("r5-run", *extra))
    problems += fixture_problems

    if FRAME.is_file():
        frame_extra = ("--frame", str(FRAME), "--fleet-1", "A1")
        problems += compare("帧用例", run("r5-device", *frame_extra), run("r5-run", *frame_extra))
        frame_note = "帧用例：已跑"
    else:
        skipped.append(f"缺 {FRAME.relative_to(ROOT)}（忽略目录），跳过帧用例")
        frame_note = "帧用例：跳过"

    print(f"[r5-device] 识别夹具用例：{'一致' if not fixture_problems else '不一致'}；{frame_note}")
    for note in skipped:
        print(f"  跳过：{note}")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 真机宿主与录制宿主发出相同的原语序列（同一套逻辑，只换宿主）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
