#!/usr/bin/env python3
"""R5 决策层全库对拍：C# 的钩子选择 vs **上游自己的 `battle_function`**（离线，无设备）。

用法：
    python tools/diagnostics/r5_decision_sweep.py [--limit N] [--counts 0,1,2,3,4]

为什么这样做：
    `verify_r5_loop.py` 里的期望值是我们**复刻**的选择规则；本脚本换成**上游真实代码**做基准——
    导入关卡模块拿到 `Campaign` 类，把 `battle_function` 绑定到一个替身实例上（钩子换成记录器），
    调用它、看它实际选了哪个钩子，再与 `Alas.Server r5-hooks` 的输出逐点比较。

口径与限制：
  * 只覆盖**默认变体**：`@Config.when(...)` 在**导入时**求值（用的是仓库里的配置），所以
    `MAP_CLEAR_ALL_THIS_TIME=True` / `POOR_MAP_DATA=True` 两个变体不在本对拍范围内（由
    `verify_r5_shadow` / `verify_r5_loop` 的变体用例覆盖）；
  * 替身只提供 `battle_count` / `config` / 钩子方法，不碰设备；上游 `battle_function` 本身只做选择。
只读：不连设备、不改变任何运行状态。
"""
from __future__ import annotations

import argparse
import importlib
import json
import pathlib
import re
import subprocess
import sys
from types import SimpleNamespace

ROOT = pathlib.Path(__file__).resolve().parents[2]
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-decision-sweep.md"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"
UPSTREAM = ROOT / ".runtime" / "engine"


class PermissiveConfig:
    """替身 config：缺的键一律 False。

    `@Config.when(...)` 包了一层 wrapper，**调用时**会读 `self.config.<键>`（`module/base/decorator.py`），
    而且是**显式调用 `self.config.__getattribute__(key)`**——这条路径**不会**回退到 `__getattr__`
    （普通属性访问才会；`SimpleNamespace` 同理，实测踩过两次）。所以只能重写 `__getattribute__` 本身。
    """

    def __init__(self, **kwargs):
        object.__setattr__(self, "_values", dict(kwargs))

    def __getattribute__(self, name: str):
        values = object.__getattribute__(self, "_values")
        if name in values:
            return values[name]
        if name.startswith("_"):
            return object.__getattribute__(self, name)
        return False


class PermissiveInstance:
    """替身关卡实例：未知属性一律给"可调用的假对象"。

    关卡自己的 `battle_function` 覆写常常顺手读别的属性（`self.MAP`、`self.fleet_2_location` …），
    替身不必理解它们的语义——只要不炸就行；选择结果只看**它调用了哪个钩子**。
    `battle_count` / `config` / 钩子由调用方显式设置。
    """

    def __init__(self, **kwargs):
        object.__setattr__(self, "_values", dict(kwargs))

    def __getattribute__(self, name: str):
        # 上游 `battle_function` 用 `self.__getattribute__(func)` **显式调用**（绕过 `__getattr__`），
        # 所以这里必须自己实现；同时**不能**把 `battle_<数字>` 也变成"永远存在"——
        # 回看逻辑靠 `hasattr(self, 'battle_N')` 判断真实钩子，全都存在会让它退化成 battle_count 本身。
        values = object.__getattribute__(self, "_values")
        if name in values:
            return values[name]
        if name.startswith("_"):
            return object.__getattribute__(self, name)
        # 回看会问 `battle_-1` / `battle_-2`（`battle_count - extra`），这些也必须报"不存在"，
        # 否则 `hasattr` 永远为真，选择会退化成 battle_count 本身（实测踩过）
        if re.fullmatch(r"battle_-?\d+", name):
            raise AttributeError(name)
        return self.__getattr__(name)

    def __getattr__(self, name: str):
        values = object.__getattribute__(self, "_values")
        if name in values:
            return values[name]
        if re.fullmatch(r"battle_-?\d+", name):
            raise AttributeError(name)

        def _fake(*_args, **_kwargs):
            return None

        _fake.__name__ = name
        return _fake


