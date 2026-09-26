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


def device_sequence(payload: dict) -> list[tuple[str, str | None, str]]:
    """完整动作序列保留舰队路径、目标和 expected；省略当前舰队不变的 ensure 查询。"""
    sequence = []
    current_fleet = payload["initial_fleet"]
    for call in payload["calls"]:
        if call["name"] == "logger.info":
            continue
        name = call["name"]
        if name == "fleet_ensure":
            index = int(call["kwargs"]["index"])
            if index == current_fleet:
                continue  # 原生 fleet_ensure 返回 False，没有发生切队动作。
            current_fleet = index
            sequence.append((name, str(index), ""))
            continue
        prefix = name.split(".")[0]
        current_fleet = {"fleet_1": 1, "fleet_2": 2,
                         "fleet_boss": payload["boss_fleet"]}.get(prefix, current_fleet)
        target = None
        for arg in call["args"]:
            if isinstance(arg, str) and arg.startswith("#"):
                target = arg[1:]
                break
        sequence.append((name, target, call["kwargs"].get("expected", "")))
    return sequence


def recording_sequence(payload: dict) -> list[tuple[str, str | None, str]]:
    """录制宿主的动作序列 → (原语名, 目标)。"""
    sequence = []
    for action in payload["actions"]:
        match = re.match(r"^([a-z_0-9]+)(?:\(([^,)]*))?", action)
        if not match:
            continue
        name = match.group(1)
        target = (match.group(2) or "").strip() or None
        expected_match = re.search(r"(?:, )expected=([^,)]*)", action)
        fleet_match = re.search(r"(?:, )fleet=([^,)]*)", action)
        if fleet_match:
            fleet = fleet_match.group(1)
            name = ("fleet_boss" if fleet == "boss" else fleet) + "." + name
        sequence.append((name, target, expected_match.group(1) if expected_match else ""))
    return sequence


def compare(label: str, device: dict, recording: dict) -> list[str]:
    """比较同一份状态反馈下两条宿主的完整原语序列。"""
    problems: list[str] = []
    left = device_sequence(device)
    right = recording_sequence(recording)
    if device.get("rounds") != recording.get("rounds"):
        problems.append(f"{label}: 钩子、计数或逐轮结果不同")
    if device.get("outcome") != recording.get("outcome"):
        problems.append(f"{label}: 循环结论不同")
    if not left or not right:
        problems.append(f"{label}: 两边都没有发出原语（至少应各发一次）")
        return problems
    if left != right:
        shared = min(len(left), len(right))
        first = next((index for index, (a, b) in enumerate(zip(left, right)) if a != b), shared)
        problems.append(f"{label}: 完整序列不一致（第 {first + 1} 处）："
                        f"设备宿主 {left[first] if first < len(left) else '<结束>'} vs "
                        f"录制宿主 {right[first] if first < len(right) else '<结束>'}")
        if len(left) != len(right):
            problems.append(f"{label}: 完整序列长度不同（设备 {len(left)}，录制 {len(right)}）")
    if not any(name == "withdraw" for name, _, _ in left):
        problems.append(f"{label}: 设备宿主应在上限后撤退（序列里没有 withdraw）")
    if not any(name.split('.')[-1].startswith(("clear_", "battle")) for name, _, _ in right):
        problems.append(f"{label}: 录制宿主那一段里没有清敌/出击类原语，用例本身可能没生效")
    return problems


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    problems: list[str] = []
    skipped: list[str] = []

    extra = ("--detection", str(DETECTION), "--fleet-1", "A1")
    device, recording = run("r5-device", *extra), run("r5-run", *extra)
    fixture_problems = compare("识别夹具用例", device, recording)
    problems += fixture_problems
    # 反例必须捕获尾部丢步、目标/舰队/expected 和状态反馈漂移。
    import copy
    for field, value in (("name", "fleet_2.clear_chosen_enemy"), ("args", ["#A1"]),
                         ("kwargs", {"expected": "siren"})):
        bad = copy.deepcopy(device)
        action = next(call for call in bad["calls"] if call["name"] == "clear_chosen_enemy")
        action[field] = value
        assert compare("损坏动作", bad, recording), field
    bad = copy.deepcopy(device)
    bad["calls"] = bad["calls"][:-1]
    assert compare("缺少尾部动作", bad, recording)
    bad = copy.deepcopy(device)
    bad["rounds"][0]["battle_count"] += 1
    assert compare("计数反馈漂移", bad, recording)

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
