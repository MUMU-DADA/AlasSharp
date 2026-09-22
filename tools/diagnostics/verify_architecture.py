#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查项目的不可变架构边界（无需设备）。"""
from __future__ import annotations

import re
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]


def main() -> int:
    problems: list[str] = []
    required = {
        "路径解析": ROOT / "src/Alas.DataTool/ProjectPaths.cs",
        "上游数据入口": ROOT / "src/Alas.Core/UpstreamData.cs",
        "视觉宿主接口": ROOT / "src/Alas.Core/Vision/IVisionEngine.cs",
        "迁移路线": ROOT / "docs/architecture-roadmap.md",
    }
    for label, path in required.items():
        if not path.is_file():
            problems.append(f"缺少{label}: {path.relative_to(ROOT)}")

    def read(rel: str) -> str:
        path = ROOT / rel
        return path.read_text(encoding="utf-8") if path.is_file() else ""

    program = read("src/Alas.DataTool/Program.cs")
    vision = read("src/Alas.Core/Vision/IVisionEngine.cs")
    models = read("src/Alas.Core/UpstreamModels.cs")
    upstream = read("src/Alas.Core/UpstreamData.cs")
    checks = {
        "CLI 使用集中路径解析": "ProjectPaths.Resolve()" in program,
        "战役生产入口": "RunCampaignPlan" in program and '"s3_run_plan"' in vision,
        "上游数据读取入口": "static Catalog Open" in upstream,
        "服务器 button 解析": "ButtonFor(string server)" in models,
    }
    for label, ok in checks.items():
        if not ok:
            problems.append(f"架构入口缺失: {label}")

    roadmap = read("docs/architecture-roadmap.md")
    for phase in ("R0：", "R1：", "R2：", "R3：", "R4：", "R5："):
        if phase not in roadmap:
            problems.append(f"迁移路线缺少阶段: {phase}")
    if "长期不能变动的规则" not in roadmap:
        problems.append("迁移路线缺少不可变边界")

    # 生产 C# 不得重新维护地图名/编号分支，也不得从离线素材 JSON 重建视觉规则。
    source_files = [
        path for path in (ROOT / "src").rglob("*.cs")
        if "bin" not in path.parts and "obj" not in path.parts
    ]
    map_literal = re.compile(r"campaign_[A-Za-z0-9]+_[0-9]+(?:_[0-9]+)+")
    for path in source_files:
        text = path.read_text(encoding="utf-8")
        if map_literal.search(text):
            problems.append(f"生产代码含地图特例字面量: {path.relative_to(ROOT)}")
        if '"assets.json"' in text and path.name != "UpstreamData.cs":
            problems.append(f"生产代码直接依赖 assets.json: {path.relative_to(ROOT)}")

    if problems:
        print("架构守卫失败:")
        for item in problems:
            print(f"- {item}")
        return 1
    print("架构守卫通过: 上游宿主链、集中路径、素材模型和迁移规范均存在")
    return 0


if __name__ == "__main__":
    sys.exit(main())
