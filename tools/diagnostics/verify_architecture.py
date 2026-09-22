#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查项目的不可变架构边界（无需设备）。"""
from __future__ import annotations

import re
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]


def _quoted(block: str) -> set[str]:
    """取出代码块里的字符串字面量（单双引号都算），用于比较两侧词表。"""
    found = re.findall(r"'([^']*)'|\"([^\"]*)\"", block or "")
    return {a or b for a, b in found if (a or b)}


def _block(text: str, start: str, stop: str) -> str:
    """从 `start` 起到其后第一个 `stop` 为止的片段（够读一个元组字面量）。"""
    at = text.find(start)
    if at < 0:
        return ""
    end = text.find(stop, at)
    return text[at:end if end > 0 else len(text)]


def _csharp_array(text: str, declaration: str) -> str:
    """C# 数组/类体的字面量块：`declaration` 之后第一对花括号里的内容。"""
    match = re.search(re.escape(declaration) + r"\s*=\s*\{(.*?)\};", text, re.S)
    if match:
        return match.group(1)
    match = re.search(re.escape(declaration) + r"\s*\{(.*?)\n    \}", text, re.S)
    return match.group(1) if match else ""


def task_domain_registration() -> list[str]:
    """每个任务域都必须在 CLI 里注册（否则队列只会报"没有注册运行器"然后失败）。

    这类漏挂在静态上就能查出来，不必等到某次队列跑起来才发现：
    扫 `Tasks/*Task.cs` 里实现 `ITaskRunner` 的类，要求 `Program.cs` 里有对应的
    `new …<类名>()` 注册语句（注册用的是类，不是 `Kind` 字面量）。
    """
    problems: list[str] = []
    program = ROOT / "src/Alas.DataTool/Program.cs"
    tasks = ROOT / "src/Alas.Core/Tasks"
    if not program.is_file() or not tasks.is_dir():
        return problems
    text = program.read_text(encoding="utf-8")
    registered = 0
    for path in sorted(tasks.glob("*Task.cs")):
        source = path.read_text(encoding="utf-8")
        if "ITaskRunner" not in source:
            continue
        match = re.search(r"class\s+(\w+)\s*:\s*ITaskRunner", source)
        if not match:
            problems.append(f"{path.name} 里找不到 `class X : ITaskRunner`")
            continue
        name = match.group(1)
        if f"{name}()" not in text:
            problems.append(f"任务域 {name}（{path.name}）没有在 Program.cs 里注册")
        else:
            registered += 1
    if registered == 0 and not problems:
        problems.append("任务域扫描不到任何 ITaskRunner 实现（检查 Tasks/ 目录）")
    return problems


def device_checklist_integrity() -> list[str]:
    """真机清单里的两条**不能悄悄消失**的东西。

    1. "主动撤退换一条 withdrawn 真机记录" —— 它是 R0 的已知缺口，但会消耗石油、
       改变账号状态，所以必须保持"需本人授权、不自动执行"的标注；把标注删掉或让脚本
       自动去跑，就是拿账号资源换一个文档上的勾。
    2. "用新运行时跑一次真机通关" —— 现有通关证据出自运行时之前的 CLI 路径，
       这条欠账不能在清单里被抹掉（抹掉之后没人记得还欠着）。

    用**字面锁定**而不是语义分析：这里要的就是"改动必须显式且被看见"，
    谁要动这两条，就得同时改这个守卫，改动天然进入审阅视野。
    """
    path = ROOT / "tools/diagnostics/device_smoke.py"
    if not path.is_file():
        return ["缺少真机清单: tools/diagnostics/device_smoke.py"]
    text = path.read_text(encoding="utf-8")
    problems = []
    for phrase, why in (
        ("【需本人授权】", "撤退项必须保持'需本人授权'标注（它消耗石油并改变账号状态）"),
        ("不自动执行", "撤退项必须保持'不自动执行'标注（不许脚本自作主张）"),
        ("新运行时", "清单里必须保留'用新运行时跑一次真机通关'这条欠账"),
    ):
        if phrase not in text:
            problems.append(f"真机清单缺少 `{phrase}`：{why}")
    return problems


def contract_consistency() -> list[str]:
    """结果合同的两份实现必须说同一套词。

    合同的价值全在"两侧口径一致"上：词表或违例码分叉了，
    跨语言对拍（verify_result_contract.py）会红，但那时改起来已经要翻两边代码。
    这里先把**静态可比的部分**（版本号、结果词表、违例码）在守卫里比一遍。
    """
    problems: list[str] = []
    py_path = ROOT / "tools/sortie_contract.py"
    cs_path = ROOT / "src/Alas.Core/Campaign/SortieResult.cs"
    if not py_path.is_file() or not cs_path.is_file():
        return problems          # 缺文件由 required 表报，这里不重复
    py = py_path.read_text(encoding="utf-8")
    cs = cs_path.read_text(encoding="utf-8")

    if "'sortie-result/1'" not in py or '"sortie-result/1"' not in cs:
        problems.append("结果合同两侧版本号不是 sortie-result/1")

    pairs = (
        ("结果词表", _block(py, "OUTCOMES = (", ")"),
         _csharp_array(cs, "public static readonly string[] Outcomes")),
        ("违例码", _block(py, "VIOLATION_CODES = (", ")"),
         _csharp_array(cs, "public static class Codes")),
    )
    for label, py_block, cs_block in pairs:
        want, have = _quoted(py_block), _quoted(cs_block)
        if not want:
            problems.append(f"结果合同缺{label}（Python 侧）")
        elif want != have:
            problems.append(f"结果合同{label}两侧不一致: "
                            f"仅 Python 有 {sorted(want - have)}，仅 C# 有 {sorted(have - want)}")
    return problems


