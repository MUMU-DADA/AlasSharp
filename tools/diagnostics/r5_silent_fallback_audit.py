#!/usr/bin/env python3
"""R5 静默兜底审计：`catch` 块里"既不记日志、也不带原因地返回兜底值"的必须逐个登记理由。

为什么需要它：本项目反复被**静默失败**咬到（"计划不完整却当成执行完毕"、
"夹具字段放错层级导致空转用例"、"对拍替身漏分支造成假红"）。
这类问题的共同形态是：出错时不声不响地用一个兜底值继续跑。

判定：块内出现下面任一种就算"如实上报"，不算静默——
  * `throw`：重抛；
  * `Log(`：写日志；
  * `Result(` 或 `.Message`：把原因带进**阻塞原因**或调用记录（引擎的主要上报方式）。

做法（与状态写入审计同一套思路）：
  1. 扫 `src/Alas.Core/Campaign/**/*.cs` 的 `catch` 块；
  2. 与 `REVIEWED` 对照：每条要么已上报，要么在表里写明"为什么可以静默"；
  3. 出现新的未登记条目就失败——**棘轮**，只允许减少。

只读：不连设备、不改仓库状态。
"""
from __future__ import annotations

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
ENGINE = ROOT / "src" / "Alas.Core" / "Campaign"

# 已复核的"静默返回"：写明为什么可以不带日志（改了代码就同步改这里）
REVIEWED: dict[str, str] = {
    "SortieResult.cs": "冻结合同 `sortie-result/1` 的实现：非法路径形态交给上层路径规则，"
                       "合同里不判（`verify_result_contract.py` 38 例覆盖；改动需两侧同步）",
    "CampaignPlan.cs:330": "`TryLoad` 读导出 JSON 失败时返回 false（`plan = null`）——调用方据此**跳过**该关卡，"
                           "不是拿兜底值继续跑；可改进点：目前丢掉具体原因（JSON 损坏 vs 文件缺失），"
                           "将来接真机时值得把原因带出去",
}

CATCH = re.compile(r"catch\s*\((?P<what>[^)]*)\)\s*(?:when\s*\([^)]*\))?\s*\{", re.S)
REPORTED = ("throw", "Log(", "Result(", ".Message")


def blocks(text: str):
    """产出 (起始行号, catch 头, 块内容)。"""
    for match in CATCH.finditer(text):
        depth = 1
        index = match.end()
        while index < len(text) and depth:
            if text[index] == "{":
                depth += 1
            elif text[index] == "}":
                depth -= 1
            index += 1
        line = text[:match.start()].count("\n") + 1
        yield line, match.group("what").strip(), text[match.end():index - 1]


def main() -> int:
    if not ENGINE.is_dir():
        raise SystemExit(f"缺少 {ENGINE.relative_to(ROOT)}")
    silent: list[tuple[str, int, str]] = []
    for path in sorted(ENGINE.rglob("*.cs")):
        text = path.read_text(encoding="utf-8")
        for line, what, body in blocks(text):
            if any(marker in body for marker in REPORTED):
                continue
            if not re.search(r"\breturn\b", body):
                continue
            silent.append((path.name, line, what))

    print(f"[r5-silent-fallback] `catch` 块里既不上报也不重抛的返回：{len(silent)} 处")
    undeclared = []
    for name, line, what in silent:
        reason = REVIEWED.get(name) or REVIEWED.get(f"{name}:{line}")
        if reason is None:
            undeclared.append((name, line, what))
        else:
            print(f"  · {name}:{line} catch ({what}) —— 已登记：{reason}")
    if undeclared:
        print("[未登记]")
        for name, line, what in undeclared:
            print(f"  · {name}:{line} catch ({what})：出错后悄悄用兜底值继续——"
                  f"要么加日志/重抛/带原因返回，要么在 REVIEWED 里写明为什么可以静默")
        print(f"FAIL: {len(undeclared)} 处静默返回没有登记")
        return 1
    print("PASS: 所有静默返回都已登记理由（棘轮只允许减少）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
