#!/usr/bin/env python3
"""R5 上游引擎迁移审计（P0）：只做 AST 解析与静态计数，不导入游戏代码、不连设备。

用法：
    python tools/diagnostics/r5_upstream_audit.py                # 重建报告
    python tools/diagnostics/r5_upstream_audit.py --print        # 同时打印摘要

产出：docs/archive/reports/r5-upstream-migration.md（由本脚本重建，不手写）

审计三件事：
  A. 上游 campaign/ 覆写方法的形态与"可原语化"程度（hooks × 形态 × 复杂度 × helper 调用）
  B. IVisionEngine 接口面与 Core 调用点的初判归属（识图保留 / 可静态化 / 必须自研 / 设备帧）
  C. 上游重依赖的用法面（哪些子系统被 cv2/numpy/scipy/adbutils/uiautomator2 等绑住）

上游目录由 ALAS_REPO 指定，默认 <仓库根>/.runtime/engine；缺失时本脚本报错退出（不静默跳过）。
"""
from __future__ import annotations

import argparse
import ast
import collections
import os
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-upstream-migration.md"

# IVisionEngine 方法的初判归属。识图类按既定目标不要求 C# 重写；
# 这里只给出"替换策略"，供评审，不代表已决定实现方式。
VISION_CLASSES: dict[str, tuple[str, str]] = {
    "Ping": ("设备帧", "保留：宿主探活，随识图通道一起保留"),
    "SetServer": ("可静态化", "静态：服务器变体来自配置"),
    "LoadScreenshot": ("设备帧", "保留：帧输入管道，属识图前置"),
    "SetScreenshot": ("设备帧", "保留：帧输入管道，属识图前置"),
    "ScaleScreenshot": ("设备帧", "可自研：纯几何缩放，但属识图前置，优先级低"),
    "PageList": ("可静态化", "静态：页面清单可由上游页面图导出"),
    "PageCurrent": ("识图保留", "保留：依赖素材匹配判断当前页面"),
    "PageGraph": ("可静态化", "静态：页面关系（Page.links）可导出为数据"),
    "AssetButtonCenter": ("识图保留", "保留：素材坐标由上游素材对象解析"),
    "PageAppear": ("识图保留", "保留：页面出现判定依赖素材匹配"),
    "AppearOn": ("识图保留", "保留：模板/颜色匹配"),
    "AppearOnBatch": ("识图保留", "保留：批量匹配"),
    "ButtonMatch": ("识图保留", "保留：按钮匹配"),
    "TemplateMatch": ("识图保留", "保留：模板匹配"),
    "Ocr": ("识图保留", "保留：OCR 模型推理"),
    "AccountState": ("必须自研", "自研：账号状态读屏后的语义与状态机"),
    "TaskCatalog": ("可静态化", "静态：任务目录/参数 schema 可导出"),
    "StatisticsReport": ("必须自研", "自研：统计口径与计算（依赖静态采集数据）"),
    "RefreshStatisticsLoot": ("必须自研", "自研：掉落统计刷新（依赖未迁移的采集链）"),
    "MeowfficerReport": ("必须自研", "自研：指挥喵报告计算"),
    "ClearMeowfficerReport": ("必须自研", "自研：报告清理"),
    "ValidateShopStrategy": ("必须自研", "自研：策略脚本校验（需与上游语义对拍）"),
    "ConfigureDevice": ("设备帧", "可自研：设备配置（ADB 传输层已有 C# 骨架）"),
    "CaptureViaEngine": ("设备帧", "可自研：抓帧路径（可用 C# 第三方库）"),
    "RunCampaignPlan": ("必须自研", "自研：**重写核心目标**——关卡流程执行（当前由上游 Campaign.run() 承担）"),
}

HEAVY_LIBS = ("cv2", "scipy", "numpy", "adbutils", "uiautomator2", "jellyfish", "lz4", "imageio", "matplotlib")


def upstream_root() -> pathlib.Path:
    configured = os.environ.get("ALAS_REPO")
    path = pathlib.Path(configured) if configured else ROOT / ".runtime" / "engine"
    if not (path / "campaign").is_dir() or not (path / "module").is_dir():
        raise SystemExit(
            f"找不到上游 ALAS 检出（需要 campaign/ 与 module/）：{path}\n"
            "用 ALAS_REPO 指定上游目录，例如：$env:ALAS_REPO='<上游仓库>'"
        )
    return path


