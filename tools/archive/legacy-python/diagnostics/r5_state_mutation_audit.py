#!/usr/bin/env python3
"""静态审计上游原语和钩子里的状态写入。

只解析上游源文本和 C# 原语注册表，不导入引擎模块，避免可选运行时依赖
（例如 rich）改变审计结果。报告记录源代码事实；它不把静态导出计划说成
生产战役真值，生产路径仍由上游 ``Campaign.run()`` 执行。
"""
from __future__ import annotations

import ast
import os
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
UPSTREAM = Path(os.environ.get("ALAS_REPO") or ROOT / ".runtime" / "engine").resolve()
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-state-mutation-audit.md"
REGISTRY = ROOT / "src" / "Alas.Core" / "Campaign" / "CampaignPrimitives.cs"

UPSTREAM_ALIASES = {"ensure_fleet": "fleet_ensure"}
DEVICE_SIDE = {
    "clear_chosen_enemy", "goto", "submarine_move_near_boss", "focus_to", "withdraw",
    "update_map", "map_swipe", "ensure_edge_insight", "ensure_no_info_bar",
    "clear_chosen_mystery",
}
MUTATING_CALLS = {"append", "extend", "insert", "remove", "pop", "clear", "sort", "reverse",
                  "update", "difference_update", "intersection_update", "symmetric_difference_update",
                  "set", "wipe_out"}


def registry_primitives() -> list[str]:
    text = REGISTRY.read_text(encoding="utf-8")
    names = sorted(set(re.findall(r'\["([a-z_0-9]+)"\]\s*=\s*new CampaignPrimitive\(', text)))
    if not names:
        raise ValueError("原语注册表为空或声明格式已变化，不能完成审计")
    return names


def _source_files() -> list[Path]:
    files = list((UPSTREAM / "module/map").rglob("*.py"))
    files += [UPSTREAM / "module/campaign/campaign_base.py"]
    files += list((UPSTREAM / "campaign").rglob("*.py"))
    return sorted({path for path in files if path.is_file()})


def _owners() -> list[tuple[str, Path, ast.ClassDef]]:
    files = _source_files()
    if not files:
        raise FileNotFoundError("无法找到上游 Map/CampaignBase 源码")
    owners: list[tuple[str, Path, ast.ClassDef]] = []
    for path in files:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for node in ast.walk(tree):
            if isinstance(node, ast.ClassDef):
                owners.append((path.relative_to(UPSTREAM).as_posix(), path, node))
    if not owners:
        raise FileNotFoundError("无法找到上游 Map/CampaignBase 类")
    return owners


def upstream_methods() -> dict[str, list[tuple[tuple[str, int, int], str]]]:
    """All same-name source definitions; this does not resolve runtime MRO."""
    found: dict[str, list[tuple[tuple[str, int, int], str]]] = {}
    owners = _owners()
    for name in registry_primitives():
        candidates = ([UPSTREAM_ALIASES[name]] if name in UPSTREAM_ALIASES else []) + [name]
        for label, path, owner in owners:
            for method in owner.body:
                if (isinstance(method, (ast.FunctionDef, ast.AsyncFunctionDef))
                        and method.name in candidates):
                    found.setdefault(name, []).append(
                        ((str(path), method.lineno, method.end_lineno or method.lineno),
                         f"{label}:{owner.name}:{method.name}"))
    return found


def _method_node(path: str, start: int, end: int) -> ast.AST:
    source = Path(path).read_text(encoding="utf-8")
    tree = ast.parse(source, filename=path)
    for node in ast.walk(tree):
        if (isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.lineno == start
                and node.end_lineno == end):
            return node
    raise RuntimeError("无法读取上游方法源码，不能判定为无状态写入")


def _targets(node: ast.AST) -> list[ast.AST]:
    if isinstance(node, (ast.Tuple, ast.List)):
        return [item for child in node.elts for item in _targets(child)]
    return [node]


def _assignment_hits(node: ast.AST) -> list[tuple[str, str]]:
    targets: list[ast.AST] = []
    if isinstance(node, ast.Assign):
        targets.extend(node.targets)
    elif isinstance(node, (ast.AnnAssign, ast.AugAssign)):
        targets.append(node.target)
    elif isinstance(node, ast.Delete):
        targets.extend(node.targets)
    hits: list[tuple[str, str]] = []
    for target in [item for root in targets for item in _targets(root)]:
        if not isinstance(target, (ast.Attribute, ast.Subscript)):
            continue
        label = ("删除属性/下标" if isinstance(node, ast.Delete)
                 else "增量赋值" if isinstance(node, ast.AugAssign)
                 else "容器下标赋值" if isinstance(target, ast.Subscript) else "属性赋值")
        hits.append((label, ast.unparse(node)))
    return hits


def _body_nodes(method: ast.AST):
    """Nested function/class bodies have a separate execution scope."""
    def walk(node: ast.AST):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef, ast.Lambda)):
            return
        yield node
        for child in ast.iter_child_nodes(node):
            yield from walk(child)
    for statement in method.body:
        yield from walk(statement)


def _mutation_hits(method: ast.AST) -> list[tuple[str, str, int]]:
    hits: list[tuple[str, str, int]] = []
    for node in _body_nodes(method):
        hits.extend((label, snippet, node.lineno) for label, snippet in _assignment_hits(node))
        if isinstance(node, ast.Call):
            if isinstance(node.func, ast.Attribute) and node.func.attr in MUTATING_CALLS:
                hits.append((f"潜在原地修改 .{node.func.attr}", ast.unparse(node), node.lineno))
            elif isinstance(node.func, ast.Name) and node.func.id in {"setattr", "delattr"}:
                hits.append((f"动态属性修改 {node.func.id}", ast.unparse(node), node.lineno))
    return list(dict.fromkeys(hits))


