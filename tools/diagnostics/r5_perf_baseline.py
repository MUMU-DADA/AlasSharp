#!/usr/bin/env python3
"""R5 性能基线（离线）：C# 引擎关键路径 vs 上游 Python 实现的同项耗时。

用法：
    python tools/diagnostics/r5_perf_baseline.py            # 重建报告
    python tools/diagnostics/r5_perf_baseline.py --print    # 同时打印

产出：docs/archive/reports/r5-performance-baseline.md（由本脚本重建，不手写）

测量口径（重要）：
  * **寻路成本场**：同一张**真实关卡地图**（从 data/campaign 导出的 map_data 取），
    C# 侧跑 `Alas.Server r5-path --repeat N` 并把命令自报的 per_operation_ms 取中位；
    Python 侧构造上游 `CampaignMap` 后跑 N 次 `find_path_initial()` 计时。两边做的是同一件事，
    但运行时不同（.NET vs CPython），Python 侧还带 logger 开销——**只看数量级，不当成精确倍数**。
  * **全库计划读取**：C# 跑 `r5-plan`（读 1437 个导出 + 干跑全部钩子）的墙钟时间；
    Python 侧是审计脚本（AST 解析上游源码，工作内容不同），仅作参考。

只做测量：不连设备、不写仓库里的数据。
"""
from __future__ import annotations

import argparse
import json
import pathlib
import statistics
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
REPORT = ROOT / "docs" / "archive" / "reports" / "r5-performance-baseline.md"
WORK = ROOT / ".runtime" / "perf"
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"
UPSTREAM = ROOT / ".runtime" / "engine"

# 两张有代表性的真实地图：最大的（200 格）与中位数的（64 格）
MAPS = [
    ("event_20240229_cn/sp", "最大地图（20×10 = 200 格）"),
    ("event_20220915_cn/c2", "中位规模（8×8 = 64 格）"),
]
REPEAT = 200


def map_tokens(rel: str) -> tuple[list[str], str]:
    """从导出里取出地图令牌行与起点（第一个非陆地格）。"""
    payload = json.loads((ROOT / "data" / "campaign" / f"{rel}.json").read_text(encoding="utf-8"))
    text = (payload.get("map") or {}).get("map_data") or ""
    rows = [row.strip() for row in text.strip().splitlines() if row.strip()]
    start = "A1"
    for y, row in enumerate(rows):
        for x, token in enumerate(row.split()):
            if token not in ("++",):
                start = f"{chr(65 + x)}{y + 1}"
                break
        else:
            continue
        break
    return rows, start


def measure_csharp(rows: list[str], start: str, repeat: int) -> dict:
    WORK.mkdir(parents=True, exist_ok=True)
    fixture = WORK / "perf-path-fixture.json"
    fixture.write_text(json.dumps({
        "cases": [{"name": "perf", "rows": rows, "start": start, "has_ambush": False, "has_enemy": True}],
    }, ensure_ascii=False), encoding="utf-8")
    values = []
    for _ in range(3):
        completed = subprocess.run([str(SERVER), "r5-path", "--fixture", str(fixture), "--repeat", str(repeat)],
                                   cwd=ROOT, capture_output=True, text=True, timeout=600,
                                   encoding="utf-8", errors="replace")
        if completed.returncode != 0:
            raise SystemExit(f"r5-path 失败：{completed.stdout} {completed.stderr}")
        payload = json.loads(completed.stdout)
        values.append(payload["cases"][0]["per_operation_ms"])
    return {"per_operation_ms": statistics.median(values), "runs": len(values), "repeat": repeat}


def measure_python(rows: list[str], start: str, repeat: int) -> dict:
    if str(UPSTREAM) not in sys.path:
        sys.path.insert(0, str(UPSTREAM))
    from module.base.utils import location2node, node2location  # noqa: PLC0415
    from module.map.map_base import CampaignMap  # noqa: PLC0415

    campaign_map = CampaignMap("perf")
    campaign_map.shape = location2node((max(len(r.split()) for r in rows) - 1, len(rows) - 1))
    campaign_map.map_data = "\n".join(rows)
    campaign_map.load_map_data()
    campaign_map.grid_connection_initial(wall=True)
    start_location = tuple(node2location(start))

    campaign_map.find_path_initial(start_location, has_ambush=False, has_enemy=True)  # 预热
    values = []
    for _ in range(3):
        begin = time.perf_counter()
        for _ in range(repeat):
            campaign_map.find_path_initial(start_location, has_ambush=False, has_enemy=True)
        values.append((time.perf_counter() - begin) * 1000 / repeat)
    return {"per_operation_ms": statistics.median(values), "runs": len(values), "repeat": repeat}


