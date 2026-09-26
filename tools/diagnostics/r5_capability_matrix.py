#!/usr/bin/env python3
"""能力归属矩阵：逐 `IVisionEngine` 方法标注归属，量化"只依赖上游静态规则"还差什么。

`docs/architecture-notes.md` 的增量纪律第 3 条要求维护这张矩阵，但一直没落成文件。
本脚本把它**生成**出来（不手写），并做两件事：
  1. 列出接口方法 + Core 里的调用点数量（事实部分，自动统计）；
  2. 与 `OWNERSHIP` 表对照：分类是**人工复核**的结论，新方法没分类就失败（棘轮）。

分类口径（沿用文档里的说法）：
  * `识图保留`：运行时必须借上游视觉/识别（模板匹配、OCR、页面出现判定、设备抓帧）；
  * `可静态化`：数据本身来自上游**静态规则/配置**，导出后自研引擎可自己消费；
  * `必须自研`：目前只在 Python 侧实现的业务逻辑，替换宿主时要自己写；
  * `设备帧`：设备控制/截图通道，替换宿主时换实现但不换语义。

用法：python tools/diagnostics/r5_capability_matrix.py   # 重建报告并做棘轮检查
只读：不连设备、不改仓库数据。
"""
from __future__ import annotations

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
INTERFACE = ROOT / "src" / "Alas.Core" / "Vision" / "IVisionEngine.cs"
REPORT = ROOT / "docs" / "archive" / "reports" / "capability-ownership.md"

# 归属（人工复核）：方法名 → (分类, 说明)
OWNERSHIP: dict[str, tuple[str, str]] = {
    "Ping": ("设备帧", "宿主存活探测；自研宿主后由自己的运行时替代"),
    "SetServer": ("可静态化", "服务器变体本就在 `data/assets.json` 里，可静态选择"),
    "LoadScreenshot": ("设备帧", "从设备/存盘取帧；替换时自己实现截图通道"),
    "SetScreenshot": ("设备帧", "把已有帧交给宿主（夹具与回放用）"),
    "ScaleScreenshot": ("设备帧", "帧缩放，属识图预处理"),
    "PageList": ("可静态化", "页面清单来自上游页面定义，可导出为静态规则"),
    "PageCurrent": ("识图保留", "当前页判定要跑上游 `ui_page_appear`"),
    "PageGraph": ("可静态化", "页面跳转关系来自上游 `Page.links`，可导出"),
    "AssetButtonCenter": ("可静态化", "按钮坐标来自上游素材绑定，已在 `data/assets.json`"),
    "PageAppear": ("识图保留", "页面出现判定，必须借上游模板匹配"),
    "AppearOn": ("识图保留", "素材出现判定，同上"),
    "AppearOnBatch": ("识图保留", "批量出现判定，同上"),
    "ButtonMatch": ("识图保留", "按钮匹配"),
    "TemplateMatch": ("识图保留", "模板匹配"),
    "Ocr": ("识图保留", "OCR（上游字体/词表）"),
    "AccountState": ("识图保留", "账号状态要读界面（资源/石油等）"),
    "TaskCatalog": ("可静态化", "任务目录来自上游 `task/` 配置，可导出"),
    "StatisticsReport": ("必须自研", "统计报表目前只在 Python 侧实现"),
    "RefreshStatisticsLoot": ("必须自研", "同上（含资源识别后的写入）"),
    "MeowfficerReport": ("必须自研", "指挥喵报表同上"),
    "ClearMeowfficerReport": ("必须自研", "同上"),
    "ValidateShopStrategy": ("必须自研", "商店策略校验同上"),
    "ConfigureDevice": ("设备帧", "设备后端配置"),
    "CaptureViaEngine": ("设备帧", "抓帧通道"),
    "RunCampaignPlan": ("必须自研", "S3 生产路径（上游 `Campaign.run()`）——**这正是 R5 要替换的目标**"),
}


# 静态数据现状（人工核对 `data/` 下的导出文件；第三阶段的输入）
#   已导出：已经有对应导出文件，将来自研引擎可以直接消费
#   未导出：数据在上游代码里，但还没导出——第三阶段要先补导出器
STATIC_DATA: dict[str, str] = {
    "SetServer": "已导出（`data/assets.json` 的 `servers`）",
    "AssetButtonCenter": "已导出（`data/assets.json` 的 `assets` 绑定）",
    "PageList": "已导出（`data/pages.json` 的页面清单与校验按钮）",
    "PageGraph": "已导出（`data/pages.json` 的 127 条 `link` 边）",
    "TaskCatalog": "**未导出**（上游 `task/` 配置还没导出）",
}