def scan(method) -> list[tuple[str, str]]:
    """Return mutation labels and source snippets; unreadable input is a hard error."""
    if not (isinstance(method, tuple) and len(method) == 3):
        raise RuntimeError("无法读取上游方法源码，不能判定为无状态写入")
    path, start, end = method
    try:
        node = _method_node(path, start, end)
    except (OSError, UnicodeError) as error:
        raise RuntimeError("无法读取上游方法源码，不能判定为无状态写入") from error
    return [(label, f"L{line}: {snippet}") for label, snippet, line in _mutation_hits(node)]


def hook_mutation_scan() -> tuple[list[tuple[str, str, int]], int]:
    """Scan source methods named battle_* / handle_*; no module import."""
    paths = sorted((UPSTREAM / "campaign").rglob("*.py"))
    if not paths:
        raise FileNotFoundError("缺少上游 campaign 源文件，无法完成钩子审计")
    hits: list[tuple[str, str, int]] = []
    hook_count = 0
    for path in paths:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for node in ast.walk(tree):
            if not isinstance(node, ast.ClassDef):
                continue
            for item in node.body:
                if not isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    continue
                if not (item.name.startswith("battle_") or item.name.startswith("handle_")):
                    continue
                hook_count += 1
                for label, snippet, line in _mutation_hits(item):
                    hits.append((path.relative_to(UPSTREAM).as_posix(),
                                 f"{item.name}: {label} `{snippet}`", line))
    if not hook_count:
        raise ValueError("没有找到任何 battle_*/handle_* 方法，不能完成审计")
    return hits, hook_count


def cell(value: object) -> str:
    return str(value).replace("|", "\\|").replace("\n", " ").replace("\r", " ")


def main() -> int:
    if not (UPSTREAM / "module/map/map.py").is_file():
        raise FileNotFoundError("缺少上游地图源码；请准备 .runtime/engine 或指定 ALAS_REPO")
    primitives = registry_primitives()
    methods = upstream_methods()
    rows: list[tuple[str, str, str, str]] = []
    unresolved = 0
    for name in primitives:
        if name in DEVICE_SIDE:
            rows.append((name, "设备侧（本审计不判定 C# 镜像）", "", "需由上游生产路径与设备证据核对"))
            continue
        located = methods.get(name)
        if located is None:
            rows.append((name, "未在上游源文本定位", "", "待确认：可能别名、组合入口或未接线"))
            unresolved += 1
            continue
        for method, owner in located:
            hits = scan(method)
            if not hits:
                rows.append((name, f"源文本未发现直接写入（{owner}）", "", "仍需核对被调用方法的副作用"))
                continue
            for label, line in hits:
                rows.append((name, f"{label}（{owner}）", f"`{line}`",
                             "待确认：静态命中不证明 C# 已同步"))
                unresolved += 1

    hook_hits, hook_count = hook_mutation_scan()
    lines = ["# R5 状态写入审计（源文本事实）", "",
             "> 本报告由 `tools/diagnostics/r5_state_mutation_audit.py` 重建，不手写。",
             "> 只解析上游源文本和 C# 注册表；不导入生产模块、不执行游戏动作。",
             "> 生产战役仍由上游 `Campaign.run()` 执行，静态导出计划仅用于离线展示、溯源和漂移校验。", "",
             "扫描 module/map、module/campaign/campaign_base.py 和 campaign 的同名方法定义，保留所有候选。",
             "这不是运行时 MRO 解析或调用图；`.sort`/`.update` 等只表示潜在原地修改，接收对象可能是局部副本。",
             "未扫描到直接写入不表示无副作用；C# 同步、执行顺序及设备结果须由专项对照证明。", "",
             f"- 注册表原语：**{len(primitives)}** 个",
             f"- 源文本待定位或状态写入待核对：**{unresolved}**",
             "", "| 原语 | 源文本命中 | 片段 | 审计结论 |", "| --- | --- | --- | --- |"]
    lines += ["| " + " | ".join(cell(value) for value in (f"`{name}`", kind, line, note)) + " |"
              for name, kind, line, note in rows]
    lines += ["", "## 关卡方法中的状态写入", "",
              f"用 AST 扫描了 **{hook_count}** 个 `battle_*`/`handle_*` 源代码方法，命中 **{len(hook_hits)}** 处。",
              "这些是上游源代码事实，不代表静态导出计划执行了这些写入，也不构成逐地图迁移结论。", ""]
    if hook_hits:
        lines += ["| 模块 | 方法/写入 | 行 |", "| --- | --- | ---: |"]
        lines += [f"| `{cell(module)}` | {cell(where)} | {line} |" for module, where, line in hook_hits]
    lines.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}：{len(primitives)} 个原语 / 待核对 {unresolved} / "
          f"关卡方法写入 {len(hook_hits)}（扫了 {hook_count} 个方法）")
    print("FAIL: 存在源文本待定位或状态写入待核对项" if unresolved
          else "PASS: 静态扫描完成，未命中需核对项；不证明运行时同步完整")
    return 1 if unresolved else 0


if __name__ == "__main__":
    sys.exit(main())