def sanitize(text: str) -> str:
    """报告里**不能出现本机绝对路径**（Python 的 ImportError 消息自带模块文件路径，实测被隐私检查抓到）。

    统一替换成本仓库的相对说法；再兜一层"盘符:\\\\ 开头"的通用清洗。
    """
    text = text.replace(str(UPSTREAM), "<engine>").replace(str(ROOT), "<repo>")
    return re.sub(r"[A-Za-z]:\\[^\s)']*", "<path>", text)


def upstream_choice(folder: str, name: str, battle_count: int) -> str | None:
    """调上游真实的 `battle_function`，返回它选中的钩子名（选不了返回 None）。"""
    module = importlib.import_module(f"campaign.{folder}.{name}")
    cls = getattr(module, "Campaign", None)
    if not isinstance(cls, type):
        # 关卡家族的**基类模块**（如 `campaign_14_base`）只有 Config，没有 Campaign 类——不是关卡
        raise LookupError("该模块没有 `Campaign` 类（关卡家族基类，不是关卡）")
    hooks = {attr for klass in cls.__mro__ for attr, value in vars(klass).items()
             if attr.startswith("battle_") and callable(value)
             and attr not in ("battle_function", "battle_status_click_interval")}
    if not any(re.fullmatch(r"battle_\d+", hook) for hook in hooks):
        # 章节基类（如 `campaign_hard/campaign_hard`）没有出击钩子，不是关卡——由调用方跳过
        raise LookupError("该模块没有定义 `battle_<数字>` 钩子（章节基类，不是关卡）")

    # **真实实例**（`__new__` 不走 `__init__`，不碰设备）：关卡覆写里会用 `super().battle_function()`
    # 这类写法，替身对象过不了 `super()` 的类型检查（实测：`super(type, obj): obj ... is not an instance`）。
    instance = cls.__new__(cls)
    recorded: dict[str, str] = {}
    # 只把**类上定义的函数**当钩子：`battle_count` / `battle_status_click_interval` 这类属性
    # 也以 `battle_` 开头，装成记录器会把 `battle_count - extra_battle` 弄炸（实测踩过）。
    for hook in hooks:
        setattr(instance, hook, (lambda hook=hook: recorded.setdefault("hook", hook) or True))
    setattr(instance, "FUNCTION_NAME_BASE", "battle_")
    setattr(instance, "battle_count", battle_count)
    # `config` 用宽松替身：`@Config.when` 的 wrapper 在**调用时**读 `self.config.<键>`，
    # 而且是显式 `self.config.__getattribute__(key)`——那条路径不回退 `__getattr__`，
    # 所以替身必须自己实现 `__getattribute__`。
    setattr(instance, "config", PermissiveConfig(Error_HandleError=False))
    setattr(instance, "_decision_sweep_config", True)
    # `battle_function` 按 MRO 取（关卡覆写优先，没有覆写即 CampaignBase 的默认变体）
    bound = cls.battle_function.__get__(instance, cls)
    bound()
    return recorded.get("hook")


def csharp_choices(chapter: str, level: str, counts: list[int]) -> dict[int, str]:
    completed = subprocess.run(
        [str(SERVER), "r5-hooks", "--chapter", chapter, "--level", level,
         "--battle-counts", ",".join(str(value) for value in counts), "--json"],
        cwd=ROOT, capture_output=True, text=True, timeout=120, encoding="utf-8", errors="replace")
    text = completed.stdout + completed.stderr
    start = text.find("{")
    if start < 0:
        return {}
    payload = json.loads(text[start:])
    return {row["battle_count"]: row["hook"] for row in payload["hooks"]}


