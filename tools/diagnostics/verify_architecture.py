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


def campaign_shims_installed() -> list[str]:
    """`op_s3_campaign_init` 里那批兼容垫片**必须还在被调用**。

    为什么单独守它：垫片（`apply_*_compat`）是"少一行调用就静默失效"的东西 ——
    代码看着还在、函数也还在，只是没人调了。而症状要到**真机**上才暴露
    （`IN_MAP` 那次就是：阈值差 0.19，真机白等 62 秒后 `GameStuckError`）。

    只用**字面扫描**：把 `op_s3_campaign_init` 的函数体切出来，要求这几个调用都在里面。
    谁要删或挪走其中一个，这里就会红 —— 于是改动进入审阅视野，而不是等到下一次真机运行。
    """
    path = ROOT / "tools" / "alas_vision.py"
    if not path.is_file():
        return ["缺少 tools/alas_vision.py"]
    text = path.read_text(encoding="utf-8")
    marker = "def op_s3_campaign_init"
    start = text.find(marker)
    if start < 0:
        return [f"`{marker}` 不存在（战役入口没了？）"]
    rest = text[start + len(marker):]
    # 切到下一个顶层 def 为止
    end = rest.find("\ndef ")
    body = rest if end < 0 else rest[:end]
    required = (
        "apply_numpy2_compat()",
        "apply_points_empty_compat()",
        "apply_fleet_bar_compat()",
        "apply_auto_search_skip_compat()",
        "apply_boss_icon_color_compat()",
        "apply_in_map_threshold_compat()",
        "apply_withdraw_trace_compat()",
    )
    missing = [call for call in required if call not in body]
    if missing:
        return [f"`op_s3_campaign_init` 里少了垫片调用：{missing}"
                "（垫片少一行调用不会报错，只会在真机上静默失效）"]
    return []


def shims_all_called() -> list[str]:
    """每个 `apply_*` 垫片**都得有人在调**（不只是定义在那里）。

    上一节只管战役入口那七个；这一节推广成一般性质：**"定义着但没人调"就是死代码**，
    而它失效的方式恰恰是静默的（`IN_MAP` 那次：阈值差 0.19，真机白等 62 秒）。

    判据：对每个 `def apply_*`，在**非 def 行**里必须能找到它的调用。
    """
    path = ROOT / "tools" / "alas_vision.py"
    if not path.is_file():
        return ["缺少 tools/alas_vision.py"]
    lines = path.read_text(encoding="utf-8").splitlines()
    defined = [line[len("def "):].split("(")[0].strip()
               for line in lines if line.startswith("def apply_")]
    if not defined:
        return ["alas_vision.py 里一个 apply_* 垫片都没有（全被删了？）"]
    calls = "\n".join(line for line in lines if not line.startswith("def "))
    unused = [name for name in defined if f"{name}(" not in calls]
    if unused:
        return [f"这些垫片定义了但**没人调用**：{unused}"
                "（垫片没人调不会报错，只会在真机上静默失效）"]
    return []


def withdraw_hook_present() -> list[str]:
    """**运行中请求撤退**的挂钩必须还在 —— 它失效的方式是静默的。

    语义：工件目录里出现 `withdraw.request` 时，宿主应在下一次战斗之前调用上游自己的
    `withdraw()`，本局据此判 `outcome=withdrawn`（R0 的真机记录就是这么来的）。
    如果哪天挂钩或路径约定被删除，请求文件会出现却**没人理** —— 没有报错，只是"撤退请求无效"。
    """
    problems = []
    execution = ROOT / "tools" / "s3_campaign_execution.py"
    if not execution.is_file() or "inst.withdraw()" not in execution.read_text(encoding="utf-8"):
        problems.append("tools/s3_campaign_execution.py 里没有调用 inst.withdraw()："
                        "运行中请求撤退会静默失效（文件出现却没人理）")
    runner = ROOT / "src" / "Alas.Core" / "Runtime" / "CampaignBatchRunner.cs"
    if not runner.is_file() or "withdraw.request" not in runner.read_text(encoding="utf-8"):
        problems.append("CampaignBatchRunner 没有传 withdraw.request 路径："
                        "请求文件的路径约定断了（同一类静默失效）")
    return problems