def main() -> int:
    problems: list[str] = []
    required = {
        "路径解析": ROOT / "src/Alas.DataTool/ProjectPaths.cs",
        "上游数据入口": ROOT / "src/Alas.Core/UpstreamData.cs",
        "视觉宿主接口": ROOT / "src/Alas.Core/Vision/IVisionEngine.cs",
        "迁移路线": ROOT / "docs/architecture-roadmap.md",
        "结果合同(C#)": ROOT / "src/Alas.Core/Campaign/SortieResult.cs",
        "结果合同(Python)": ROOT / "tools/sortie_contract.py",
        "结果合同说明": ROOT / "docs/result-contract.md",
        "实机证据核对": ROOT / "tools/diagnostics/audit_real_records.py",
        "常驻运行时": ROOT / "src/Alas.Core/Runtime/AlasSession.cs",
        "批量任务编排": ROOT / "src/Alas.Core/Runtime/CampaignBatchRunner.cs",
        "结构化日志": ROOT / "src/Alas.Core/Runtime/SessionLog.cs",
        "统一错误分类": ROOT / "src/Alas.Core/Runtime/RuntimeErrors.cs",
        "运行时说明": ROOT / "docs/runtime.md",
        "任务模型": ROOT / "src/Alas.Core/Tasks/TaskModel.cs",
        "任务队列": ROOT / "src/Alas.Core/Tasks/TaskQueue.cs",
        "战役任务域": ROOT / "src/Alas.Core/Tasks/CampaignBatchTask.cs",
        "账号状态任务域": ROOT / "src/Alas.Core/Tasks/AccountStateTask.cs",
        "任务域说明": ROOT / "docs/tasks.md",
        "账号状态验收": ROOT / "tools/diagnostics/verify_account_state.py",
        "运行报告": ROOT / "src/Alas.Core/Runtime/RunReport.cs",
        "运行报告验收": ROOT / "tools/diagnostics/verify_report.py",
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
    batch = read("src/Alas.Core/Runtime/CampaignBatchRunner.cs")
    checks = {
        "CLI 使用集中路径解析": "ProjectPaths.Resolve()" in program,
        "战役生产入口": "RunCampaignPlan" in batch and '"s3_run_plan"' in vision,
        "上游数据读取入口": "static Catalog Open" in upstream,
        "服务器 button 解析": "ButtonFor(string server)" in models,
        # 结果判定只走合同：裁决在运行时里做，CLI 只把裁决结果排版出来。
        "战役结果走合同裁决": "SortieContract.Violations" in batch
                              and "SortieContract.Describe" in program,
        # R1：业务编排在运行时里，CLI 只解析参数与排版（不许自己驱动引擎）。
        "战役编排走运行时": "AlasSession.Start" in program and "CampaignBatchRunner" in program,
        "CLI 不直接驱动引擎": "RunCampaignPlan" not in program
                              and "InProcessVisionEngine" not in program,
        # R2：任务域走通用任务模型，CLI 只解析队列文件（不解释任务内容）。
        "任务队列入口": "TaskQueueFile.Parse" in program and "TaskQueue" in program,
        "参数解析共享": program.count("ParseRunFlags(") >= 2,
        "任务模型是接口": "interface ITaskRunner" in read("src/Alas.Core/Tasks/TaskModel.cs"),
    }
    for label, ok in checks.items():
        if not ok:
            problems.append(f"架构入口缺失: {label}")

    problems.extend(contract_consistency())
    problems.extend(task_domain_registration())
    problems.extend(device_checklist_integrity())

    roadmap = read("docs/architecture-roadmap.md")
    for phase in ("R0：", "R1：", "R2：", "R3：", "R4：", "R5："):
        if phase not in roadmap:
            problems.append(f"迁移路线缺少阶段: {phase}")
    if "长期不能变动的规则" not in roadmap:
        problems.append("迁移路线缺少不可变边界")

    # 生产 C# 不得重新维护地图名/编号分支，也不得从离线素材 JSON 重建视觉规则。
    csharp_files = [
        path for path in (ROOT / "src").rglob("*.cs")
        if "bin" not in path.parts and "obj" not in path.parts
    ]
    map_literal = re.compile(r"campaign_[A-Za-z0-9]+_[0-9]+(?:_[0-9]+)+")
    for path in csharp_files:
        text = path.read_text(encoding="utf-8")
        if map_literal.search(text):
            problems.append(f"生产代码含地图特例字面量: {path.relative_to(ROOT)}")
        if '"assets.json"' in text and path.name != "UpstreamData.cs":
            problems.append(f"生产代码直接依赖 assets.json: {path.relative_to(ROOT)}")

    # R0 门槛：`CampaignEnd` 只表示"出击结束"（撤退也抛它），不得单独用作通关判据。
    # 范围是**生产代码**（C# 生产源 + tools 顶层）；`tools/diagnostics/` 下的对拍脚本
    # 会**故意**写"campaign_end 就声称通关"的反例文档，不算违规实现。
    clear_from_end = re.compile(r"(?i)\bcleared\b\s*=[^=\n]{0,60}\bcampaign_?end\b")
    for path in csharp_files + [p for p in (ROOT / "tools").glob("*.py")]:
        text = path.read_text(encoding="utf-8")
        hit = clear_from_end.search(text)
        if hit:
            line = text[:hit.start()].count("\n") + 1
            problems.append(f"生产代码用 CampaignEnd 单字段判通关: "
                            f"{path.relative_to(ROOT)}:{line}")

    if problems:
        print("架构守卫失败:")
        for item in problems:
            print(f"- {item}")
        return 1
    print("架构守卫通过: 上游宿主链、集中路径、素材模型和迁移规范均存在")
    return 0


if __name__ == "__main__":
    sys.exit(main())
