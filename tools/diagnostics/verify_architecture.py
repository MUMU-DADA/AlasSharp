#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""静态验证唯一产品执行核心的边界。

Alas.Engine 是唯一产品执行核心。Alas.Core 已删除，不能以源码、项目、发布文件
或执行入口的形式重新出现。Python 仅允许作为独立 CV/OCR worker。
"""
from __future__ import annotations

import ast
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# These modules belonged to the retired Core/S3/R5 Python host. Keeping their
# names in the active tools tree makes it too easy to reintroduce a business
# Python dependency through an ad-hoc import. Historical copies live under
# tools/archive/legacy-python and are deliberately outside the product path.
LEGACY_HOST_FILES = (
    "alas_vision.py",
    "vision_worker.py",
    "campaign_rules.py",
    "campaign_shadow_observation.py",
    "native_campaign_runtime.py",
    "native_scheduler.py",
    "native_task_overrides.py",
    "native_telemetry.py",
    "native_tool_device.py",
    "s3_camera_compat.py",
    "s3_campaign_entry.py",
    "s3_campaign_execution.py",
    "s3_campaign_outcome.py",
    "ui_rule_catalog.py",
)
LEGACY_IMPORT = re.compile(
    r"(?:^|\n)\s*(?:from\s+(?:alas_vision|campaign_rules|native_campaign_runtime|"
    r"native_scheduler|native_task_overrides|native_telemetry|native_tool_device|"
    r"campaign_shadow_observation|s3_campaign_[a-z_]+|ui_rule_catalog)\b|"
    r"import\s+(?:alas_vision|campaign_rules|native_campaign_runtime|"
    r"native_scheduler|native_task_overrides|native_telemetry|native_tool_device|"
    r"campaign_shadow_observation|s3_campaign_[a-z_]+|ui_rule_catalog)\b)",
    re.MULTILINE,
)


def read(relative: str) -> str:
    path = ROOT / relative
    return path.read_text(encoding="utf-8") if path.is_file() else ""


def project_references(path: Path) -> list[str]:
    tree = ET.parse(path).getroot()
    return sorted(Path(item.attrib.get("Include", "")).stem for item in tree.iter("ProjectReference"))


def contract_consistency() -> list[str]:
    py = read("tools/sortie_contract.py")
    cs = read("src/Alas.Engine/Contracts/SortieContract.cs")
    problems: list[str] = []
    if not py or not cs:
        return ["Engine 结果合同源文件缺失"]
    if "'sortie-result/1'" not in py or '"sortie-result/1"' not in cs:
        problems.append("结果合同两侧版本号不是 sortie-result/1")
    py_outcomes = set(re.findall(r"'([^']+)'", py[py.find("OUTCOMES =") : py.find("WIN_RANKS")]))
    cs_outcomes = set(re.findall(r'"([^\"]+)"', cs[cs.find("Outcomes") : cs.find("WinRanks")]))
    if py_outcomes != cs_outcomes:
        problems.append(f"结果词表不一致: Python-only={sorted(py_outcomes - cs_outcomes)}, CSharp-only={sorted(cs_outcomes - py_outcomes)}")
    py_codes = set(re.findall(r"'([^']+)'", py[py.find("VIOLATION_CODES") : py.find("def _failures")]))
    codes_start = cs.find("public static class Codes")
    codes_end = cs.find("    }", codes_start)
    cs_codes = set(re.findall(r'"([^\"]+)"', cs[codes_start:codes_end]))
    if py_codes != cs_codes:
        problems.append(f"结果违例码不一致: Python-only={sorted(py_codes - cs_codes)}, CSharp-only={sorted(cs_codes - py_codes)}")
    return problems


def product_boundary() -> list[str]:
    problems: list[str] = []
    tools_root = ROOT / "tools"
    for filename in LEGACY_HOST_FILES:
        active = tools_root / filename
        if active.is_file():
            problems.append(f"退役 Python 业务宿主仍位于活动目录: {active.relative_to(ROOT)}")
    # The active diagnostics directory may contain export/oracle checks, but
    # it must not import the retired host. Archive paths are intentionally
    # skipped and are checked only as historical files.
    active_diagnostics = tools_root / "diagnostics"
    if active_diagnostics.is_dir():
        for script in active_diagnostics.rglob("*.py"):
            if script.name == Path(__file__).name:
                continue
            try:
                text = script.read_text(encoding="utf-8")
            except OSError as error:
                problems.append(f"无法读取活动诊断脚本 {script.relative_to(ROOT)}: {error}")
                continue
            if LEGACY_IMPORT.search(text):
                problems.append(f"活动诊断脚本导入退役 Python 宿主: {script.relative_to(ROOT)}")

    retired_core = ROOT / "src/Alas.Core"
    if retired_core.exists():
        tracked = list(retired_core.rglob("*"))
        if any(path.is_file() for path in tracked):
            problems.append("退役 Alas.Core 源码仍存在；Engine 必须是唯一产品执行核心")
        else:
            problems.append("退役 Alas.Core 目录仍存在；删除空目录后再验收")
    if (ROOT / "src/Alas.Core/Alas.Core.csproj").is_file():
        problems.append("退役 Alas.Core.csproj 仍存在")
    expected = {
        "Alas.Contracts": [],
        "Alas.Client": ["Alas.Contracts"],
        "Alas.Engine": [],
        "Alas.Engine.Cli": ["Alas.Engine"],
        "Alas.Server": ["Alas.Contracts", "Alas.Engine"],
        "Alas.UI": ["Alas.Contracts"],
        "Alas.UI.Desktop": ["Alas.Contracts", "Alas.Engine", "Alas.UI"],
        "Alas.UI.Browser": ["Alas.Client", "Alas.UI"],
        "Alas.UI.Headless": ["Alas.UI"],
    }
    for project in (ROOT / "src").glob("*/*.csproj"):
        name = project.parent.name
        if name not in expected:
            problems.append(f"未登记产品项目: {project.relative_to(ROOT)}")
            continue
        references = project_references(project)
        if references != sorted(expected[name]):
            problems.append(f"项目引用漂移: {name}: {references}")
        if "Alas.Core" in project.read_text(encoding="utf-8"):
            problems.append(f"产品项目仍引用退役 Core: {project.relative_to(ROOT)}")
    for solution in (ROOT / "Alas.sln", ROOT / "Alas.Engine.slnx", ROOT / "Alas.UI.slnx"):
        if "Alas.Core" in solution.read_text(encoding="utf-8"):
            problems.append(f"产品解决方案仍包含退役 Core: {solution.name}")

    source_roots = [ROOT / "src/Alas.Engine", ROOT / "src/Alas.Server", ROOT / "src/Alas.UI", ROOT / "src/Alas.UI.Desktop", ROOT / "src/Alas.UI.Browser"]
    for source_root in source_roots:
        for source in source_root.rglob("*.cs"):
            if any(part in {"bin", "obj"} for part in source.parts):
                continue
            text = source.read_text(encoding="utf-8-sig")
            if re.search(r"\b(?:using|global::)\s+Alas\.Core\b|Alas\.Core\.csproj|Alas\.Core\.Diagnostics", text):
                problems.append(f"产品源码调用退役 Core: {source.relative_to(ROOT)}")
            if re.search(r"(?:alas_vision|s3_campaign|native_campaign_runtime|RunCampaignPlan|InProcessVisionEngine)", text, re.I):
                problems.append(f"产品源码保留 Python/旧计划业务入口: {source.relative_to(ROOT)}")

    tests = ROOT / "tests/Alas.Engine.Tests/Alas.Engine.Tests.csproj"
    if project_references(tests) != ["Alas.Engine"]:
        problems.append("Engine 测试必须只引用 Alas.Engine")
    server = read("src/Alas.Server/ControlServer.cs")
    desktop = read("src/Alas.UI.Desktop/DirectEngineBackend.cs")
    workspace = read("src/Alas.Engine/Runtime/EngineControlWorkspace.cs")
    queue = read("src/Alas.Engine/Tasks/TaskQueue.cs")
    campaign_resume = read("src/Alas.Engine/Tasks/CampaignResumeTask.cs")
    if "EngineControlWorkspace" not in server or "QueueExecution" in server:
        problems.append("Server 未把编排交给 EngineControlWorkspace")
    if "EngineControlWorkspace" not in desktop or "DirectCoreBackend" in desktop:
        problems.append("桌面 UI 未直接调用 EngineControlWorkspace")
    if "new TaskQueue().RunAsync" not in workspace:
        problems.append("EngineControlWorkspace 未调用 Engine.TaskQueue")
    if "new CampaignRunTask()" not in queue or "new CampaignResumeTask()" not in queue:
        problems.append("Engine 队列缺少战役任务注册")
    if "SortieContract.Violations" not in campaign_resume:
        problems.append("战役结果没有由 Engine.SortieContract 裁决")
    if "EngineCapabilityUnavailableException" not in workspace:
        problems.append("未迁移能力没有在 Engine 内显式拒绝")
    if "ControlClient" not in read("src/Alas.UI.Browser/BrowserControlBackend.cs"):
        problems.append("浏览器 UI 未使用共享 HTTP 客户端")

    worker = ROOT / "src/Alas.Engine/Imaging/Worker/vision_worker.py"
    if not worker.is_file():
        problems.append("缺少独立 CV/OCR worker")
    else:
        allowed = {"base64", "io", "json", "sys", "cv2", "numpy", "imageio", "scipy", "hashlib", "pathlib", "PIL", "onnxruntime"}
        worker_text = worker.read_text(encoding="utf-8")
        for node in ast.walk(ast.parse(worker_text)):
            imports = ([alias.name for alias in node.names] if isinstance(node, ast.Import)
                       else [node.module or ""] if isinstance(node, ast.ImportFrom) else [])
            if any(name.split(".")[0] not in allowed for name in imports):
                problems.append("CV/OCR worker 导入了业务依赖")
        if re.search(r"(?i)(?:alas_vision|campaign_rules|s3_campaign|native_campaign|adb|device\.click|run_campaign|campaign\.run)", worker_text):
            problems.append("CV/OCR worker 包含业务宿主、设备动作或战役调度入口")
    return problems


def runtime_contract() -> list[str]:
    problems: list[str] = []
    engine = read("src/Alas.Engine/Contracts/SortieContract.cs")
    for marker in ("public static class SortieContract", "public sealed class SortieResult", "public static List<string> Violations"):
        if marker not in engine:
            problems.append(f"Engine 结果合同缺少 {marker}")
    for path in (ROOT / "src").rglob("*.cs"):
        if any(part in {"bin", "obj"} for part in path.parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        if re.search(r"(?i)\bcleared\b\s*=[^=\n]{0,80}\bcampaign_?end\b", text):
            problems.append(f"代码使用 CampaignEnd 单字段判通关: {path.relative_to(ROOT)}")
        if '"assets.json"' in text and path.name != "UpstreamData.cs":
            problems.append(f"生产代码直接依赖 assets.json: {path.relative_to(ROOT)}")
    map_literal = re.compile(r"campaign_[A-Za-z0-9]+_[0-9]+(?:_[0-9]+)+")
    for path in (ROOT / "src").rglob("*.cs"):
        if any(part in {"bin", "obj"} for part in path.parts):
            continue
        if path.relative_to(ROOT).as_posix() == "src/Alas.Engine/Rules/Generated/CampaignMaps.g.cs":
            continue
        if map_literal.search(path.read_text(encoding="utf-8")):
            problems.append(f"生产代码含地图特例字面量: {path.relative_to(ROOT)}")
    compiler = ROOT / "tools/migration/compile_campaign_maps.py"
    if compiler.is_file():
        try:
            result = subprocess.run([sys.executable, str(compiler), "--upstream", os.environ.get("ALAS_FORK", str(ROOT / ".runtime/engine")), "--output", str(ROOT / "src/Alas.Engine/Rules/Generated/CampaignMaps.g.cs"), "--check"], cwd=ROOT, capture_output=True, timeout=60)
            if result.returncode:
                problems.append("C# 地图声明生成漂移检查失败")
        except (OSError, subprocess.TimeoutExpired):
            problems.append("C# 地图声明生成漂移检查无法完成")
    return problems


def publish_boundary() -> list[str]:
    problems: list[str] = []
    for directory in (ROOT / ".runtime/publish/server-engine", ROOT / ".runtime/publish/desktop-engine"):
        if not directory.is_dir():
            continue
        if any(path.name.lower().startswith("alas.core") for path in directory.rglob("*")):
            problems.append(f"发布输出包含退役 Core: {directory.relative_to(ROOT)}")
    return problems


def main() -> int:
    required = {
        "Engine 项目": ROOT / "src/Alas.Engine/Alas.Engine.csproj",
        "Engine 合同": ROOT / "src/Alas.Engine/Contracts/SortieContract.cs",
        "Engine 会话": ROOT / "src/Alas.Engine/Runtime/EngineSession.cs",
        "Engine 控制工作区": ROOT / "src/Alas.Engine/Runtime/EngineControlWorkspace.cs",
        "Engine 任务队列": ROOT / "src/Alas.Engine/Tasks/TaskQueue.cs",
        "Server": ROOT / "src/Alas.Server/ControlServer.cs",
        "桌面入口": ROOT / "src/Alas.UI.Desktop/DirectEngineBackend.cs",
        "结果合同说明": ROOT / "docs/result-contract.md",
        "迁移路线": ROOT / "docs/architecture-roadmap.md",
        "Python oracle": ROOT / "tools/sortie_contract.py",
    }
    problems = [f"缺少{label}: {path.relative_to(ROOT)}" for label, path in required.items() if not path.is_file()]
    problems.extend(product_boundary())
    problems.extend(contract_consistency())
    problems.extend(runtime_contract())
    problems.extend(publish_boundary())
    roadmap = read("docs/architecture-roadmap.md")
    for marker in ("桌面 UI → Alas.Engine", "浏览器 UI → Alas.Server → Alas.Engine", "旧 Core 已从仓库删除", "长期不能变动的规则"):
        if marker not in roadmap:
            problems.append(f"迁移路线缺少边界: {marker}")
    if problems:
        print("\n".join(f"FAIL: {problem}" for problem in problems))
        return 1
    print("PASS: Engine is the only product execution core; Core is outside product and publish boundaries")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