def classify_body(node: ast.FunctionDef) -> tuple[str, str | None]:
    """把方法体归类，用于判断"薄覆写 vs 真实逻辑"：

    return_self   单条 `return self.<helper>(...)`     —— 等价于"选哪个原语"
    return_super  单条 `return super().<helper>(...)`  —— 委托父类
    return_other  单条 `return <其它表达式>.<attr>(...)`（如 `self.map.foo()`）
    call_only     单条裸调用 `self.<helper>(...)`
    assign/pass   单条赋值 / 空实现
    logic         其余（真实方法体）
    """
    body = [n for n in node.body if not (isinstance(n, ast.Expr) and isinstance(n.value, ast.Constant))]
    if len(body) != 1:
        return "logic", None
    only = body[0]
    if isinstance(only, ast.Pass):
        return "pass", None
    if isinstance(only, ast.Assign):
        return "assign", None
    call = None
    if isinstance(only, ast.Return) and isinstance(only.value, ast.Call):
        call = only.value
    elif isinstance(only, ast.Expr) and isinstance(only.value, ast.Call):
        call = only.value
    if call is None:
        return "logic", None
    fn = call.func
    if not isinstance(fn, ast.Attribute):
        return "logic", None
    owner = fn.value
    if isinstance(owner, ast.Name) and owner.id == "self":
        return ("return_self" if isinstance(only, ast.Return) else "call_only"), fn.attr
    if isinstance(owner, ast.Call) and isinstance(owner.func, ast.Name) and owner.func.id == "super":
        return "return_super", fn.attr
    if isinstance(only, ast.Return):
        return "return_other", fn.attr
    return "logic", None


def primitive_family(name: str) -> str:
    """按前缀把 helper 归入原语族，用于估算原语数量。"""
    for prefix in ("clear_", "fleet_", "campaign_", "ui_", "map_", "handle_", "check_", "pick_",
                   "goto", "appear", "combat_", "battle_", "get_", "is_", "set_", "on_", "_"):
        if name.startswith(prefix):
            return prefix.rstrip("_")
    return "其它"


def audit_campaign(engine: pathlib.Path) -> dict:
    files = sorted((engine / "campaign").rglob("*.py"))
    hooks: dict[str, collections.Counter] = collections.defaultdict(collections.Counter)
    shapes: collections.Counter = collections.Counter()
    bases: collections.Counter = collections.Counter()
    delegates: collections.Counter = collections.Counter()
    helper_calls: collections.Counter = collections.Counter()
    statements: collections.Counter = collections.Counter()
    lines: list[int] = []
    classes = methods = config_attrs = module_assigns = failures = 0
    per_file: list[int] = []

    for path in files:
        try:
            tree = ast.parse(path.read_text(encoding="utf-8", errors="replace"))
        except SyntaxError:
            failures += 1
            continue
        count = 0
        for node in tree.body:
            if isinstance(node, ast.Assign):
                module_assigns += 1
            if not isinstance(node, ast.ClassDef):
                continue
            classes += 1
            for base in node.bases:
                bases[ast.unparse(base).split(".")[-1]] += 1
            for item in node.body:
                if isinstance(item, ast.Assign) and node.name == "Config":
                    config_attrs += 1
                if not isinstance(item, ast.FunctionDef):
                    continue
                methods += 1
                count += 1
                shape, target = classify_body(item)
                shapes[shape] += 1
                hooks[item.name][shape] += 1
                if shape in ("return_self", "call_only") and target:
                    delegates[target] += 1
                if shape == "logic":
                    body = [n for n in item.body
                            if not (isinstance(n, ast.Expr) and isinstance(n.value, ast.Constant))]
                    bucket = "1-2" if len(body) <= 2 else "3-5" if len(body) <= 5 \
                        else "6-10" if len(body) <= 10 else ">10"
                    statements[bucket] += 1
                    lines.append((item.end_lineno or item.lineno) - item.lineno + 1)
                    for call in (n for n in ast.walk(item) if isinstance(n, ast.Call)):
                        fn = call.func
                        if isinstance(fn, ast.Attribute) and isinstance(fn.value, ast.Name) \
                                and fn.value.id == "self":
                            helper_calls[fn.attr] += 1
        per_file.append(count)

    lines.sort()
    families: collections.Counter = collections.Counter()
    for name, count in helper_calls.items():
        families[primitive_family(name)] += count
    families_by_name: collections.Counter = collections.Counter()
    for name in helper_calls:
        families_by_name[primitive_family(name)] += 1

    return {
        "files": len(files), "failures": failures, "classes": classes, "methods": methods,
        "config_attrs": config_attrs, "module_assigns": module_assigns, "hooks": hooks,
        "shapes": shapes, "bases": bases, "delegates": delegates, "helper_calls": helper_calls,
        "statements": statements, "lines": lines, "per_file": per_file,
        "families": families, "families_by_name": families_by_name,
    }


