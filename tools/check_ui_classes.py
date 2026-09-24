#!/usr/bin/env python3
"""检查界面类名是否有对应样式，防止"照抄了上游类名、却忘记写样式"。

未定义的类名不会导致 XAML 编译失败，却会丢失字号、最小高度、圆角等样式。
本检查覆盖 AXAML 中的静态类名及状态类名；不替代视觉对照或动态控件检查。

判定规则：
1. 视图里 `Classes="a b"` 与 `Classes.x` 出现的每个类名都要有解释；
2. 有解释 = 满足其一：
   - 别的 axaml 里有 `Selector="...".类名`（含伪类/复合选择器）；
   - 该元素同时带 `Theme="{StaticResource ...}"`（样式由 ControlTheme 承担）；
   - 列在下面的 ALLOWED 里，并写明理由（自建标记类等）。
退出码非 0 表示存在未解释的类名。
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# 自建标记类：上游没有对应类，也没有样式语义（仅用于定位/断言），逐个写明理由。
ALLOWED = {
    "nav-item": "自建标记类：断言用它定位侧栏项（样式由 NavItemTheme 承担）",
    "rail-count-badge": "自建标记类：徽标样式直接写在元素上（对齐上游 .count-badge）",
    "monitor-action": "自建标记类：日志工具按钮（样式由 IconButtonTheme 承担）",
    "segment-tab": "自建标记类：分段控件页签（样式由 SegmentTabTheme 承担）",
    "field-row": "自建标记类：设置页字段行（样式在本地 Styles 中定义）",
    "palette-swatch": "样式直接写在元素上（内边距/圆角/固定宽），对齐上游 .palette-swatch",
    "heading": "离线预览工具自己的标题类，样式写在同文件的 UserControl.Styles 里",
    "home-main": "HomeView 分区标记，几何由 Grid 定义",
    "home-deck": "HomeView 分区标记，几何由 Grid 定义",
    "home-editorial": "HomeView 分区标记，几何由 Grid 定义",
    "topbar-actions": "悬停展开由 TopBarView 属性和控件主题处理",
}

CLASS_USE = re.compile(r'Classes="([^"]+)"')
CLASS_STATE = re.compile(r"Classes\.([A-Za-z0-9_-]+)")
SELECTOR = re.compile(r'Selector="([^"]+)"')
SELECTOR_CLASS = re.compile(r"\.([A-Za-z0-9_-]+)")
THEME = re.compile(r'Theme="\{StaticResource')


def collect(root: Path) -> tuple[dict[str, set[str]], dict[str, set[str]]]:
    used: dict[str, set[str]] = {}
    styled: dict[str, set[str]] = {}
    for path in sorted(root.rglob("*.axaml")):
        text = path.read_text(encoding="utf-8", errors="replace")
        for line in text.splitlines():
            themed = bool(THEME.search(line))
            for match in CLASS_USE.finditer(line):
                for name in match.group(1).split():
                    if name:
                        bucket = used.setdefault(name, set())
                        bucket.add(path.name)
                        if themed:
                            styled.setdefault(name, set()).add(f"{path.name}（Theme=）")
            for match in CLASS_STATE.finditer(line):
                used.setdefault(match.group(1), set()).add(path.name)
        for match in SELECTOR.finditer(text):
            for name in SELECTOR_CLASS.finditer(match.group(1)):
                styled.setdefault(name.group(1), set()).add(path.name)
    return used, styled


def main() -> int:
    root = Path(__file__).resolve().parents[1] / "src" / "Alas.UI"
    if not root.is_dir():
        print(f"找不到界面源码目录：{root}")
        return 2
    used, styled = collect(root)
    unexplained = sorted(name for name in used if name not in styled and name not in ALLOWED)
    print(f"类名检查：使用 {len(used)} 个，有样式或已解释 {len(used) - len(unexplained)} 个")
    if unexplained:
        print("以下类名没有任何样式，也不在白名单里（元素会按默认样式渲染）：")
        for name in unexplained:
            print(f"  - {name}（用于 {', '.join(sorted(used[name]))}）")
        return 1
    print("全部类名都有样式定义或已说明理由 (OK)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
