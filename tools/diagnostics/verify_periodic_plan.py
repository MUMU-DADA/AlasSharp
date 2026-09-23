# -*- coding: utf-8 -*-
"""周期任务勘察验收：`periodic_plan`（只读，报"跑某任务会去跑哪个类"）。

三段：

1. **与独立读数对拍**：op 报的"导入 + 类名 + 行号"与脚本**自己再读一遍** `alas.py` 的
   结果逐项一致 —— 单侧读错不会两边一起错。
2. **边界**：不存在的任务名 → `found=false` + 明确原因；空任务名 → 明确报错（都不许兜底）。
3. **只读保证**：调用前后 `sys.modules` 里**不该多出目标模块** —— 这条比"我们没有 import"的
   声明硬：如果哪天有人在 op 里加了 import，这条会红。

用法：
    python tools/diagnostics/verify_periodic_plan.py
"""

from __future__ import annotations

import ast
import importlib
import json
import os
import sys
import tempfile
import types
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
sys.path.insert(0, str(ROOT / 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

os.chdir(ENGINE)
import alas_vision as av                                        # noqa: E402

TASKS = ['commission', 'research', 'dorm', 'reward']


def independent_read(task: str):
    """独立实现：从 alas.py 里读同名方法的行号与导入（不 import 任何目标模块）。"""
    with open(Path(ENGINE) / 'alas.py', encoding='utf-8') as stream:
        tree = ast.parse(stream.read())
    for node in ast.walk(tree):
        if isinstance(node, ast.FunctionDef) and node.name == task:
            imports = sorted(
                f'from {n.module or ""} import {a.name}'
                for n in ast.walk(node) if isinstance(n, ast.ImportFrom)
                for a in n.names)
            return {'lineno': node.lineno, 'imports': imports}
    return None


def call(args):
    return call_op('periodic_plan', args)


def call_op(op, args):
    response = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': op, 'args': args})))
    assert response.get('ok'), response
    return response['result']


def check(failures, name, passed, detail=''):
    print(f"  {'ok  ' if passed else 'FAIL'} {name}" + ('' if passed else f'  ← {detail}'))
    if not passed:
        failures.append(f'{name}: {detail}')


def verify_native_dispatch(failures):
    """执行适配器的全离线行为验收。

    这里故意调用真实 ``AzurLaneAutoScript.run`` 及其 ``reward`` / ``opsi_explore`` /
    ``event`` 方法，只把方法内部 import 的目标类、配置工厂与共享设备换成替身。因此能抓出
    “从 AST 找到第一个类后统一 ``instance.run()``”这类偏离上游入口的实现，同时不会连接设备。
    """
    print()
    print('=== 通用上游任务执行适配器（真实 alas.py 分派 + 全替身依赖）===')

    mapping = [
        ('Reward', 'Reward', 'reward'), ('reward', 'Reward', 'reward'),
        ('OpsiExplore', 'OpsiExplore', 'opsi_explore'),
        ('opsi_explore', 'OpsiExplore', 'opsi_explore'),
        ('Event', 'Event', 'event'), ('event', 'Event', 'event'),
        ('Restart', 'Restart', 'restart'), ('restart', 'Restart', 'restart'),
    ]
    for supplied, command, method in mapping:
        got = call({'task': supplied})
        check(failures, f'{supplied} → {command}/{method}',
              got.get('found') is True
              and got.get('scheduler_command') == command
              and got.get('method') == method, f'{got}')
    for helper in ('save_error_log', 'loop'):
        got = call({'task': helper})
        check(failures, f'辅助方法 {helper} 不可作为调度任务',
              got.get('found') is False and bool(got.get('error')), f'{got}')

    for supplied in ('false', 1, [], {'enabled': True}):
        args = {'task': 'reward', 'allow_actions': supplied, 'confirm': 'reward'}
        preflight = call_op('periodic_preflight', args)
        check(failures, f'非布尔授权值 {supplied!r} 不得通过放行判定',
              preflight.get('decision') == 'denied'
              and preflight.get('allow_actions') is False
              and 'plan' not in preflight, f'{preflight}')

    state = types.SimpleNamespace(
        configs=[], overrides=[], calls=[], task_calls=[], screenshots=0,
        mode='success', direct_override_sets=0)
    original_device_config = object()

    class FakeConfig:
        def __init__(self, config_name='alas', task=None):
            object.__setattr__(self, '_ready', False)
            object.__setattr__(self, 'config_name', config_name)
            object.__setattr__(self, 'task', types.SimpleNamespace(command=task or 'Alas'))
            object.__setattr__(self, 'Campaign_Name', 'd3')
            object.__setattr__(self, 'Campaign_Event', 'event_fixture')
            object.__setattr__(self, 'Campaign_Mode', 'normal')
            object.__setattr__(self, 'Error_OnePushConfig', '')
            object.__setattr__(self, 'Error_SaveError', False)
            object.__setattr__(self, 'bound', {'BuyFurniture_Enable': 'Dorm.BuyFurniture.Enable'})
            object.__setattr__(self, '_ready', True)
            state.configs.append((config_name, task, self))

        def __setattr__(self, key, value):
            if getattr(self, '_ready', False) and key == 'BuyFurniture_Enable':
                state.direct_override_sets += 1
            object.__setattr__(self, key, value)

        def override(self, **kwargs):
            state.overrides.append(dict(kwargs))
            for key, value in kwargs.items():
                # 模拟上游 override 的非持久化赋值：绕过 AzurLaneConfig.__setattr__。
                object.__setattr__(self, key, value)

        def task_call(self, task):
            state.task_calls.append(task)

    class FakeDevice:
        def __init__(self):
            self.config = original_device_config

        def stuck_record_clear(self):
            state.calls.append(('device.stuck_record_clear',))

        def click_record_clear(self):
            state.calls.append(('device.click_record_clear',))
            if state.mode == 'pre_dispatch_failure':
                raise RuntimeError('click history reset failed')

        def screenshot(self):
            state.screenshots += 1

    device = FakeDevice()

    from module.config.config import TaskEnd
    from module.exception import GameNotRunningError, RequestHumanTakeover

    class FakeReward:
        def __init__(self, config, device):
            state.calls.append(('reward.construct', config.task.command, device is globals_device[0]))

        def run(self):
            state.calls.append(('reward.run',))
            if state.mode == 'task_end':
                raise TaskEnd
            if state.mode == 'false':
                raise GameNotRunningError('offline fixture')
            if state.mode == 'system_exit':
                raise RequestHumanTakeover

    class FakeOSCampaignRun:
        def __init__(self, config, device):
            self.config = config
            state.calls.append(('opsi.construct', config.task.command, device is globals_device[0]))

        def run(self):
            raise AssertionError('opsi_explore 不得被改写成 instance.run()')

        def opsi_explore(self):
            state.calls.append(('opsi_explore',))

    class FakeCampaignRun:
        def __init__(self, config, device):
            self.config = config
            state.calls.append(('event.construct', config.task.command, device is globals_device[0]))

        def run(self, name, folder='campaign_main', mode='normal', total=0):
            state.calls.append(('event.run', name, folder, mode, total))

    globals_device = [device]
    fake_modules = {
        'module.reward.reward': ('Reward', FakeReward),
        'module.campaign.os_run': ('OSCampaignRun', FakeOSCampaignRun),
        'module.campaign.run': ('CampaignRun', FakeCampaignRun),
    }
    saved_modules = {name: sys.modules.get(name) for name in fake_modules}
    config_module = importlib.import_module('module.config.config')
    device_module = importlib.import_module('module.device.device')
    native_alas = importlib.import_module('alas')
    saved_config = config_module.AzurLaneConfig
    saved_alas_config = native_alas.AzurLaneConfig
    saved_device_class = device_module.Device
    saved_device_engine = av._device_engine
    saved_notify = native_alas.handle_notify

    try:
        for name, (attribute, cls) in fake_modules.items():
            module = types.ModuleType(name)
            setattr(module, attribute, cls)
            sys.modules[name] = module
        config_module.AzurLaneConfig = FakeConfig
        native_alas.AzurLaneConfig = FakeConfig
        device_module.Device = lambda config: device
        av._device_engine = lambda: device
        native_alas.handle_notify = lambda *args, **kwargs: None

        with tempfile.TemporaryDirectory(prefix='alas-periodic-native-') as tmp:
            config_file = Path(tmp) / 'alas.json'
            config_file.write_bytes(b'{"sentinel":"unchanged"}\n')
            before = config_file.read_bytes()

            # 闸门与 resolver 都必须在配置、设备及上游对象构造之前完成。
            denied_cases = [
                {'task': 'reward', 'confirm': 'reward'},
                {'task': 'reward', 'allow_actions': True, 'confirm': 'research'},
                {'task': 'no_such_task_zzz', 'allow_actions': True,
                 'confirm': 'no_such_task_zzz'},
                {'task': 'reward', 'allow_actions': True, 'confirm': 'reward',
                 'overrides': []},
            ]
            for args in denied_cases:
                configs_before, calls_before = len(state.configs), len(state.calls)
                got = call_op('periodic_run', args)
                check(failures, f"拒绝路径零构造: {args['task']}",
                      got.get('decision') == 'denied'
                      and len(state.configs) == configs_before
                      and len(state.calls) == calls_before
                      and device.config is original_device_config, f'{got}')

            for supplied in ('false', 1, [], {'enabled': True}):
                configs_before, calls_before = len(state.configs), len(state.calls)
                got = call_op('periodic_run', {
                    'task': 'reward', 'allow_actions': supplied, 'confirm': 'reward'})
                check(failures, f'非布尔授权值 {supplied!r} 不得执行',
                      got.get('decision') == 'denied'
                      and got.get('allow_actions') is False
                      and 'plan' not in got
                      and len(state.configs) == configs_before
                      and len(state.calls) == calls_before, f'{got}')

            state.mode = 'success'
            reward = call_op('periodic_run', {
                'task': 'reward', 'allow_actions': True, 'confirm': 'reward',
                'overrides': {'BuyFurniture_Enable': True},
            })
            check(failures, 'reward 走原生 Reward.run',
                  reward.get('decision') == 'ran'
                  and reward.get('native_success') is True
                  and ('reward.run',) in state.calls, f'{reward} calls={state.calls}')
            check(failures, 'Reward 配置按 Scheduler Command 绑定',
                  any(task == 'Reward' for _, task, _ in state.configs), f'{state.configs}')
            check(failures, 'overrides 使用 config.override 且不直接 setattr',
                  state.overrides == [{'BuyFurniture_Enable': True}]
                  and state.direct_override_sets == 0, f'overrides={state.overrides} '
                  f'direct={state.direct_override_sets}')
            check(failures, 'overrides 不改 fake 配置文件字节',
                  config_file.read_bytes() == before, f'{config_file.read_bytes()!r}')
            check(failures, '成功后恢复共享 device.config',
                  device.config is original_device_config, f'{device.config!r}')

            opsi = call_op('periodic_run', {
                'task': 'OpsiExplore', 'allow_actions': True, 'confirm': 'OpsiExplore'})
            check(failures, 'OpsiExplore 走原生 opsi_explore（不是 run）',
                  opsi.get('decision') == 'ran' and opsi.get('native_success') is True
                  and ('opsi_explore',) in state.calls, f'{opsi} calls={state.calls}')

            event = call_op('periodic_run', {
                'task': 'event', 'allow_actions': True, 'confirm': 'event'})
            check(failures, 'Event 原生转发 name/folder/mode',
                  event.get('decision') == 'ran' and event.get('native_success') is True
                  and ('event.run', 'd3', 'event_fixture', 'normal', 0) in state.calls,
                  f'{event} calls={state.calls}')
            check(failures, '三类配置工厂收到规范任务名',
                  {'Reward', 'OpsiExplore', 'Event'} <=
                  {task for _, task, _ in state.configs}, f'{state.configs}')

            state.mode = 'task_end'
            ended = call_op('periodic_run', {
                'task': 'reward', 'allow_actions': True, 'confirm': 'reward'})
            check(failures, 'TaskEnd 沿原生 dispatcher 记成功',
                  ended.get('decision') == 'ran' and ended.get('native_success') is True,
                  f'{ended}')

            state.mode = 'false'
            false_result = call_op('periodic_run', {
                'task': 'reward', 'allow_actions': True, 'confirm': 'reward'})
            check(failures, '原生 dispatcher 返回 False 不得算成功',
                  false_result.get('decision') != 'ran'
                  and false_result.get('native_success') is False, f'{false_result}')
            check(failures, 'False 后恢复共享 device.config',
                  device.config is original_device_config, f'{device.config!r}')

            state.mode = 'pre_dispatch_failure'
            calls_before = len(state.calls)
            pre_dispatch = call_op('periodic_run', {
                'task': 'reward', 'allow_actions': True, 'confirm': 'reward'})
            check(failures, '原生调用前失败不得声称已运行',
                  pre_dispatch.get('decision') == 'error'
                  and pre_dispatch.get('ran') is not True
                  and pre_dispatch.get('native_success') is False
                  and ('reward.run',) not in state.calls[calls_before:], f'{pre_dispatch}')
            check(failures, '原生调用前失败后恢复共享 device.config',
                  device.config is original_device_config, f'{device.config!r}')

            state.mode = 'system_exit'
            try:
                exit_result = call_op('periodic_run', {
                    'task': 'reward', 'allow_actions': True, 'confirm': 'reward'})
                escaped = False
            except SystemExit as error:
                exit_result, escaped = {'escaped': repr(error)}, True
            check(failures, 'SystemExit 转结构化失败且不退出宿主',
                  not escaped and exit_result.get('decision') != 'ran'
                  and exit_result.get('native_success') is False, f'{exit_result}')
            check(failures, 'SystemExit 后恢复共享 device.config',
                  device.config is original_device_config, f'{device.config!r}')
            still_alive = call({'task': 'reward'})
            check(failures, 'SystemExit 后宿主仍可继续响应',
                  still_alive.get('found') is True, f'{still_alive}')
    finally:
        config_module.AzurLaneConfig = saved_config
        native_alas.AzurLaneConfig = saved_alas_config
        device_module.Device = saved_device_class
        av._device_engine = saved_device_engine
        native_alas.handle_notify = saved_notify
        for name, previous in saved_modules.items():
            if previous is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = previous


def main() -> int:
    failures: list[str] = []
    print('=== 与独立读数对拍 ===')
    for task in TASKS:
        mine = independent_read(task)
        got = call({'task': task})
        if mine is None:
            ok = got.get('found') is False
            detail = f"独立读也找不到；op={got}"
        else:
            ok = (got.get('found') is True and got.get('lineno') == mine['lineno']
                  and sorted(got.get('imports') or []) == mine['imports'])
            detail = (f"op(lineno={got.get('lineno')}, imports={got.get('imports')}) "
                      f"独立(lineno={mine['lineno']}, imports={mine['imports']})")
        print(f"  {'ok  ' if ok else 'FAIL'} {task:12s} {'' if ok else '← ' + detail}")
        if not ok:
            failures.append(f'{task}: {detail}')

    print()
    print('=== 边界 ===')
    unknown = call({'task': 'no_such_task_zzz'})
    empty = call({})
    boundary = [
        ('不存在的任务名 → found=false + 原因',
         unknown.get('found') is False and bool(unknown.get('error')),
         f'{unknown}'),
        ('空任务名 → 明确报错（不兜底）',
         bool(empty.get('error')) and empty.get('found') is None,
         f'{empty}'),
    ]
    for name, ok, detail in boundary:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
        if not ok:
            failures.append(f'{name}: {detail}')

    print()
    print('=== 只读保证（调用前后不该多出目标模块）===')
    watch = ['module.commission.commission', 'module.research.research']
    before = {name for name in watch if name in sys.modules}
    call({'task': 'commission'})
    call({'task': 'research'})
    after = {name for name in watch if name in sys.modules}
    ok = before == after
    print(f"  {'ok  ' if ok else 'FAIL'} 未新导入目标模块（{sorted(after) or '无'}）")
    if not ok:
        failures.append(f'op 引入了目标模块: {sorted(after - before)}')

    verify_native_dispatch(failures)

    # ---- 任务侧：走队列（产品路径），断言结论与证据；输入有错必须 failed 而不是"部分成功"
    print()
    print('=== 任务侧（kind = periodic_plan，走队列）===')
    exe = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net10.0' / 'alashub.exe'
    if not exe.is_file():
        print(f'[跳过] 未构建 {exe.relative_to(ROOT)}（先 dotnet build）—— 任务侧未验。')
    else:
        import subprocess
        import tempfile
        with tempfile.TemporaryDirectory(prefix='alas-periodic-') as tmp:
            tmpdir = Path(tmp)
            good = tmpdir / 'good.json'
            good.write_text(json.dumps({'tasks': [
                {'id': 'plan', 'kind': 'periodic_plan', 'input': {'tasks': ['commission']}}]}),
                encoding='utf-8')
            bad = tmpdir / 'bad.json'
            bad.write_text(json.dumps({'tasks': [
                {'id': 'plan-bad', 'kind': 'periodic_plan',
                 'input': {'tasks': ['commission', 'no_such_task_zzz']}}]}), encoding='utf-8')

            def run_queue(queue_file: Path, artifacts: Path):
                proc = subprocess.run([str(exe), 'queue', '--file', str(queue_file),
                                '--artifacts', str(artifacts)],
                               capture_output=True, text=True, encoding='utf-8',
                               errors='replace', timeout=300)
                artifact = next(iter(sorted(artifacts.glob('*/task-*.json'))), None)
                return json.loads(artifact.read_text(encoding='utf-8')) if artifact else {}

            ok_doc = run_queue(good, tmpdir / 'art-good')
            bad_doc = run_queue(bad, tmpdir / 'art-bad')
            ok_evidence = ok_doc.get('evidence') or {}
            bad_evidence = bad_doc.get('evidence') or {}
            plan = (ok_evidence.get('plans') or [{}])[0]
            expected = independent_read('commission') or {}
            task_checks = [
                ('全查到 → succeeded', ok_doc.get('outcome') == 'succeeded',
                 f"outcome={ok_doc.get('outcome')}"),
                ('证据里的行号与独立读一致', plan.get('lineno') == expected.get('lineno'),
                 f"任务={plan.get('lineno')} 独立={expected.get('lineno')}"),
                ('证据带上游 Scheduler.Command 与原生方法',
                 plan.get('scheduler_command') == 'Commission'
                 and plan.get('method') == 'commission', f'{plan}'),
                ('证据带导入与 run 标记',
                 bool(plan.get('imports')) and plan.get('calls_run') is True, f'{plan}'),
                ('有查不到的名字 → failed（不是部分成功）',
                 bad_doc.get('outcome') == 'failed'
                 and bad_evidence.get('missing') == ['no_such_task_zzz'],
                 f"outcome={bad_doc.get('outcome')} missing={bad_evidence.get('missing')}"),
                ('失败原因点名了是哪个任务',
                 'no_such_task_zzz' in str(bad_doc.get('error') or ''),
                 f"error={bad_doc.get('error')}"),
            ]
            for name, ok, detail in task_checks:
                print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
                if not ok:
                    failures.append(f'{name}: {detail}')

    # ---- 放行判定（periodic_preflight）：四条路径 + "永不执行"这条不变量
    print()
    print('=== 放行判定（kind = periodic_preflight）===')
    if not exe.is_file():
        print('[跳过] 未构建 alashub —— 放行判定未验。')
    else:
        import subprocess
        import tempfile
        with tempfile.TemporaryDirectory(prefix='alas-preflight-') as tmp:
            tmpdir = Path(tmp)
            cases = {
                'no-auth': {'task': 'reward'},
                'bad-confirm': {'task': 'reward', 'allow_actions': True, 'confirm': 'research'},
                'unknown': {'task': 'no_such_task_zzz', 'allow_actions': True,
                            'confirm': 'no_such_task_zzz'},
                'allowed': {'task': 'reward', 'allow_actions': True, 'confirm': 'reward'},
            }
            queue_file = tmpdir / 'queue.json'
            queue_file.write_text(json.dumps({'tasks': [
                {'id': key, 'kind': 'periodic_preflight', 'input': value}
                for key, value in cases.items()] + [
                # 执行入口也放进来：**只放未授权的那条** —— 它会被闸门挡下、不会真的执行
                {'id': 'run-no-auth', 'kind': 'periodic_run', 'input': {'task': 'reward'}},
                # 队列 JSON 不能自行把默认 dry-run 会话升级成动作会话。即使任务输入的
                # 两道宿主闸门都满足，会话没有 --run --allow-actions 仍必须在 C# 侧拒绝。
                {'id': 'run-session-bypass', 'kind': 'periodic_run',
                 'input': {'task': 'reward', 'allow_actions': True, 'confirm': 'reward'}},
                {'id': 'run-session-required', 'kind': 'periodic_run', 'required': True,
                 'input': {'task': 'reward', 'allow_actions': True, 'confirm': 'reward'}},
                {'id': 'run-invalid-allow', 'kind': 'periodic_run',
                 'input': {'task': 'reward', 'allow_actions': 'false', 'confirm': 'reward'}},
                {'id': 'run-invalid-confirm', 'kind': 'periodic_run',
                 'input': {'task': 'reward', 'allow_actions': True, 'confirm': 7}},
                {'id': 'preflight-invalid-allow', 'kind': 'periodic_preflight',
                 'input': {'task': 'reward', 'allow_actions': 'false', 'confirm': 'reward'}},
                {'id': 'preflight-invalid-confirm', 'kind': 'periodic_preflight',
                 'input': {'task': 'reward', 'allow_actions': True, 'confirm': 7}},
            ]}, ensure_ascii=False), encoding='utf-8')
            artifacts = tmpdir / 'artifacts'
            proc = subprocess.run([str(exe), 'queue', '--file', str(queue_file),
                            '--artifacts', str(artifacts), '--continue-on-error'],
                           capture_output=True, text=True, encoding='utf-8',
                           errors='replace', timeout=300)
            docs = {}
            for artifact in artifacts.glob('*/task-*.json'):
                document = json.loads(artifact.read_text(encoding='utf-8'))
                docs[artifact.name.replace('task-', '').replace('.json', '')] = document

            def decision(key):
                evidence = (docs.get(key) or {}).get('evidence') or {}
                return (docs.get(key) or {}).get('outcome'), evidence

            gate_checks = []
            # 没有会话授权是前置条件不满足：未运行，不伪装成执行失败。
            run_outcome = (docs.get('run-no-auth') or {}).get('outcome')
            gate_checks.append((
                'periodic_run 会话未授权 → skipped 且保留前置条件原因',
                run_outcome == 'skipped'
                and (docs.get('run-no-auth') or {}).get('unmet_preconditions')
                and '--run --allow-actions' in (proc.stdout or ''),
                f"outcome={run_outcome}"))
            bypass_outcome, bypass_evidence = decision('run-session-bypass')
            gate_checks.append((
                '队列输入不能绕过会话级 dry-run 联锁',
                bypass_outcome == 'skipped'
                and (docs.get('run-session-bypass') or {}).get('stop_reason') == 'precondition'
                and (docs.get('run-session-bypass') or {}).get('boundary_state') is None
                and not bypass_evidence,
                f"outcome={bypass_outcome} evidence={bypass_evidence}"))
            required = docs.get('run-session-required') or {}
            gate_checks.append((
                'required 任务会话未授权 → failed 但未开始执行',
                required.get('outcome') == 'failed'
                and required.get('stop_reason') == 'precondition'
                and required.get('boundary_state') is None,
                f"outcome={required.get('outcome')}"))
            for key, field in (
                ('run-invalid-allow', 'input.allow_actions'),
                ('run-invalid-confirm', 'input.confirm'),
                ('preflight-invalid-allow', 'input.allow_actions'),
                ('preflight-invalid-confirm', 'input.confirm'),
            ):
                doc = docs.get(key) or {}
                gate_checks.append((
                    f'{key} → 输入类型前置条件失败',
                    doc.get('outcome') == 'skipped'
                    and doc.get('stop_reason') == 'precondition'
                    and any(field in reason for reason in doc.get('unmet_preconditions') or []),
                    f"outcome={doc.get('outcome')} unmet={doc.get('unmet_preconditions')}"))
            for key in ('no-auth', 'bad-confirm', 'unknown'):
                outcome, evidence = decision(key)
                gate_checks.append((f'{key} → failed 且 decision=denied',
                                    outcome == 'failed' and evidence.get('decision') == 'denied',
                                    f"outcome={outcome} decision={evidence.get('decision')}"))
            _, allowed_evidence = decision('allowed')
            gate_checks += [
                ('两闸都过 → allowed',
                 allowed_evidence.get('decision') == 'allowed', f'{allowed_evidence}'),
                # 这条是**不变量**：这个 op/任务存在的前提就是它不驱动游戏
                ('四条路径的 executes 全为 False（永不执行）',
                 all((decision(k)[1].get('executes') is False) for k in cases),
                 f"{ {k: decision(k)[1].get('executes') for k in cases} }"),
                ('放行时随附勘察结果（会跑哪个类）',
                 bool((allowed_evidence.get('plan') or {}).get('imports')),
                 f"plan={allowed_evidence.get('plan')}"),
                ('未授权的原因提到 allow_actions',
                 'allow_actions' in str(decision('no-auth')[1].get('reason') or ''),
                 f"reason={decision('no-auth')[1].get('reason')}"),
            ]
            for name, ok, detail in gate_checks:
                print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
                if not ok:
                    failures.append(f'{name}: {detail}')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（读数与独立读一致、边界明确、且不 import 目标模块）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