def audit_vision() -> tuple[list[tuple[str, str]], list[tuple[str, str, int]]]:
    """接口方法清单 + Core 内调用点计数。只解析 IVisionEngine 接口块本身。"""
    source = (ROOT / "src" / "Alas.Core" / "Vision" / "IVisionEngine.cs").read_text(encoding="utf-8")
    start = source.find("public interface IVisionEngine")
    if start < 0:
        raise SystemExit("IVisionEngine.cs 里找不到 interface IVisionEngine 定义")
    end = source.find("\n}", start)
    block = source[start:end if end > start else len(source)]
    methods: list[tuple[str, str]] = []
    seen: set[str] = set()
    for match in re.finditer(r"^\s{4}(?:Task<[^>]+>|[\w<>?\[\]\. ]+?)\s+(\w+)\(", block, re.M):
        name = match.group(1)
        if name in seen:
            continue
        seen.add(name)
        methods.append((name, VISION_CLASSES.get(name, ("未分类", "待补"))))
    calls: list[tuple[str, str, int]] = []
    receivers = r"(?:_vision|Vision|vision|_engine|Engine)"
    for path in sorted((ROOT / "src" / "Alas.Core").rglob("*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        hit = 0
        for name, _ in methods:
            hit += len(re.findall(rf"{receivers}\.{name}\(", text))
        if hit:
            calls.append((str(path.relative_to(ROOT)), "", hit))
    return methods, calls


def audit_libraries(engine: pathlib.Path) -> tuple[dict[str, int], dict[str, dict[str, int]]]:
    totals: dict[str, int] = {}
    per_lib: dict[str, dict[str, int]] = {}
    for lib in HEAVY_LIBS:
        files = 0
        subs: collections.Counter = collections.Counter()
        for path in (engine / "module").rglob("*.py"):
            text = path.read_text(encoding="utf-8", errors="replace")
            if re.search(rf"^\s*(import|from)\s+{lib}\b", text, re.M):
                files += 1
                subs["/".join(path.relative_to(engine).parts[:2])] += 1
        totals[lib] = files
        per_lib[lib] = dict(subs.most_common(6))
    return totals, per_lib


def render(engine: pathlib.Path) -> str:
    campaign = audit_campaign(engine)
    methods, calls = audit_vision()
    lib_totals, lib_subs = audit_libraries(engine)

    out: list[str] = []
    add = out.append
    add("# R5 上游引擎迁移审计（P0）")
    add("")
    add("> 本报告由 `tools/diagnostics/r5_upstream_audit.py` 重建，不手写；只做 AST 解析与静态计数，")
    add("> 不导入游戏代码、不连设备。目标与流程见 [上游引擎重写](../../../UPSTREAM-ENGINE-REWRITE.md)。")
    add("")
    add(f"上游检出：`campaign/` {campaign['files']} 个文件（解析失败 {campaign['failures']}）")
    add("")

    add("## A. 关卡覆写：形态与可原语化程度")
    add("")
    add(f"- 类 **{campaign['classes']}** 个，方法 **{campaign['methods']}** 个，`Config` 属性 **{campaign['config_attrs']}** 个，模块级声明赋值 **{campaign['module_assigns']}** 个")
    per_file = sorted(campaign["per_file"])
    add(f"- 每文件方法数：中位 **{per_file[len(per_file) // 2]}**，最多 {per_file[-1]}")
    add(f"- **不同方法名仅 {len(campaign['hooks'])} 个** ← 覆写面是\"少数钩子 × 大量关卡\"，不是 {campaign['methods']} 种不同逻辑")
    add("")
    add("### 方法体形态")
    add("")
    add("| 形态 | 数量 | 占比 | 含义 |")
    add("| --- | --- | --- | --- |")
    labels = {
        "return_self": "单条 `return self.<helper>()`，等价于\"选哪个原语\"",
        "call_only": "单条裸调用 `self.<helper>()`，同上",
        "return_super": "单条 `return super().x()`，委托父类",
        "return_other": "单条 `return <子对象>.<方法>()`（如 `self.map.…`），按原语处理",
        "logic": "真实方法体（见下方复杂度分布）",
        "assign": "单条赋值",
        "pass": "空实现",
    }
    total = sum(campaign["shapes"].values()) or 1
    for shape, count in campaign["shapes"].most_common():
        add(f"| `{shape}` | {count} | {count / total:.1%} | {labels.get(shape, '')} |")
    add("")
    add("### 薄覆写的目标 helper（`return_self` + `call_only`，全部）")
    add("")
    add("| 目标 | 次数 |")
    add("| --- | --- |")
    for name, count in campaign["delegates"].most_common():
        add(f"| `{name}` | {count} |")
    add("")
    add("### `logic` 方法的复杂度")
    add("")
    add("| 语句数 | 方法数 |")
    add("| --- | --- |")
    for bucket in ("1-2", "3-5", "6-10", ">10"):
        add(f"| {bucket} | {campaign['statements'][bucket]} |")
    lines = campaign["lines"]
    if lines:
        add("")
        add(f"行数：中位 {lines[len(lines) // 2]}，P90 {lines[int(len(lines) * 0.9)]}，最多 {lines[-1]}")
    add("")
    add("### `logic` 方法调用的 helper（原语候选）")
    add("")
    add("| helper | 调用次数 | 归入原语族 |")
    add("| --- | --- | --- |")
    for name, count in campaign["helper_calls"].most_common(25):
        add(f"| `{name}` | {count} | {primitive_family(name)} |")
    add("")
    add(f"不同 helper 总数 **{len(campaign['helper_calls'])}**；按前缀归族后：")
    add("")
    add("| 原语族 | 不同 helper 数 | 调用次数 |")
    add("| --- | --- | --- |")
    for family, count in campaign["families"].most_common():
        add(f"| `{family}` | {campaign['families_by_name'][family]} | {count} |")
    add("")
    add("### 按钩子看形态（Top 12）")
    add("")
    add("| 钩子 | 合计 | 形态分布 |")
    add("| --- | --- | --- |")
    for name, counter in sorted(campaign["hooks"].items(), key=lambda kv: -sum(kv[1].values()))[:12]:
        parts = "，".join(f"{k}={v}" for k, v in counter.most_common())
        add(f"| `{name}` | {sum(counter.values())} | {parts} |")
    add("")
    add("### 类基类分布")
    add("")
    add("| 基类 | 次数 |")
    add("| --- | --- |")
    for name, count in campaign["bases"].most_common(8):
        add(f"| `{name}` | {count} |")
    add("")

    add("## B. `IVisionEngine` 接口面与初判归属")
    add("")
    add(f"接口方法 **{len(methods)}** 个（初判仅供评审）：")
    add("")
    add("| 方法 | 初判 | 说明 |")
    add("| --- | --- | --- |")
    for name, (kind, why) in methods:
        add(f"| `{name}` | {kind} | {why} |")
    add("")
    add("Core 内调用点分布（按接口方法名 + 视觉宿主接收者统计，含实现类内部转发）：")
    add("")
    add("| 文件 | 调用次数 |")
    add("| --- | --- |")
    for path, _, count in sorted(calls, key=lambda item: -item[2]):
        add(f"| `{path}` | {count} |")
    add(f"| **合计** | **{sum(item[2] for item in calls)}** |")
    add("")

    add("## C. 上游重依赖的用法面（按子系统）")
    add("")
    add("> 统计范围：上游 `module/**`（不含 `campaign/**` 与 `deploy/**`）；计数为\"导入该库的文件数\"。")
    add("")
    add("| 依赖 | 涉及文件数 | 主要子系统 |")
    add("| --- | --- | --- |")
    for lib, files in sorted(lib_totals.items(), key=lambda kv: -kv[1]):
        subs = "、".join(f"`{k}`({v})" for k, v in lib_subs[lib].items()) or "—"
        add(f"| `{lib}` | {files} | {subs} |")
    add("")
    add("> 识别用途与逻辑用途需按文件逐一区分：识别用途按目标不要求 C# 重写；")
    add("> 逻辑用途（设备输入、截图处理、字符串相似度等）在允许使用 C# 第三方库的前提下可逐个替代。")
    add("")
    return "\n".join(out)


def main() -> int:
    parser = argparse.ArgumentParser(description="R5 上游引擎迁移审计（P0）")
    parser.add_argument("--print", action="store_true", dest="echo", help="同时打印报告到标准输出")
    args = parser.parse_args()
    engine = upstream_root()
    report = render(engine)
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text(report + "\n", encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}（{len(report.splitlines())} 行）")
    if args.echo:
        print(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
