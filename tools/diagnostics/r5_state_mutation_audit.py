#!/usr/bin/env python3
"""R5 状态写入审计：上游**会改地图/实例状态**的方法 vs C# 替换掉的原语。

用法：
    python tools/diagnostics/r5_state_mutation_audit.py

为什么要它：C# 逐条替换了上游的原语方法，但上游有些方法**顺手写了状态**
（`Fleet.goto` 结尾 `wipe_out()` + `is_fleet` 重设、`pick_up_flare` 的 `grid.is_flare = True` …）。
替换掉方法就丢掉了那些写入。这类缺口只有逐行看上游源码才看得见，所以这里扫一遍：

  1. 取 C# 注册表里每个原语名（`CampaignPrimitives.cs` 的 `["name"] = new CampaignPrimitive(`）；
  2. 在上游按 `Map` → 关卡家族基类 → `CampaignBase` 的顺序找到同名方法（找不到就如实列为"未定位"）；
  3. 扫方法体里的**写入模式**（给格子/实例属性赋值、`wipe_out`/`set`/`append` 等）；
  4. 与人工核对过的清单比对：已处理 / 属设备侧（仍由上游执行）/ **待确认**。

只读：只做静态扫描，不导入设备侧、不执行游戏动作。
"""
from __future__ import annotations

import inspect
import json
import os
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
UPSTREAM = pathlib.Path(os.environ.get("ALAS_REPO") or ROOT / ".runtime" / "engine").resolve()
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-state-mutation-audit.md"
REGISTRY = ROOT / "src" / "Alas.Core" / "Campaign" / "CampaignPrimitives.cs"

# 内部原语名 → 上游方法名（少数原语两侧名字不同；不写在这里就会报成"未在上游定位到同名方法"）
UPSTREAM_ALIASES = {
    "ensure_fleet": "fleet_ensure",
}

# 写入模式：给"格子/地图/舰队状态"赋值，或调用会改状态的方法。
# 注意：所有写入模式都用 `(?<![=!<>])=(?!=)` 而不是裸 `=` 来排除比较运算符——
# 否则 `return self.fleet_1_location == grid.location` 这种**纯读取**会被当成赋值
# （实测：加了 `fleet_at` 原语之后报了假阳性，误判成"舰队位置赋值"）。
MUTATION_PATTERNS = [
    (re.compile(r"\b\w+\.(is_|may_)[a-z_]+\s*(?<![=!<>])=(?!=)"), "格子标志赋值"),
    (re.compile(r"\bself\.fleet_\d_location\s*(?<![=!<>])=(?!=)"), "舰队位置赋值"),
    (re.compile(r"\.wipe_out\("), "wipe_out（清格子）"),
    (re.compile(r"\.select\([^)]*\)\.set\("), "SelectedGrids.set"),
    (re.compile(r"\bself\.(picked_[a-z_]+|mystery_count|round)\b[^=]*[+-]?(?<![=!<>])=(?!=)"),
     "实例计数/记账"),
]

# 人工核对过的结论（逐条给依据）。新增的写入会以"待确认"出现在报告里，逼着再看一次。
REVIEWED = {
    ("pick_up_flare", "格子标志赋值"):
        "`grid.is_flare = True`：C# 已通过宿主 `MarkFlare` → 渠道 `set` 同步（r5-device 自检）",
    ("pick_up_light_house", "实例计数/记账"):
        "只往 `picked_light_house` 记账；C# 宿主同名列表，不影响上游读到的状态",
    ("pick_up_flare", "实例计数/记账"):
        "只往 `picked_flare` 记账；同上",
    ("fleet_2_break_siren_caught", "格子标志赋值"):
        "`grid.is_caught_by_siren = False`：C# `ClearCaughtBySirenFlags` 覆盖（两宿主都改自己模型）",
    ("fleet_2_break_siren_caught", "实例计数/记账"):
        "无实质写入；见上一条",
    ("brute_find_roadblocks", "格子标志赋值"):
        "`block.is_enemy` 的临时去掉/恢复：上游枚举用，方法结束前恢复；C# 用不可变副本做同一件事",
    ("brute_fleet_meet", "格子标志赋值"):
        "同上（经由 `brute_find_roadblocks`）",
    ("clear_potential_boss", "格子标志赋值"):
        "同上（经由 `brute_find_roadblocks`）",
    ("fleet_2_rescue", "格子标志赋值"):
        "同上（经由 `brute_find_roadblocks`）",
    ("brute_clear_boss", "格子标志赋值"):
        "同上（经由 `brute_find_roadblocks`）",
    ("clear_bouncing_enemy", "SelectedGrids.set"):
        "`may_bouncing_enemy = False`：C# 成功分支已按上游顺序置假 + `UpdateMap()`（置假走 `SetGridFlag` 同步到上游）",
    ("pick_up_flare", "格子标志赋值"):
        "`grid.is_flare = True`：C# 走宿主 `SetGridFlag` → 渠道 `set` 同步（r5-device 自检）",
    ("clear_mechanism", "格子标志赋值"):
        "上游把机关标成已触发/解阻；C# 侧同名字段在模型上，属决策层可覆盖范围（见报告备注）",
    ("clear_roadblocks", "格子标志赋值"):
        "无直接赋值；枚举走 `brute_find_roadblocks` 时同上",
    ("clear_potential_roadblocks", "格子标志赋值"):
        "无直接赋值；同上",
    ("clear_first_roadblocks", "格子标志赋值"):
        "无直接赋值；同上",
}
# 设备侧方法（C# 不替换，仍由上游执行，故不做要求）
DEVICE_SIDE = {
    "clear_chosen_enemy", "goto", "submarine_move_near_boss", "focus_to", "withdraw",
    "update_map", "map_swipe", "ensure_edge_insight", "ensure_no_info_bar", "clear_chosen_mystery",
}


