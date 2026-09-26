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
  - **运行目录入口**（`--run`）：临时造运行目录，核对唯一局部日志可用；两个局部日志必须报歧义；
    只有 queue/shadow 或只有 queue 时必须失败，即使共享引擎目录存在日志也不能猜归属；
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
SHARED_REPO = ROOT / ".runtime" / "r5-probe" / "shared-engine"
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

    # 运行目录入口：只允许显式 --log 或唯一局部日志。shared-engine/log 下的文件
    # 刻意存在，用来证明时间窗/latest/upstream_log 回退已被禁止。
    if RUN_DIR.exists():
        shutil.rmtree(RUN_DIR)
    if SHARED_REPO.exists():
        shutil.rmtree(SHARED_REPO)
    SHARED_REPO.mkdir(parents=True)
    (SHARED_REPO / "log").mkdir()
    shutil.copyfile(LOG, SHARED_REPO / "log" / "shared-latest.txt")

    def make_run(name: str, local_logs: tuple[str, ...] = (), shadow: bool = False) -> pathlib.Path:
        path = RUN_DIR / name
        path.mkdir(parents=True, exist_ok=True)
        (path / "queue.json").write_text(json.dumps({
            "tasks": [{"id": "fixture", "kind": "campaign_batch", "required": True,
                       "input": {"chapters": ["campaign.campaign_main.campaign_3_1"], "clear_all": False}}],
        }, ensure_ascii=False), encoding="utf-8")
        for filename in local_logs:
            shutil.copyfile(LOG, path / filename)
        if shadow:
            (path / "shadow-observation.json").write_text(json.dumps({
                "upstream_log": "shared-latest.txt", "hooks": {"matched": 2, "mismatched": 0}
            }), encoding="utf-8")
        return path

    def run_directory(path: pathlib.Path, explicit_log: pathlib.Path | None = None) -> tuple[int, dict | None, str]:
        extra = ["--log", str(explicit_log)] if explicit_log is not None else []
        completed = subprocess.run(
            [str(SERVER), "r5-diff", "--run", str(path), "--repo", str(SHARED_REPO),
             "--detection", str(DETECTION), "--fleet-1", "A1", *extra, "--json"],
            cwd=ROOT, capture_output=True, text=True, timeout=600,
            encoding="utf-8", errors="replace")
        text = completed.stdout + completed.stderr
        start = text.find("{")
        payload = json.loads(text[start:]) if start >= 0 else None
        return completed.returncode, payload, text

    unique_code, unique_payload, unique_text = run_directory(make_run("unique", ("upstream.log",)))
    if unique_code not in (0, 1) or unique_payload is None:
        problems.append(f"唯一局部日志用例应成功解析，实际退出码 {unique_code}：{unique_text.strip().splitlines()[-3:]}")
    else:
        if unique_payload["level"] != "campaign_main/campaign_3_1":
            problems.append(f"唯一局部日志用例应从 queue.json 解析关卡，实际 {unique_payload['level']}")
        problems += check(unique_payload, unique_text, "唯一局部日志用例")
    if "日志来自运行目录内唯一的日志文件" not in unique_text:
        problems.append("唯一局部日志用例应报告局部日志来源")

    explicit_code, explicit_payload, explicit_text = run_directory(
        make_run("explicit"), explicit_log=LOG)
    if explicit_code not in (0, 1) or explicit_payload is None or "日志来自 --log" not in explicit_text:
        problems.append(f"显式 --log 应允许没有局部日志的运行目录，实际退出码 {explicit_code}：{explicit_text.strip().splitlines()[-3:]}")

    ambiguous_code, ambiguous_payload, ambiguous_text = run_directory(
        make_run("ambiguous", ("first.log", "second.txt")))
    if ambiguous_code != 1 or ambiguous_payload is not None or "多个日志候选" not in ambiguous_text:
        problems.append(f"多个局部日志必须因歧义失败，实际退出码 {ambiguous_code}：{ambiguous_text.strip().splitlines()[-3:]}")

    shadow_code, shadow_payload, shadow_text = run_directory(make_run("shadow-only", shadow=True))
    if shadow_code != 1 or shadow_payload is not None or "没有局部动作日志" not in shadow_text:
        problems.append(f"只有 queue+shadow 必须失败且不能回退 upstream_log，实际退出码 {shadow_code}：{shadow_text.strip().splitlines()[-3:]}")

    empty_code, empty_payload, empty_text = run_directory(make_run("empty"))
    if empty_code != 1 or empty_payload is not None or "没有局部动作日志" not in empty_text:
        problems.append(f"只有 queue 即使共享目录有日志也必须失败，实际退出码 {empty_code}：{empty_text.strip().splitlines()[-3:]}")

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

    print(f"[r5-diff] 已执行识别夹具与运行目录日志归属用例；{real_note}；{frame_note}")
    for note in skipped:
        print(f"  跳过：{note}")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 统一对照覆盖决策层（钩子）与动作层（原语/目标），运行目录严格按日志归属解析，差异如实列出")
    return 0


if __name__ == "__main__":
    sys.exit(main())
