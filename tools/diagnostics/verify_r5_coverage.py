#!/usr/bin/env python3
"""R5 原语覆盖核对：已登记原语里哪些被夹具真正执行过（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_coverage.py

做的事：
  1. 跑 `r5-exec`（钩子级夹具）与 `r5-loop`（关卡循环/变体夹具），把两边**实际执行到的步骤与动作**
     归一成原语名（剥掉 `fleet_1.` / `fleet_2.` / `fleet_boss.` 前缀与 `super().`）；
  2. 与 `verify_r5_execution.EXPECTED_PRIMITIVES`（已登记原语的唯一来源）对照；
  3. **没有被任何夹具覆盖的原语必须在 <ALLOWED_GAPS> 里显式声明并写清原因**，否则失败——
     这样"缺口"是**被声明的**，而不是悄悄漏掉的；
  4. 打印覆盖率。

只读：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "diagnostics"))
from verify_r5_execution import EXPECTED_PRIMITIVES  # noqa: E402

SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"
EXEC_FIXTURE = ROOT / "tools" / "diagnostics" / "r5-execution-fixture.json"
LOOP_FIXTURE = ROOT / "tools" / "diagnostics" / "r5-loop-fixture.json"

# 允许的覆盖缺口：**必须写清原因**，且不能用来掩盖"其实能测但没测"的情况。
ALLOWED_GAPS: dict[str, str] = {
    # 已实现、也有上游真实方法对拍（复合原语扫描的 `check_accessibility`），但**暂时没有夹具钩子路径**：
    # 它只出现在目前 `plan_complete=false` 的钩子体里（需要导出器支持"局部变量 + 条件"后才会进计划），
    # 所以这里的"夹具覆盖"统计到不了它。写明原因，而不是悄悄漏掉。
    "check_accessibility": "只在 plan_complete=false 的钩子体里出现；已由复合原语扫描对拍，等结构建模后进计划",
}

# 日志标记 → 原语：有些原语是**被别的原语内部调用**的（`battle_boss` 由 clear_all 变体在无剩余敌人时调、
# `fleet_2_break_siren_caught` 由变体与 fleet_2_* 调用），它们不发设备动作、只留日志，
# 所以覆盖统计要把这些**我们自己写的日志标记**也算进来（标记就是原语里的原话，改原语时同步改这里）。
LOG_MARKERS: dict[str, str] = {
    "No battle executed.": "battle_boss",
    "Break siren caught, fleet_2:": "fleet_2_break_siren_caught",
    "No fleet caught by siren.": "fleet_2_break_siren_caught",
    "Appear caught by siren, but not fleet_2.": "fleet_2_break_siren_caught",
    "Brute clear roadblocks between fleets.": "brute_fleet_meet",
    "Fleet_2 rescue": "fleet_2_rescue",
    "Clear bouncing enemy": "clear_bouncing_enemy",
}


def normalize(name: str) -> str:
    """`fleet_boss.clear_boss` → `clear_boss`；`super().X` → `X`。"""
    text = name.strip()
    for prefix in ("fleet_1.", "fleet_2.", "fleet_boss.", "fleet_submarine.", "super()."):
        if text.startswith(prefix):
            text = text[len(prefix):]
    return text.split(" ")[0].strip()


def run(command: str, fixture: pathlib.Path) -> dict:
    completed = subprocess.run([str(SERVER), command, "--fixture", str(fixture), "--json"],
                               cwd=ROOT, capture_output=True, text=True, timeout=600,
                               encoding="utf-8", errors="replace")
    text = completed.stdout + completed.stderr
    start = text.find("{")
    if start < 0:
        raise SystemExit(f"{command} 没有输出 JSON：{text.strip().splitlines()[-3:]}")
    return json.loads(text[start:])


def main() -> int:
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    covered: dict[str, str] = {}

    def note(name: str, where: str) -> None:
        key = normalize(name)
        if key and key[0].isalpha() and key not in covered:
            covered[key] = where

    for case in run("r5-exec", EXEC_FIXTURE)["cases"]:
        for step in case.get("steps") or []:
            note(step.split("：")[0].split(":")[0], f"r5-exec/{case['name'][:28]}")
        for action in case.get("actions") or []:
            note(action.split("(")[0], f"r5-exec/{case['name'][:28]}")
    for case in run("r5-loop", LOOP_FIXTURE)["cases"]:
        for round_ in case.get("rounds") or []:
            note(round_.get("hook") or "", f"r5-loop/{case['name'][:28]}")
        for action in case.get("actions") or []:
            note(action.split("(")[0], f"r5-loop/{case['name'][:28]}")
        for log in case.get("logs") or []:
            note(log.split("：")[0], f"r5-loop/{case['name'][:28]}")
            for marker, primitive in LOG_MARKERS.items():
                if log.startswith(marker):
                    note(primitive, f"r5-loop/{case['name'][:28]}（日志标记）")

    missing = [name for name in EXPECTED_PRIMITIVES if name not in covered]
    undeclared = [name for name in missing if name not in ALLOWED_GAPS]
    declared = [name for name in missing if name in ALLOWED_GAPS]

    total = len(EXPECTED_PRIMITIVES)
    print(f"[r5-coverage] 已登记原语 {total} 个：夹具覆盖 {total - len(missing)}"
          f"（{100 * (total - len(missing)) / total:.1f}%）")

    # 计划完整性：`plan_complete=false` 的钩子 `steps` 恒为空——**必须由引擎拒绝执行**，
    # 否则等于"静默什么都不做"。这里把数量报出来（口径只说清楚"有多少钩子没有可执行计划"，
    # 不假装 100%）；引擎侧的拒绝行为由 `r5-exec` 的夹具用例断言。
    incomplete = 0
    hooks = 0
    for path in sorted((ROOT / "data" / "campaign").rglob("*.json")):
        payload = json.loads(path.read_text(encoding="utf-8"))
        for battle in (payload.get("campaign") or {}).get("battles") or []:
            if not str(battle.get("method", "")).startswith("battle_"):
                continue
            hooks += 1
            if not battle.get("plan_complete", True):
                incomplete += 1
    print(f"[r5-coverage] battle_* 钩子 {hooks} 个：有可执行计划 {hooks - incomplete}"
          f"，**plan_complete=false（引擎拒绝执行并报原因）{incomplete}**")
    if declared:
        print("[已声明缺口]")
        for name in declared:
            print(f"  {name}：{ALLOWED_GAPS[name]}")
    if undeclared:
        print("[未声明缺口]")
        for name in undeclared:
            print(f"  {name}：没有任何夹具执行到它，也没有在 ALLOWED_GAPS 里声明原因")
    if undeclared:
        print(f"FAIL: {len(undeclared)} 个原语没有夹具覆盖且未声明")
        return 1
    print("PASS: 每个已登记原语都被夹具执行过，或已在 ALLOWED_GAPS 里写明原因")
    return 0


if __name__ == "__main__":
    sys.exit(main())
