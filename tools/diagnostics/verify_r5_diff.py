#!/usr/bin/env python3
"""R5 原语动作层对照：上游实际动作（日志）vs C# 干跑动作（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_diff.py

做的事（全部可复现，不依赖本机帧）：
  用夹具日志 `fixtures/actions-3-1.log`（上游格式：`<<< CLEAR ENEMY >>>` + `Clear enemy: D2` ×2）
  与识别夹具 `fixtures/detection-3-1.json`（把 D2 标成敌人）跑
  `Alas.Server r5-diff --log <日志> --chapter campaign_main --level campaign_3_1 --detection <识别> --json`，
  核对：
  - **决策层**：上游日志里的 `Using function:` 与 C# 在同一 battle_count 下会选的钩子逐轮一致（无漂移）；
  - **动作层**：两边都用到 `clear_chosen_enemy`，且**目标交集是 D2**（"打的是同一格"的正面证据）；
  - 只有上游用到的原语（`clear_enemy`，上游会额外打包装层表头）与只有 C# 用到的原语（`withdraw`）
    被如实列出——这类差异是**轨迹粒度/状态来源**造成的，不是引擎错误；
  - 没有"目标不一致"（本夹具两边打同一格）；
  - **运行目录入口**（`--run`）：临时造一个运行目录（`queue.json` + 目录内日志），核对它自己能解析出
    章节与日志并给出同样的两层结论——真机验证时就不用再手填 `--chapter/--level/--log`；
  - 帧可用时（`data/fixtures/inmap_3-1.png`）额外跑一次帧驱动对照，缺帧则跳过并说明。

只做离线对照：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import json
import pathlib
import shutil
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
LOG = ROOT / "tools" / "diagnostics" / "fixtures" / "actions-3-1.log"
DETECTION = ROOT / "tools" / "diagnostics" / "fixtures" / "detection-3-1.json"
FRAME = ROOT / "data" / "fixtures" / "inmap_3-1.png"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"
RUN_DIR = ROOT / ".runtime" / "r5-probe" / "run-dir-fixture"
# 真机口径：硬模式 1-4 的一次真实运行日志 + 同关卡的现场帧（都在忽略目录；两者不是同一局）
REAL_LOG = ROOT / "data" / "s3_native_1_4.log"
REAL_FRAME = ROOT / "data" / "fixtures" / "map_hard_1_4.png"


def run_diff(arguments: list[str]) -> tuple[int, dict | None, str]:
    completed = subprocess.run([str(SERVER), "r5-diff", *arguments, "--json"],
                               cwd=ROOT, capture_output=True, text=True, timeout=600,
                               encoding="utf-8", errors="replace")
    text = completed.stdout + completed.stderr
    start = text.find("{")
    payload = json.loads(text[start:]) if start >= 0 else None
    return completed.returncode, payload, text


def diff(*extra: str) -> tuple[int, dict | None, str]:
    return run_diff(["--log", str(LOG), "--chapter", "campaign_main",
                     "--level", "campaign_3_1", *extra])


def check(payload: dict | None, text: str, label: str) -> list[str]:
    problems: list[str] = []
    if payload is None:
        return [f"{label}: 没有解析到 JSON 输出：{text.strip().splitlines()[-3:]}"]
    hooks = payload["hooks"]
    if hooks["mismatched"] or hooks["matched"] < 2:
        problems.append(f"{label}: 决策层应至少 2 轮一致且无漂移，实际 {hooks['matched']} 一致 / "
                        f"{hooks['mismatched']} 不一致")
    actions = payload["actions"]
    entries = {entry["primitive"]: entry for entry in actions["entries"]}
    common = entries.get("clear_chosen_enemy")
    if common is None:
        problems.append(f"{label}: 两边都应有 clear_chosen_enemy，实际原语 {sorted(entries)}")
    else:
        if "D2" not in common["upstream_targets"]:
            problems.append(f"{label}: 上游目标应含 D2，实际 {common['upstream_targets']}")
        if "D2" not in common["csharp_targets"]:
            problems.append(f"{label}: C# 目标应含 D2，实际 {common['csharp_targets']}")
        if not common["targets_intersect"]:
            problems.append(f"{label}: 目标交集应为真（两边打同一格）")
    if not actions["any_target_matched"]:
        problems.append(f"{label}: 应报告『至少一个原语打到同一格』")
    if actions["target_mismatches"]:
        problems.append(f"{label}: 本夹具不应有目标不一致，实际 {actions['target_mismatches']}")
    if "clear_enemy" not in actions["only_upstream"]:
        problems.append(f"{label}: 上游的包装层原语 clear_enemy 应列在 only_upstream，"
                        f"实际 {actions['only_upstream']}")
    return problems


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    problems: list[str] = []
    skipped: list[str] = []

    code, payload, text = diff("--detection", str(DETECTION), "--fleet-1", "A1")
    if code not in (0, 1):
        problems.append(f"识别夹具用例退出码 {code}：{text.strip().splitlines()[-3:]}")
    problems += check(payload, text, "识别夹具用例")

    if FRAME.is_file():
        frame_code, frame_payload, frame_text = diff("--frame", str(FRAME), "--fleet-1", "A1")
        if frame_code not in (0, 1):
            problems.append(f"帧用例退出码 {frame_code}：{frame_text.strip().splitlines()[-3:]}")
        problems += check(frame_payload, frame_text, "帧用例")
        frame_note = "帧用例：已跑"
    else:
        skipped.append(f"缺 {FRAME.relative_to(ROOT)}（忽略目录），跳过帧用例")
        frame_note = "帧用例：跳过"

    # 运行目录入口：造一个最小运行目录（queue.json + 目录内日志），核对 --run 能自解析
    if RUN_DIR.exists():
        shutil.rmtree(RUN_DIR)
    RUN_DIR.mkdir(parents=True)
    (RUN_DIR / "queue.json").write_text(json.dumps({
        "tasks": [{"id": "fixture", "kind": "campaign_batch", "required": True,
                   "input": {"chapters": ["campaign.campaign_main.campaign_3_1"], "clear_all": False}}],
    }, ensure_ascii=False), encoding="utf-8")
    shutil.copyfile(LOG, RUN_DIR / "upstream.log")
    run_completed = subprocess.run(
        [str(SERVER), "r5-diff", "--run", str(RUN_DIR), "--detection", str(DETECTION),
         "--fleet-1", "A1", "--json"],
        cwd=ROOT, capture_output=True, text=True, timeout=600, encoding="utf-8", errors="replace")
    run_text = run_completed.stdout + run_completed.stderr
    run_start = run_text.find("{")
    run_payload = json.loads(run_text[run_start:]) if run_start >= 0 else None
    if run_completed.returncode not in (0, 1):
        problems.append(f"运行目录用例退出码 {run_completed.returncode}：{run_text.strip().splitlines()[-3:]}")
    if run_payload is None:
        problems.append("运行目录用例没有解析到 JSON 输出")
    else:
        if run_payload["level"] != "campaign_main/campaign_3_1":
            problems.append(f"运行目录用例应从 queue.json 解析出 campaign_3_1，实际 {run_payload['level']}")
        problems += check(run_payload, run_text, "运行目录用例")
    if "章节来自 queue.json" not in run_text:
        problems.append("运行目录用例应说明章节来源（解析来源必须打印，不能隐式猜）")

    # 真机口径（可跳过）：真实运行日志 + 同关卡现场帧。两者不是同一局，所以只断言
    # 决策层一致与"目标格子有交集"，不断言动作完全一致。
    if REAL_LOG.is_file() and REAL_FRAME.is_file():
        real_code, real_payload, real_text = run_diff(
            ["--log", str(REAL_LOG), "--chapter", "campaign_main", "--level", "campaign_1_4",
             "--frame", str(REAL_FRAME), "--fleet-1", "A1"])
        if real_code not in (0, 1):
            problems.append(f"真机口径用例退出码 {real_code}：{real_text.strip().splitlines()[-3:]}")
        elif real_payload is None:
            problems.append("真机口径用例没有解析到 JSON 输出")
        else:
            hooks = real_payload["hooks"]
            if hooks["mismatched"] or hooks["matched"] < 4:
                problems.append(f"真机口径用例：决策层应 4 轮一致且无漂移，实际 {hooks['matched']}/"
                                f"{hooks['mismatched']}")
            if not real_payload["actions"]["any_target_matched"]:
                problems.append("真机口径用例：帧驱动后应至少有一个原语打到上游打过的格子")
            route = real_payload["route"]
            if not route["common"]:
                problems.append("真机口径用例：路线层应报告共同格子（上游走位与 C# 走位有交集）")
            if not route["same_order"]:
                problems.append("真机口径用例：共同格子的相对顺序应一致")
            if "fleet_1_position" in real_payload["actions"]["only_upstream"]:
                problems.append("纯日志标记（fleet_1_position）不应出现在原语对照的 only_upstream 里")
        # 不给帧时应当**打不到任何格子**——这条对照说明"是状态适配器在起作用"，不是巧合
        plain_code, plain_payload, _ = run_diff(
            ["--log", str(REAL_LOG), "--chapter", "campaign_main", "--level", "campaign_1_4",
             "--fleet-1", "A1"])
        if plain_code in (0, 1) and plain_payload is not None \
                and plain_payload["actions"]["any_target_matched"]:
            problems.append("不给识别结果时不应有目标交集（否则说明结论不是由识别驱动的）")
        real_note = "真机口径用例：已跑（帧驱动有交集、无识别无交集）"
    else:
        skipped.append("缺 data/s3_native_1_4.log 或 data/fixtures/map_hard_1_4.png（忽略目录），跳过真机口径用例")
        real_note = "真机口径用例：跳过"

    print(f"[r5-diff] 识别夹具用例：决策层无漂移、动作层两边都打 D2、目标交集为真；"
          f"运行目录用例：已跑；{real_note}；{frame_note}")
    for note in skipped:
        print(f"  跳过：{note}")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 统一对照同时覆盖决策层（钩子）与动作层（原语/目标），运行目录入口可自解析，差异如实列出")
    return 0


if __name__ == "__main__":
    sys.exit(main())