def periodic_run_gate_intact() -> list[str]:
    """执行入口（`op_periodic_run`）必须**先过两道闸、再构造/运行上游对象**。

    为什么值得静态守：这是"**不许在未授权时花钱**"这条承诺的落点。
    失效方式同样是静默的 —— 谁把闸门挪到构造之后、或删掉其中一个，
    代码照样能跑（甚至更"顺"），而代价是**未授权的账号操作**。

    判据不看措辞、看**顺序**：两道闸的判断必须出现在原生调度器构造之前；执行必须
    绑定 Scheduler.Command 对应的任务配置并调用 `AzurLaneAutoScript.run(method_name)`。
    """
    path = ROOT / "tools" / "alas_vision.py"
    if not path.is_file():
        return ["缺少 tools/alas_vision.py"]
    text = path.read_text(encoding="utf-8")
    start = text.find("def op_periodic_run(")
    if start < 0:
        return ["`op_periodic_run` 不存在（执行入口没了？）"]
    rest = text[start:]
    end = rest.find("\ndef ")
    body = rest if end < 0 else rest[:end]

    problems = []
    construct_at = body.find("runner = AzurLaneAutoScript(")
    if construct_at < 0:
        problems.append("`op_periodic_run` 没有构造 AzurLaneAutoScript 原生调度器")
        return problems
    for marker, why in (("if not allow:", "allow_actions 闸"),
                        ("if confirm != task:", "二次确认闸")):
        at = body.find(marker)
        if at < 0:
            problems.append(f"`op_periodic_run` 缺少{why}（{marker}）—— 未授权的账号操作会畅通无阻")
        elif at > construct_at:
            problems.append(f"`op_periodic_run` 的{why}在**构造对象之后**才检查 —— 顺序错了，"
                            "等于没闸（对象已经建起来、很可能已经动了设备）")
    required = (
        "AzurLaneConfig(instance, task=command)",
        "AzurLaneAutoScript(instance)",
        "_device_engine(config=config)",
        "config.override(**overrides)",
        "runner.run(method_name)",
    )
    missing = [marker for marker in required if marker not in body]
    if missing:
        problems.append(f"`op_periodic_run` 未完整复用上游任务绑定/调度语义: {missing}")
    return problems


def periodic_run_session_gate_intact() -> list[str]:
    """队列文件不能把默认 dry-run 会话自行升级成周期任务动作会话。

    宿主的两道闸只约束任务输入；CLI 的 `--run --allow-actions` 授权记录在
    `SessionOptions`。产品任务必须先检查会话授权，再调用 `periodic_run`。
    """
    path = ROOT / "src/Alas.Core/Tasks/PeriodicRunTask.cs"
    if not path.is_file():
        return ["缺少 PeriodicRunTask.cs（周期任务产品入口没了？）"]
    text = path.read_text(encoding="utf-8")
    call_at = text.find('CallTyped<PeriodicRunResult>("periodic_run"')
    if call_at < 0:
        return ["PeriodicRunTask 没有调用宿主 periodic_run（行为变了？）"]
    before_call = text[:call_at]
    missing = [marker for marker in ("context.Options.DryRun", "context.Options.AllowActions")
               if marker not in before_call]
    if missing:
        return [f"PeriodicRunTask 在调用宿主前没有检查会话级联锁: {missing}；"
                "队列 JSON 可能绕过 --run --allow-actions"]
    return []


def native_tool_boundary_intact() -> list[str]:
    def read(path):
        return (ROOT / path).read_text(encoding='utf-8')

    text = read('tools/alas_vision.py')
    body = text.split('def op_tool_run(args):', 1)[-1].split('\ndef _asset_id_map', 1)[0]
    problems = []
    construct = body.find('runner = ToolRunner(')
    for marker in ('if not allow:', 'confirm != task', "op_tool_plan({'task': task})"):
        position = body.find(marker)
        if construct < 0 or position < 0 or position > construct:
            problems.append(f'独立工具必须先验证授权/注册再构造: {marker}')
    for marker in ("runner.run(plan['method'], skip_first_screenshot=True)",
                   'class ToolRunner(AzurLaneAutoScript):', '_device_engine(config=self.config)'):
        if marker not in body:
            problems.append(f'独立工具偏离上游分派/设备语义: {marker}')
    domain = read('src/Alas.Core/Tasks/ToolRunTask.cs')
    gate = domain.find('Preconditions(request, context)')
    call = domain.find('CallTyped<JsonObject>("tool_run"')
    if gate < 0 or call < gate or not all(item in domain for item in
                                        ('context.Options.DryRun', 'context.Options.AllowActions')):
        problems.append('独立工具缺少调用前的会话级授权检查')
    if '.Register(new ToolRunTask())' not in read('src/Alas.Core/Runtime/QueueExecution.cs'):
        problems.append('独立工具未注册到 Core 通用任务队列')
    return problems