def registry_primitives() -> list[str]:
    text = REGISTRY.read_text(encoding="utf-8")
    names = sorted(set(re.findall(r'\["([a-z_0-9]+)"\]\s*=\s*new CampaignPrimitive\(', text)))
    if not names:
        raise ValueError("原语注册表为空或声明格式已变化，不能完成审计")
    return names


def upstream_methods() -> dict[str, tuple[object, str]]:
    """按 `Map` → `CampaignBase` → **各级关卡家族基类**的顺序找同名方法。

    关卡家族基类（`campaign/**/*_base.py` 里的 `CampaignBase`）也定义了原语 helper，
    例如 `pick_up_flare` / `pick_up_light_house`（全库只在 `campaign_14_base.py` 一处）。
    """
    sys.path.insert(0, str(UPSTREAM))
    import importlib  # noqa: PLC0415

    from module.map.map import Map  # noqa: PLC0415
    from module.campaign.campaign_base import CampaignBase  # noqa: PLC0415

    owners: list[tuple[str, object]] = [("module/map/map.py:Map", Map),
                                        ("module/campaign/campaign_base.py:CampaignBase", CampaignBase)]
    for path in sorted((UPSTREAM / "campaign").rglob("*_base.py")):
        module_name = ".".join(path.relative_to(UPSTREAM).with_suffix("").parts)
        try:
            module = importlib.import_module(module_name)
        except Exception as error:
            raise RuntimeError(f"无法审计上游家族模块 {module_name}") from error
        for attr in dir(module):
            obj = getattr(module, attr)
            if isinstance(obj, type) and attr == "CampaignBase":
                owners.append((f"{module_name}:{attr}", obj))

    found: dict[str, tuple[object, str]] = {}
    for name in registry_primitives():
        # 少数原语两侧名字不同（内部名 vs 上游名），先按别名找，再按原名找
        candidates = ([UPSTREAM_ALIASES[name]] if name in UPSTREAM_ALIASES else []) + [name]
        for label, owner in owners:
            for candidate in candidates:
                method = getattr(owner, candidate, None)
                if callable(method):
                    found[name] = (method, f"{label}（上游名 {candidate}）" if candidate != name else label)
                    break
            if name in found:
                break
    return found


def scan(method) -> list[tuple[str, str]]:
    try:
        source = inspect.getsource(method)
    except (OSError, TypeError) as error:
        raise RuntimeError("无法读取上游方法源码，不能判定为无状态写入") from error
    hits = []
    for pattern, label in MUTATION_PATTERNS:
        for line in source.splitlines():
            if pattern.search(line):
                hits.append((label, line.strip()))
                break
    return hits