def main() -> int:
    parser = argparse.ArgumentParser(description="R5 决策层全库对拍")
    parser.add_argument("--limit", type=int, default=0, help="只跑前 N 关（0 = 全部）")
    parser.add_argument("--counts", default="0,1,2,3,4")
    options = parser.parse_args()
    counts = [int(token) for token in options.counts.split(",")]

    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER.relative_to(ROOT)}；先运行 ./build.ps1 构建")
    if str(UPSTREAM) not in sys.path:
        sys.path.insert(0, str(UPSTREAM))

    levels: list[tuple[str, str]] = []
    for path in sorted((ROOT / "data" / "campaign").rglob("*.json")):
        parts = path.relative_to(ROOT / "data" / "campaign").parts
        if len(parts) == 2:
            levels.append((parts[0], path.stem))
    if options.limit:
        levels = levels[:options.limit]

    compared = matched = 0
    mismatches: list[tuple[str, int, str, str]] = []
    skipped: dict[str, int] = {}
    examples: dict[str, list[str]] = {}
    for folder, name in levels:
        csharp = csharp_choices(folder, name, counts)
        if not csharp:
            skipped["关卡计划读不出来"] = skipped.get("关卡计划读不出来", 0) + 1
            continue
        for battle_count in counts:
            try:
                expected = upstream_choice(folder, name, battle_count)
            except Exception as error:                  # noqa: BLE001 —— 上游跑不动就如实记，不算通过
                key = (str(error) if isinstance(error, LookupError) and not isinstance(error, (IndexError, KeyError))
                       else f"上游导入/调用失败：{type(error).__name__}")
                skipped[key] = skipped.get(key, 0) + 1
                bucket = examples.setdefault(key, [])
                if len(bucket) < 3:
                    bucket.append(sanitize(f"{folder}/{name} battle_count={battle_count}：{error}"))
                continue
            actual = csharp.get(battle_count)
            compared += 1
            if expected == actual:
                matched += 1
            else:
                mismatches.append((f"{folder}/{name}", battle_count, expected, actual))

    lines = ["# R5 决策层全库对拍（C# 钩子选择 vs 上游 `battle_function`）", "",
             "> 本报告由 `tools/diagnostics/r5_decision_sweep.py` 重建，不手写。",
             "> 基准是**上游真实代码**：导入关卡模块、把 `battle_function` 绑到替身实例上调用，看它选了哪个钩子。",
             "> 只覆盖**默认变体**（`@Config.when` 在导入时求值，仓库配置下即默认变体）；两个变体由 shadow/loop 用例覆盖。", "",
             f"- 关卡数：**{len(levels)}**",
             f"- 逐点比较：**{compared}** 次（每关 battle_count ∈ {counts}）",
             f"- 一致：**{matched}**；不一致：**{len(mismatches)}**", ""]
    if mismatches:
        lines += ["## 不一致（前 20 条）", "", "| 关卡 | battle_count | 上游 | C# |", "| --- | --- | --- | --- |"]
        lines += [f"| {level} | {count} | `{expected}` | `{actual}` |" for level, count, expected, actual in mismatches[:20]]
        lines.append("")
    if skipped:
        lines += ["## 跳过（如实列出原因，不当作通过）", "", "| 原因 | 次数 |", "| --- | --- |"]
        lines += [f"| {reason} | {count} |" for reason, count in sorted(skipped.items())]
        lines.append("")
        lines += ["### 例子（每类最多 3 条）", ""]
        for reason, items in examples.items():
            lines.append(f"- **{reason}**")
            lines += [f"  - `{item}`" for item in items]
        lines.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    # 兜底：整份报告再过一次脱敏，避免将来有别的消息带出本机路径
    REPORT.write_text(sanitize("\n".join(lines)), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}：{len(levels)} 关 / {compared} 次比较 / 不一致 {len(mismatches)}")
    for item in mismatches[:5]:
        print("  -", item)
    return 1 if mismatches else 0


if __name__ == "__main__":
    sys.exit(main())
