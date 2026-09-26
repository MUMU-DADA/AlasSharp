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
REVIEWED: dict[tuple[str, str, str], str] = {
    ("SortieResult.cs", "Exception", "return true;"):
        "冻结合同 sortie-result/1：非法路径形态交给上层路径规则，合同不判",
    ("CampaignPlan.cs", "Exception error", "return false;"):
        "TryReadModule 读取导出失败时返回 false；调用方跳过该关卡，不继续执行兜底计划",
}

# Mask comments and strings before looking at braces or reporting calls. Their
# text is not executable evidence (for example // throw; must not hide a return).
NON_CODE = re.compile(
    r'//[^\r\n]*|/\*.*?\*/|\$*"{3,}.*?"{3,}|(?:\$@|@\$|@)"(?:""|[^"])*"'
    r'|\$?"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'', re.S)
CATCH = re.compile(r"\bcatch\b\s*(?:\((?P<what>[^)]*)\))?\s*(?:when\s*\([^)]*\))?\s*\{", re.S)
REPORTED = re.compile(r"\bthrow\b|\bLog\s*\(|\bResult\s*\(|\.Message\b")


def code_only(text: str) -> str:
    return NON_CODE.sub(lambda match: re.sub(r"[^\r\n]", " ", match.group()), text)



def blocks(text: str):
    """产出 (起始行号, catch 头, 块内容)。"""
    text = code_only(text)
    matches = list(CATCH.finditer(text))
    if len(matches) != len(re.findall(r"\bcatch\b", text)):
        raise ValueError("出现不能解析的 catch 语法，不能省略该分支的审计")
    for match in matches:
        depth = 1
        index = match.end()
        while index < len(text) and depth:
            if text[index] == "{":
                depth += 1
            elif text[index] == "}":
                depth -= 1
            index += 1
        line = text[:match.start()].count("\n") + 1
        if depth:
            raise ValueError(f"第 {line} 行 catch 块未闭合，不能完成审计")
        yield line, (match.group("what") or "裸 catch").strip(), text[match.end():index - 1]


def main() -> int:
    if not ENGINE.is_dir():
        raise SystemExit(f"缺少 {ENGINE.relative_to(ROOT)}")
    silent: list[tuple[str, int, str, str]] = []
    paths = sorted(ENGINE.rglob("*.cs"))
    if not paths:
        raise ValueError("没有 Campaign C# 源文件，不能完成静默兜底审计")
    for path in paths:
        text = path.read_text(encoding="utf-8")
        for line, what, body in blocks(text):
            if REPORTED.search(body):
                continue
            if not re.search(r"\breturn\b", body):
                continue
            silent.append((path.relative_to(ENGINE).as_posix(), line, what, " ".join(body.split())))

    print(f"[r5-silent-fallback] `catch` 块里既不上报也不重抛的返回：{len(silent)} 处")
    undeclared = []
    consumed = set()
    for name, line, what, body in silent:
        key = (name, what, body)
        reason = REVIEWED.get(key) if key not in consumed else None
        consumed.add(key)
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