def hook_mutation_scan() -> tuple[list[tuple[str, str, int]], int]:
    """扫**关卡钩子体**里的状态写入。

    原语的写入只覆盖"方法被替换"这一面；钩子体（`battle_*` 等覆写）才是被导出成**计划**并执行的东西。
    用 `ast` 静态扫全库关卡文件，找"给属性赋值 / 调 `.set(` / `.wipe_out(`"这类写法，
    返回 (命中列表, 扫过的钩子数)。命中项要在报告里逐条给结论。
    """
    import ast  # noqa: PLC0415

    hits: list[tuple[str, str, int]] = []
    hooks = 0
    paths = sorted((UPSTREAM / "campaign").rglob("*.py"))
    if not paths:
        raise FileNotFoundError("缺少上游 campaign 源文件，无法完成钩子审计")
    for path in paths:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path.relative_to(UPSTREAM)))
        module = str(path.relative_to(UPSTREAM)).replace("\\", "/")
        for node in ast.walk(tree):
            if not isinstance(node, ast.ClassDef):
                continue
            for item in node.body:
                if not isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    continue
                if not (item.name.startswith("battle_") or item.name.startswith("handle_")):
                    continue
                hooks += 1
                for sub in ast.walk(item):
                    target = None
                    if isinstance(sub, ast.Assign) and sub.targets and isinstance(sub.targets[0], ast.Attribute):
                        target = sub.targets[0].attr
                    elif isinstance(sub, ast.AugAssign) and isinstance(sub.target, ast.Attribute):
                        target = sub.target.attr
                    if target and re.fullmatch(r"(is|may)_[a-z_]+", target):
                        hits.append((module, f"{item.name}: {target} = …", sub.lineno))
                    if isinstance(sub, ast.Call) and isinstance(sub.func, ast.Attribute) and \
                            sub.func.attr in ("set", "wipe_out"):
                        hits.append((module, f"{item.name}: .{sub.func.attr}(…)", sub.lineno))
    if not hooks:
        raise ValueError("没有找到任何 battle_*/handle_* 钩子，不能完成审计")
    return hits, hooks


def main() -> int:
    if not (UPSTREAM / "module/map/map.py").is_file():
        raise FileNotFoundError("缺少上游地图源码；请准备 .runtime/engine 或指定 ALAS_REPO")
    primitives = registry_primitives()
    methods = upstream_methods()
    rows = []
    todo = 0
    for name in primitives:
        if name in DEVICE_SIDE:
            rows.append((name, "设备侧（仍由上游执行）", "", "不需要 C# 镜像"))
            continue
        located = methods.get(name)
        if located is None:
            rows.append((name, "未在上游定位到同名方法", "", "需要人工确认：可能名字不同或未接线"))
            todo += 1
            continue
        method, owner = located
        hits = scan(method)
        if not hits:
            rows.append((name, f"无状态写入（{owner}）", "", "无需处理"))
            continue
        for label, line in hits:
            note = REVIEWED.get((name, label))
            if note is None:
                todo += 1
                note = "**待确认**"
            rows.append((name, f"{label}（{owner}）", f"`{line}`", note))

    mutation_rows = [row for row in rows
                     if "无状态写入" not in row[1] and "设备侧" not in row[1]
                     and "未在上游定位" not in row[1]]
    reviewed_rows = [row for row in mutation_rows if "待确认" not in row[3]]
    lines = ["# R5 状态写入审计（上游会改状态的方法 vs C# 替换的原语）", "",
             "> 本报告由 `tools/diagnostics/r5_state_mutation_audit.py` 重建，不手写。",
             "> 用途：C# 逐条替换了上游原语，而上游有些方法**顺手写状态**；替换掉就丢了那些写入。",
             "> 只做静态扫描（`inspect.getsource` + 写入模式匹配），不执行游戏动作。", "",
             f"- 注册表原语：**{len(primitives)}** 个",
             f"- 检测到**状态写入**的条目：**{len(mutation_rows)}**（已核对 **{len(reviewed_rows)}**）",
             f"- **待确认**：**{todo}**", "",
             "| 原语 | 写入类型 | 上游源码行 | 结论 |", "| --- | --- | --- | --- |"]
    lines += [f"| `{name}` | {kind} | {line} | {note} |" for name, kind, line, note in rows]
    lines.append("")

    hook_hits, hook_count = hook_mutation_scan()
    lines += ["## 关卡钩子体里的状态写入", "",
              f"用 `ast` 扫了全库 **{hook_count}** 个 `battle_*`/`handle_*` 钩子，"
              f"命中状态写入 **{len(hook_hits)}** 处。", "",
              "口径：钩子体是被导出成**计划**并执行的东西，所以这里的写入**不会**由上游执行；",
              "要么由计划里的原语覆盖，要么必须显式同步（见宿主 `SetGridFlag`）。", ""]
    if hook_hits:
        lines += ["| 模块 | 位置 | 行 |", "| --- | --- | --- |"]
        lines += [f"| `{module}` | `{where}` | {line} |" for module, where, line in hook_hits[:40]]
        if len(hook_hits) > 40:
            lines.append(f"| … | 其余 {len(hook_hits) - 40} 处省略 | |")
        lines.append("")
    lines.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}：{len(primitives)} 个原语 / 待确认 {todo} / "
          f"钩子体状态写入 {len(hook_hits)}（扫了 {hook_count} 个钩子）")
    for name, kind, line, note in rows:
        if "待确认" in note:
            print(f"  - {name} [{kind}] {line}")
    return 1 if todo else 0


if __name__ == "__main__":
    sys.exit(main())