def native_scheduler_boundary_intact() -> list[str]:
    text = (ROOT / 'tools/native_scheduler.py').read_text(encoding='utf-8')
    problems = []
    for marker in ('class SchedulerRunner(AzurLaneAutoScript):', 'runner.loop()',
                   'return super().wait_until(future)',
                   'super().run(command, skip_first_screenshot=skip_first_screenshot)',
                   "object.__setattr__(config, 'stop_event', stop)"):
        if marker not in text:
            problems.append(f'调度器没有保持原生循环/事件/分派语义: {marker}')
    if 'def loop(' in text or 'def get_next_task(' in text:
        problems.append('调度器适配器不得复制原生任务选择或循环状态机')
    domain = (ROOT / 'src/Alas.Core/Tasks/SchedulerRunTask.cs').read_text(encoding='utf-8')
    if not all(marker in domain for marker in
               ('context.Options.DryRun', 'context.Options.AllowActions', 'token.Register',
                'stop.request', 'CallTyped<JsonObject>("scheduler_run"', '"stop_observed"')):
        problems.append('Core 调度器缺少授权/停止/确认边界')
    return problems


def cli_task_boundary_intact() -> list[str]:
    """CLI 只解析队列文件路径和公共参数；运行时持有会话与 runner 注册。

    `run` / `goto` 曾各自在 CLI 分支解析业务字段并直接构造会话和任务对象。表面上它们
    也调用 TaskQueue，实际却保留了第二套任务入口和状态边界；参数遗漏还能在任务校验前
    触及设备。兼容命令只能给出迁移提示，具体 `observe` / `navigate` 输入必须进队列 JSON。
    """
    path = ROOT / "src/Alas.DataTool/Program.cs"
    if not path.is_file():
        return ["缺少 Program.cs，无法检查 CLI 任务边界"]
    text = path.read_text(encoding="utf-8")

    def command_block(command: str) -> str:
        marker = f'if (command == "{command}")'
        start = text.find(marker)
        if start < 0:
            return ""
        next_branch = text.find('\n            if (command == "', start + len(marker))
        return text[start:next_branch if next_branch >= 0 else len(text)]

    problems = []
    queue = command_block("queue")
    required_queue_markers = ("QueueExecution.RunFile", "ParseRunFlags")
    missing = [marker for marker in required_queue_markers if marker not in queue]
    if missing:
        problems.append(f"queue 入口没有委托运行时执行: {missing}")
    leaked_queue = [marker for marker in (
        "TaskQueueFile.Parse", "AlasSession.Start", "new Alas.Tasks.TaskQueue",
        ".Register(", "ReadCompletedState", "LatestState",
    ) if marker in queue]
    if leaked_queue:
        problems.append(f"queue CLI 分支仍驱动会话或断点状态: {leaked_queue}")

    forbidden = (
        "TaskRequest", "TaskQueue", "AlasSession.Start", "ObserveTask", "NavigateTask",
        '"tick_seconds"', '"seconds"', '"map"', '"max_hops"', '"rounds"',
    )
    for command in ("run", "goto"):
        body = command_block(command)
        if not body:
            problems.append(f"缺少 {command} 兼容入口；请保留明确的 queue 迁移提示")
            continue
        leaked = [marker for marker in forbidden if marker in body]
        if leaked:
            problems.append(f"{command} CLI 分支仍解释任务内容或驱动会话: {leaked}")
        if "已弃用" not in body or "queue --file" not in body:
            problems.append(f"{command} CLI 分支没有明确指向 queue --file 的弃用提示")
    return problems