def interface_methods() -> list[str]:
    text = INTERFACE.read_text(encoding="utf-8")
    text = re.sub(r"///.*", "", text)
    text = re.sub(r"//.*", "", text)
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    start = text.index("interface IVisionEngine")
    depth, index = 0, text.index("{", start)
    end = index
    for position in range(index, len(text)):
        if text[position] == "{":
            depth += 1
        elif text[position] == "}":
            depth -= 1
            if depth == 0:
                end = position
                break
    block = re.sub(r"\s+", " ", text[index + 1:end])
    names = []
    for match in re.finditer(r"([A-Za-z_][\w<>?\[\],\.\s]*?)\s+([A-Z]\w*)\s*\(([^;]*?)\)\s*;", block):
        if match.group(2) not in {"get", "set"}:
            names.append(match.group(2))
    return names


def call_sites(name: str) -> tuple[int, int]:
    core = ROOT / "src" / "Alas.Core"
    count, files = 0, set()
    for path in core.rglob("*.cs"):
        if path == INTERFACE:
            continue
        hits = len(re.findall(rf"\.{re.escape(name)}\s*\(", path.read_text(encoding="utf-8")))
        if hits:
            count += hits
            files.add(path.name)
    return count, len(files)


def main() -> int:
    if not INTERFACE.is_file():
        raise SystemExit(f"缺少 {INTERFACE.relative_to(ROOT)}")
    methods = interface_methods()
    rows = []
    total_sites = 0
    unclassified = []
    for name in methods:
        count, files = call_sites(name)
        total_sites += count
        if name not in OWNERSHIP:
            unclassified.append(name)
            rows.append((name, "**未分类**", count, files, "新方法没有归属结论——先分类再合入"))
            continue
        category, reason = OWNERSHIP[name]
        rows.append((name, category, count, files, reason))

    by_category: dict[str, int] = {}
    for _, category, *_ in rows:
        by_category[category] = by_category.get(category, 0) + 1

    lines = [
        "# 能力归属矩阵（`IVisionEngine` 逐方法）",
        "",
        "> 本报告由 `tools/diagnostics/r5_capability_matrix.py` 重建，不手写。",
        "> 用途：`docs/architecture-notes.md` 增量纪律第 3 条要求的归属矩阵；",
        "> 也是「最终只依赖上游静态规则」（第三阶段）的**差距清单**。",
        "",
        f"- 接口方法：**{len(methods)}** 个；Core 里调用点合计 **{total_sites}** 处",
        f"- 分类统计：" + "、".join(f"**{key} {value}**" for key, value in sorted(by_category.items())),
        "",
        "| 方法 | 归属 | 静态数据 | 调用点 | 涉及文件 | 说明 |",
        "| --- | --- | --- | --- | --- | --- |",
    ]
    lines += [f"| `{name}` | {category} | {STATIC_DATA.get(name, "—")} | {count} | {files} | {reason} |"
              for name, category, count, files, reason in rows]
    lines += [
        "",
        "## 怎么读这张表（对第三阶段的意义）",
        "",
        "> ⚠️ **边界（别读错）**：`可静态化` 说的是「数据**可以**来自静态导出」，**不是**现在就该把运行时切过去。",
        "> 按仓库纪律（`AGENTS.md`、`docs/architecture-roadmap.md`）：静态导出只用于**离线展示、来源追踪与漂移校验**；",
        "> 界面导航、任务目录与地图识别在**替换宿主之前**必须继续走上游对象与原生流程，",
        "> 切换属于第三阶段、要过 R5 的门槛（同局对照 + 性能基线 + 真实产品路径证据）。",
        "> 这张表的作用是**列清差距**，不是提前动运行时。",
        "",
        "- **可静态化**：数据在上游的静态规则/配置里，导出后自研引擎能自己消费——"
        "这些是第三阶段可以直接摘掉的运行时调用；标着**未导出**的（页面图、任务目录）"
        "就是第三阶段要先补的导出器；",
        "- **识图保留**：运行时要跑上游模板匹配/OCR，替换宿主前必须有等价的自研视觉"
        "（或继续借上游视觉宿主）；",
        "- **必须自研**：目前只在 Python 侧实现的业务逻辑（统计报表、商店策略、S3 生产路径），"
        "替换宿主时代码要自己写；",
        "- **设备帧**：设备控制与抓帧通道，语义不变、实现要换。",
        "",
        "棘轮：出现**未分类**的方法即失败——新接口方法必须先给归属结论再合入。",
        "",
    ]
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}：{len(methods)} 个方法 / {total_sites} 个调用点")
    print("分类：" + "、".join(f"{k} {v}" for k, v in sorted(by_category.items())))
    if unclassified:
        print("FAIL: 以下方法没有归属结论：" + "、".join(unclassified))
        return 1
    print("PASS: 每个接口方法都有归属结论（棘轮：新方法必须分类）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