def measure_plan_read() -> dict:
    """全库计划读取：C# r5-plan（含干跑全部钩子）的墙钟时间。"""
    values = []
    for _ in range(3):
        begin = time.perf_counter()
        completed = subprocess.run([str(SERVER), "r5-plan"], cwd=ROOT, capture_output=True,
                                   text=True, timeout=900, encoding="utf-8", errors="replace")
        values.append(time.perf_counter() - begin)
        if completed.returncode != 0:
            raise SystemExit("r5-plan 失败")
    return {"wall_seconds": statistics.median(values), "runs": len(values)}


def measure_audit() -> dict:
    values = []
    for _ in range(2):
        begin = time.perf_counter()
        subprocess.run([sys.executable, str(ROOT / "tools" / "diagnostics" / "r5_upstream_audit.py")],
                       cwd=ROOT, capture_output=True, text=True, timeout=900, encoding="utf-8",
                       errors="replace")
        values.append(time.perf_counter() - begin)
    return {"wall_seconds": statistics.median(values), "runs": len(values)}


def render(path_rows: list[dict], plan: dict, audit: dict) -> str:
    out: list[str] = []
    add = out.append
    add("# R5 性能基线（离线）")
    add("")
    add("> 本报告由 `tools/diagnostics/r5_perf_baseline.py` 重建，不手写。")
    add("> 用途：为\"替换上游引擎\"留替换前后的对照底数（P2 要求之一）。")
    add("")
    add("## 口径与局限（先读这段）")
    add("")
    add("- 寻路两边做的是**同一件事**（同一张真实地图上算成本场），但运行时不同"
        "（.NET 10 vs CPython 3.14），Python 侧还带 logger 开销——只看**数量级**，别当精确倍数；")
    add(f"- C# 侧取命令自报的内部 per_operation_ms 中位（每次 3 轮 × 每轮 {REPEAT} 次），"
        "不含进程启动；Python 侧同样预热后计时；")
    add("- 全库计划读取两边**工作内容不同**（C# 读导出并干跑全部钩子；Python 审计解析上游源码做 AST 统计），"
        "放一起只为记录量级，不能直接相减；")
    add("- 机器状态、后台负载会影响绝对值；重跑同一脚本即可得到同口径数字。")
    add("")
    add("## 寻路成本场（同一张真实地图）")
    add("")
    add("| 地图 | 规模 | C# per-op（ms） | 上游 Python per-op（ms） | 量级比 |")
    add("| --- | --- | --- | --- | --- |")
    for item in path_rows:
        ratio = item["python"]["per_operation_ms"] / item["csharp"]["per_operation_ms"] \
            if item["csharp"]["per_operation_ms"] else 0
        add(f"| `{item['rel']}` | {item['note']} | {item['csharp']['per_operation_ms']:.4f} | "
            f"{item['python']['per_operation_ms']:.4f} | {ratio:.1f}× |")
    add("")
    add("## 全库计划读取（1437 个导出 / 3019 个钩子 / 5694 步）")
    add("")
    add("| 工具 | 做什么 | 墙钟（秒） |")
    add("| --- | --- | --- |")
    add(f"| `Alas.Server r5-plan` | 读全部导出 + 干跑全部钩子 + 统计 | {plan['wall_seconds']:.2f} |")
    add(f"| `tools/diagnostics/r5_upstream_audit.py` | 审计上游 `campaign/` 源码（AST） | {audit['wall_seconds']:.2f} |")
    add("")
    add("## 复现方式")
    add("")
    add("```powershell")
    add("python tools/diagnostics/r5_perf_baseline.py")
    add("& src\\Alas.Server\\bin\\Release\\net10.0\\Alas.Server.exe r5-path --fixture <夹具> --repeat 200")
    add("```")
    add("")
    return "\n".join(out)


def main() -> int:
    parser = argparse.ArgumentParser(description="R5 性能基线")
    parser.add_argument("--print", action="store_true", dest="echo")
    args = parser.parse_args()
    if not SERVER.is_file():
        raise SystemExit(f"缺少 {SERVER}；先运行 ./build.ps1 构建")

    path_rows = []
    for rel, note in MAPS:
        rows, start = map_tokens(rel)
        path_rows.append({
            "rel": rel, "note": note, "start": start, "grids": sum(len(r.split()) for r in rows),
            "csharp": measure_csharp(rows, start, REPEAT),
            "python": measure_python(rows, start, REPEAT),
        })
    plan = measure_plan_read()
    audit = measure_audit()

    report = render(path_rows, plan, audit)
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text(report + "\n", encoding="utf-8", newline="\n")
    print(f"已重建 {REPORT.relative_to(ROOT)}（{len(report.splitlines())} 行）")
    if args.echo:
        print(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