def task_domain_registration() -> list[str]:
    """每个任务域都必须在运行时队列入口注册。

    这类漏挂在静态上就能查出来，不必等到某次队列跑起来才发现：
    扫 `Tasks/*Task.cs` 里实现 `ITaskRunner` 的类，要求 `QueueExecution.cs` 里有对应的
    `new …<类名>()` 注册语句（注册用的是类，不是 `Kind` 字面量）。
    """
    problems: list[str] = []
    runtime = ROOT / "src/Alas.Core/Runtime/QueueExecution.cs"
    tasks = ROOT / "src/Alas.Core/Tasks"
    if not runtime.is_file() or not tasks.is_dir():
        return problems
    text = runtime.read_text(encoding="utf-8")
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
            problems.append(f"任务域 {name}（{path.name}）没有在 QueueExecution.cs 里注册")
        else:
            registered += 1
    if registered == 0 and not problems:
        problems.append("任务域扫描不到任何 ITaskRunner 实现（检查 Tasks/ 目录）")
    return problems


def device_checklist_integrity() -> list[str]:
    """真机清单保留新入口待验项，并禁止周期任务被冒烟脚本自动执行。

    本局撤退证据已经归档；当前欠账是新队列入口的观测、导航、通关与
    周期任务原生调度回归。动作任务仍须显式授权。

    用**字面锁定**而不是语义分析：这里要的就是"改动必须显式且被看见"，
    谁要动这两条，就得同时改这个守卫，改动天然进入审阅视野。
    """
    path = ROOT / "tools/diagnostics/device_smoke.py"
    if not path.is_file():
        return ["缺少真机清单: tools/diagnostics/device_smoke.py"]
    text = path.read_text(encoding="utf-8")
    problems = []
    for phrase, why in (
        ("--read-only-device", "只读抓帧必须能独立于动作授权运行"),
        ("当前队列入口的导航真机回归", "新入口导航证据仍待补"),
        ("用新运行时跑一次真机通关", "新入口成功结算证据仍待补"),
        ("【需本人授权】", "周期任务真跑必须标注需授权"),
        ("不自动执行周期任务", "冒烟脚本不能自行执行周期动作"),
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
        "控制工作区运行时": ROOT / "src/Alas.Core/Runtime/ControlWorkspace.cs",
        "Kestrel 控制传输层": ROOT / "src/Alas.Server/ControlServer.cs",
        "共享控制合同": ROOT / "src/Alas.Contracts/ControlModels.cs",
        "共享控制客户端": ROOT / "src/Alas.Client/ControlClient.cs",
        "控制状态事件流": ROOT / "src/Alas.Server/ControlStateFeed.cs",
        "并发工件读取": ROOT / "src/Alas.Core/Runtime/ArtifactReader.cs",
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
    device_check = read("src/Alas.DataTool/DeviceCheck.cs")
    real_device_check = device_check.split("public static int RunReal(", 1)[-1].split(
        "public static int Run(", 1)[0]
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
        "观测任务只在队列入口注册": "RunLoop.Run" not in program
                                   and not (ROOT / "src/Alas.DataTool/RunLoop.cs").exists()
                                   and "new ObserveTask()" in read("src/Alas.Core/Runtime/QueueExecution.cs"),
        "导航动作检查会话授权": all(marker in read("src/Alas.Core/Tasks/NavigateTask.cs")
                                  for marker in ("context.Options.DryRun",
                                                 "context.Options.AllowActions",
                                                 "context.Session.DeviceConfigureCount")),
        "导航任务只在队列入口注册": "DeviceCheck.RunGoto" not in program
                                   and "public static int RunGoto" not in read("src/Alas.DataTool/DeviceCheck.cs")
                                   and "new NavigateTask()" in read("src/Alas.Core/Runtime/QueueExecution.cs"),
        "导航生产路径调用上游 UI": '"ui_ensure"' in read("src/Alas.Core/Tasks/NavigateTask.cs")
                              and "PageNavigator(" not in read("src/Alas.Core/Tasks/NavigateTask.cs")
                              and "ui.ui_ensure(destination" in read("tools/alas_vision.py"),
        "页面识别直接调用上游判据": "UI.ui_page_appear(main, page, offset=offset)" in read("tools/alas_vision.py")
                               and "def _variants(" not in read("tools/alas_vision.py"),
        "关卡入口不运行固定坐标旁路": not any(marker in read("tools/alas_vision.py") for marker in (
            "_UNFINISHED_RED_BOX", "_proactive_abort_worker", "enter_with_dialog_handler")),
        "C# 不保留独立导航点击算法": not any("class PageNavigator" in p.read_text(encoding="utf-8")
            for p in (ROOT / "src/Alas.Core/Navigation").glob("*.cs")),
        "服务器切换由上游释放全部资源": "server_module.set_server(s)" in read("tools/alas_vision.py"),
        "控件发现来自上游继承声明": "discover_controls(FORK)" in read("tools/alas_vision.py")
                               and (ROOT / "tools/ui_rule_catalog.py").is_file(),
        "全量原生规则覆盖进入总验收": "verify_upstream_coverage.py" in read("tools/diagnostics/verify_all.py"),
        "旧控件逐页驱动不再执行": not any(marker in read("tools/diagnostics/verify_controls.py")
            for marker in ("import alas_vision", "import subprocess", "click_xy", "PLAN =", "def swipe(")),
        "旧页面逐段驱动不再执行": not any(marker in read(path)
            for path in ("tools/diagnostics/verify_page.py", "tools/diagnostics/verify_pages.py")
            for marker in ("import alas_vision", "import subprocess", "def tap(", "def candidates(", "TARGET_BUTTON")),
        "真机设备诊断只读": "public static int RunReal(" in device_check
                          and "public static int Run(" in device_check
                          and not any(action in real_device_check for action in (
                              "device.Click(", "device.Swipe(", "device.Back(",
                              "AssetButtonCenter(", "ConfigureEngineDevice(")),
        # R2：任务域走通用任务模型，CLI 只解析队列文件（不解释任务内容）。
        "任务队列入口": "QueueExecution.RunFile" in program
                          and "TaskQueueFile.Parse" in read("src/Alas.Core/Runtime/QueueExecution.cs"),
        "参数解析共享": program.count("ParseRunFlags(") >= 2,
        "任务模型是接口": "interface ITaskRunner" in read("src/Alas.Core/Tasks/TaskModel.cs"),
        "控制队列编排归运行时": "QueueExecution.RunFile" in read("src/Alas.Core/Runtime/ControlWorkspace.cs")
                                  and "QueueExecution.RunFile" not in read("src/Alas.Server/ControlServer.cs")
                                  and not (ROOT / "src/Alas.DataTool/ControlServer.cs").exists(),
        "控制服务无 UI 引用": "Microsoft.AspNetCore.App" in read("src/Alas.Server/Alas.Server.csproj")
                              and "Alas.UI" not in read("src/Alas.Server/Alas.Server.csproj")
                              and "Avalonia" not in read("src/Alas.Server/Alas.Server.csproj"),
        "共享客户端与合同不依赖宿主或 UI": all(
            forbidden not in read(project)
            for project in ("src/Alas.Client/Alas.Client.csproj", "src/Alas.Contracts/Alas.Contracts.csproj")
            for forbidden in ("Alas.Core", "Alas.Server", "Alas.UI", "Avalonia", "Microsoft.AspNetCore")),
        # UI transport boundary: native desktop composition calls Core directly;
        # only the browser adapter is allowed to depend on ControlClient/HTTP.
        "桌面 UI 直接调用 Core": "Alas.Runtime" in read("src/Alas.UI.Desktop/DirectCoreBackend.cs")
                                 and not any(marker in read("src/Alas.UI.Desktop/DirectCoreBackend.cs")
                                             for marker in ("ControlClient", "HttpClient", "http://", "https://", "/api/")),
        "浏览器 UI 才使用网络适配器": "ControlClient" in read("src/Alas.UI.Browser/BrowserControlBackend.cs")
                                     and "../Alas.Client/Alas.Client.csproj" in read("src/Alas.UI.Browser/Alas.UI.Browser.csproj"),
        "UI 共享层不绑定传输": "ControlClient" not in read("src/Alas.UI/Alas.UI.csproj")
                              and "Alas.Server" not in read("src/Alas.UI/Alas.UI.csproj"),
        "路线记录 Core 与传输边界": "桌面 UI 在同一进程内通过能力接口调用 Core" in read("docs/architecture-roadmap.md"),
    }
    for label, ok in checks.items():
        if not ok:
            problems.append(f"架构入口缺失: {label}")

    problems.extend(contract_consistency())
    problems.extend(task_domain_registration())
    problems.extend(withdraw_hook_present())
    problems.extend(periodic_run_gate_intact())
    problems.extend(periodic_run_session_gate_intact())
    problems.extend(native_tool_boundary_intact())
    problems.extend(native_scheduler_boundary_intact())
    problems.extend(cli_task_boundary_intact())
    problems.extend(campaign_shims_installed())
    problems.extend(shims_all_called())
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
