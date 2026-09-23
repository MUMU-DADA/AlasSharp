# -*- coding: utf-8 -*-
"""R1 验收：常驻运行时（会话 / 批处理 / 证据 / 取消）。

R1 阶段门槛是三条，都在这里被断言：

1. **同一进程连续运行多个任务时，宿主和设备只初始化一次** ——
   用替身宿主数构造次数（`host_start_count`）与设备配置次数（`device_configure_count`）。
2. **取消、超时和异常都能释放资源并保留证据** ——
   取消在关卡边界生效（进行中的出击不打断）、后面没跑的关卡如实记为跳过、
   宿主被 Dispose、工件与会话日志落盘。
3. **CLI 参数不再复制一套业务状态机** —— CLI 只解析参数与排版；
   编排/判定/工件都在 `Alas.Core/Runtime`。这条另有 `verify_architecture.py` 的静态检查。

做法：把结果合同里的**真实文档**当作上游返回喂给替身宿主，让 C# 运行时按真实路径跑
（批处理、合同裁决、工件落盘、取消与释放全是产品代码），再核对它报出来的事实。
不启动 Python、不连设备。
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

from sortie_contract import CONTRACT                       # noqa: E402

EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'

CLEARED = 'campaign.campaign_main.campaign_1_1'
CLEARED2 = 'campaign.campaign_main.campaign_1_2'
CLEARED3 = 'campaign.campaign_main.campaign_1_3'
BAD_CONTRACT = 'campaign.campaign_main.campaign_2_1'
UPSTREAM_BOOM = 'campaign.campaign_main.campaign_2_2'
WITHDRAWN_CHAPTER = 'campaign.campaign_main.campaign_1_4'


def cleared_document(stage: str, rank: str = 'S', stage_label: str = '1-1') -> dict:
    """一份**过合同**的通关文档（形态与生产方一致）。"""
    return {
        'contract': CONTRACT,
        'chapter': stage,
        'stage': stage_label,
        'dry_run': False,
        'outcome': 'cleared',
        'cleared': True,
        'campaign_end': True,
        'end_reason': 'In stage.',
        'stop_reason': 'cleared',
        'end_evidence': {
            'battle_rank': rank,
            'rank_source': 'BATTLE_STATUS_',
            'combat_status': True,
            'stage_observed': True,
            'expected_end': 'in_stage',
            'withdrawn': False,
            'call_path': ['module.combat.combat.combat_status',
                          'module.handler.enemy_searching.handle_in_stage'],
        },
        'steps': [{'step': 'execute_a_battle', 'round': 1, 'ms': 12.0}],
        'elapsed_s': 61.5,
        'stopped_early': False,
    }


def contract_violation_document() -> dict:
    """campaign_end 单字段声称通关：运行时必须拒绝（R0 口径在 R1 批处理里同样成立）。"""
    return {
        'contract': CONTRACT,
        'chapter': BAD_CONTRACT,
        'stage': '2-1',
        'dry_run': False,
        'outcome': 'cleared',
        'cleared': True,
        'campaign_end': True,
        'steps': [{'step': 'execute_a_battle', 'round': 1, 'ms': 10.0}],
        'elapsed_s': 20.0,
        'stopped_early': False,
    }


def dry_run_document(stage: str) -> dict:
    return {
        'contract': CONTRACT,
        'chapter': stage,
        'stage': '2-1',
        'dry_run': True,
        'cleared': False,
        'plan_steps': ['battle_0', 'battle_2'],
        'semantic_trace': ['battle_default'],
        'tier': 'C',
    }


def build_cases() -> list[dict]:
    return [
        {
            'name': 'dry_run_reads_rules_only',
            'dry_run': True,
            'artifacts': True,
            'chapters': [{'chapter': BAD_CONTRACT, 'document': dry_run_document(BAD_CONTRACT)}],
            'expect': {
                'outcome': 'dry_run',
                'cleared': False,
                'host_start_count': 1,
                'device_configure_count': 0,
                'stages': [{'chapter': BAD_CONTRACT, 'outcome': 'dry_run', 'cleared': False,
                            'failed': False, 'skipped': False, 'violations': []}],
                'artifacts': ['index.json', 'session-log.jsonl', 'sortie-2-1.json'],
            },
        },
        {
            'name': 'live_batch_three_stages_cleared',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'chapters': [
                {'chapter': CLEARED, 'document': cleared_document(CLEARED, 'S', '1-1')},
                {'chapter': CLEARED2, 'document': cleared_document(CLEARED2, 'A', '1-2')},
                {'chapter': CLEARED3, 'document': cleared_document(CLEARED3, 'B', '1-3')},
            ],
            'expect': {
                'outcome': 'cleared',
                'cleared': True,
                'host_start_count': 1,
                'device_configure_count': 1,
                'stages': [
                    {'chapter': CLEARED, 'outcome': 'cleared', 'cleared': True,
                     'failed': False, 'skipped': False, 'violations': []},
                    {'chapter': CLEARED2, 'outcome': 'cleared', 'cleared': True,
                     'failed': False, 'skipped': False, 'violations': []},
                    {'chapter': CLEARED3, 'outcome': 'cleared', 'cleared': True,
                     'failed': False, 'skipped': False, 'violations': []},
                ],
                'artifacts': ['index.json', 'session-log.jsonl',
                              'sortie-1-1.json', 'sortie-1-2.json', 'sortie-1-3.json'],
            },
        },
        {
            'name': 'upstream_error_stops_batch_and_keeps_evidence',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'chapters': [
                {'chapter': CLEARED, 'document': cleared_document(CLEARED)},
                {'chapter': UPSTREAM_BOOM, 'error': 'RuntimeError: fixture upstream failure'},
                {'chapter': CLEARED2, 'document': cleared_document(CLEARED2)},
            ],
            'expect': {
                'outcome': 'error',
                'cleared': False,
                'host_start_count': 1,
                'device_configure_count': 1,
                'stages': [
                    {'chapter': CLEARED, 'outcome': 'cleared', 'cleared': True,
                     'failed': False, 'skipped': False, 'violations': []},
                    {'chapter': UPSTREAM_BOOM, 'outcome': 'error', 'cleared': False,
                     'failed': True, 'skipped': False, 'violations': []},
                    {'chapter': CLEARED2, 'outcome': 'skipped', 'cleared': False,
                     'failed': True, 'skipped': True, 'violations': []},
                ],
                # 失败关与后面被跳过的关都要留档：不能只留成功的那一份。
                'artifacts': ['index.json', 'session-log.jsonl',
                              'sortie-1-1.json', 'sortie-2-2.json', 'sortie-1-2.json'],
            },
        },
        {
            'name': 'contract_violation_stops_batch',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'chapters': [
                {'chapter': BAD_CONTRACT, 'document': contract_violation_document()},
                {'chapter': CLEARED, 'document': cleared_document(CLEARED)},
            ],
            'expect': {
                'outcome': 'error',
                'cleared': False,
                'host_start_count': 1,
                'device_configure_count': 1,
                'stages': [
                    {'chapter': BAD_CONTRACT, 'outcome': 'cleared', 'cleared': False,
                     'failed': True, 'skipped': False,
                     'violations': ['cleared_without_settlement_evidence']},
                    {'chapter': CLEARED, 'outcome': 'skipped', 'cleared': False,
                     'failed': True, 'skipped': True, 'violations': []},
                ],
                'artifacts': ['index.json', 'session-log.jsonl',
                              'sortie-2-1.json', 'sortie-1-1.json'],
            },
        },
        {
            'name': 'cancel_at_stage_boundary_keeps_evidence',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'cancel_after_chapter': CLEARED,
            'chapters': [
                {'chapter': CLEARED, 'document': cleared_document(CLEARED)},
                {'chapter': CLEARED2, 'document': cleared_document(CLEARED2)},
                {'chapter': CLEARED3, 'document': cleared_document(CLEARED3)},
            ],
            'expect': {
                'outcome': 'cancelled',
                'cleared': False,
                'host_start_count': 1,
                'device_configure_count': 1,
                'stages': [
                    {'chapter': CLEARED, 'outcome': 'cleared', 'cleared': True,
                     'failed': False, 'skipped': False, 'violations': []},
                    {'chapter': CLEARED2, 'outcome': 'skipped', 'cleared': False,
                     'failed': True, 'skipped': True, 'violations': []},
                    {'chapter': CLEARED3, 'outcome': 'skipped', 'cleared': False,
                     'failed': True, 'skipped': True, 'violations': []},
                ],
                'artifacts': ['index.json', 'session-log.jsonl', 'sortie-1-1.json'],
            },
        },
        {
            'name': 'continue_on_error_runs_every_stage',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'stop_on_failure': False,
            'chapters': [
                {'chapter': UPSTREAM_BOOM, 'error': 'RuntimeError: fixture upstream failure'},
                {'chapter': CLEARED, 'document': cleared_document(CLEARED)},
            ],
            'expect': {
                'outcome': 'error',
                'cleared': False,
                'host_start_count': 1,
                'device_configure_count': 1,
                'stages': [
                    {'chapter': UPSTREAM_BOOM, 'outcome': 'error', 'cleared': False,
                     'failed': True, 'skipped': False, 'violations': []},
                    {'chapter': CLEARED, 'outcome': 'cleared', 'cleared': True,
                     'failed': False, 'skipped': False, 'violations': []},
                ],
                'artifacts': ['index.json', 'session-log.jsonl',
                              'sortie-2-2.json', 'sortie-1-1.json'],
            },
        },
    ]


def withdrawn_document(stage: str) -> dict:
    """一份**过合同**的撤退文档（R4 门槛：数据面要能区分"成功与撤退"）。"""
    return {
        'contract': CONTRACT,
        'chapter': stage,
        'stage': '1-4',
        'dry_run': False,
        'outcome': 'withdrawn',
        'cleared': False,
        'campaign_end': True,
        'reason': 'ScriptError, No combat executed, Withdrawing',
        'stop_reason': 'withdrawn',
        'end_evidence': {
            'battle_rank': None, 'rank_source': None, 'combat_status': False,
            'stage_observed': False, 'expected_end': None, 'withdrawn': True,
            'call_path': ['module.map.map_operation.withdraw'],
        },
        'steps': [{'step': 'execute_a_battle', 'round': 1, 'ms': 10.0},
                  {'step': 'withdraw', 'ms': 5.0}],
        'elapsed_s': 27.8,
        'stopped_early': True,
    }


def build_queue_cases() -> list[dict]:
    """R2：任务队列。`tasks` 存在时自检走队列路径而不是单批路径。"""
    def campaign(task_id, chapters, **input_overrides):
        payload = {'chapters': chapters}
        payload.update(input_overrides)
        return {'id': task_id, 'kind': 'campaign_batch', 'input': payload}

    return [
        {
            'name': 'queue_two_campaign_tasks_succeeded',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'tasks': [
                dict(campaign('clear-1-1', [CLEARED]),
                     documents={CLEARED: cleared_document(CLEARED, 'S', '1-1')}),
                dict(campaign('clear-1-2', [CLEARED2]),
                     documents={CLEARED2: cleared_document(CLEARED2, 'S', '1-2')}),
            ],
            'expect': {
                'outcome': 'succeeded',
                'host_start_count': 1,
                'device_configure_count': 1,
                'backend_calls': 5,          # 设备配置 1 + 每任务 2 次（边界快照 + s3_run_plan）
                'stopped_early': False,
                'tasks': [
                    {'id': 'clear-1-1', 'outcome': 'succeeded', 'error_kind': 'none'},
                    {'id': 'clear-1-2', 'outcome': 'succeeded', 'error_kind': 'none'},
                ],
                'artifacts': ['queue.json', 'state.json', 'session-log.jsonl',
                              'task-clear-1-1.json', 'task-clear-1-2.json',
                              'sortie-1-1.json', 'sortie-1-2.json'],
            },
        },
        {
            'name': 'queue_precondition_is_skipped_not_failed',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'tasks': [
                # 空章节 = 前置条件不满足：记 skipped，**不**当成失败，也不该调用后端
                campaign('bad-input', []),
                dict(campaign('clear-1-1', [CLEARED]),
                     documents={CLEARED: cleared_document(CLEARED, 'S', '1-1')}),
            ],
            'expect': {
                'outcome': 'partial',
                'host_start_count': 1,
                'device_configure_count': 1,
                'backend_calls': 3,          # 设备配置 1 + 真正跑的那个任务 2 次（没跑的不产生边界快照）
                'stopped_early': False,
                'tasks': [
                    {'id': 'bad-input', 'outcome': 'skipped', 'error_kind': 'none'},
                    {'id': 'clear-1-1', 'outcome': 'succeeded', 'error_kind': 'none'},
                ],
                'artifacts': ['queue.json', 'state.json',
                              'task-bad-input.json', 'task-clear-1-1.json'],
            },
        },
        {
            'name': 'queue_required_precondition_stops_queue',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'tasks': [
                dict(campaign('required-bad', []), required=True),
                dict(campaign('clear-1-1', [CLEARED]),
                     documents={CLEARED: cleared_document(CLEARED, 'S', '1-1')}),
            ],
            'expect': {
                'outcome': 'failed',
                'host_start_count': 1,
                'device_configure_count': 1,
                'backend_calls': 1,          # 只有设备配置；两个任务都没跑
                'stopped_early': True,
                'tasks': [
                    {'id': 'required-bad', 'outcome': 'failed', 'error_kind': 'none'},
                    {'id': 'clear-1-1', 'outcome': 'skipped', 'error_kind': 'none'},
                ],
                'artifacts': ['queue.json', 'state.json',
                              'task-required-bad.json', 'task-clear-1-1.json'],
            },
        },
        {
            'name': 'queue_upstream_failure_stops_and_skips_rest',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'tasks': [
                dict(campaign('boom', [UPSTREAM_BOOM]),
                     errors={UPSTREAM_BOOM: 'RuntimeError: fixture upstream failure'}),
                dict(campaign('clear-1-1', [CLEARED]),
                     documents={CLEARED: cleared_document(CLEARED, 'S', '1-1')}),
            ],
            'expect': {
                'outcome': 'failed',
                'host_start_count': 1,
                'device_configure_count': 1,
                'backend_calls': 3,
                'stopped_early': True,
                'tasks': [
                    {'id': 'boom', 'outcome': 'failed', 'error_kind': 'upstream_error'},
                    {'id': 'clear-1-1', 'outcome': 'skipped', 'error_kind': 'upstream_error'},
                ],
                'artifacts': ['queue.json', 'state.json',
                              'task-boom.json', 'task-clear-1-1.json'],
            },
        },
        {
            'name': 'queue_resume_skips_completed_task',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'resume_completed': ['clear-1-1'],
            'tasks': [
                dict(campaign('clear-1-1', [CLEARED]),
                     documents={CLEARED: cleared_document(CLEARED, 'S', '1-1')}),
                dict(campaign('clear-1-2', [CLEARED2]),
                     documents={CLEARED2: cleared_document(CLEARED2, 'S', '1-2')}),
            ],
            'expect': {
                'outcome': 'partial',
                'host_start_count': 1,
                'device_configure_count': 1,
                'backend_calls': 3,          # 已完成的任务不再调后端；真正跑的任务产生 2 次
                'stopped_early': False,
                'tasks': [
                    {'id': 'clear-1-1', 'outcome': 'skipped', 'error_kind': 'none'},
                    {'id': 'clear-1-2', 'outcome': 'succeeded', 'error_kind': 'none'},
                ],
                'artifacts': ['queue.json', 'state.json',
                              'task-clear-1-1.json', 'task-clear-1-2.json'],
            },
        },
        {
            # R4 门槛：数据面要能**区分成功与撤退** —— 撤退是"跑了但没通关"，
            # 不能标成 upstream_error（那会让前端只能靠猜），所以 error_kind 留 none、
            # 结论放 evidence.batch_outcome。
            'name': 'queue_withdrawn_is_failed_without_error_kind',
            'dry_run': False,
            'allow_actions': True,
            'serial': 'stub-1',
            'artifacts': True,
            'tasks': [
                dict(campaign('withdraw-1-4', [WITHDRAWN_CHAPTER]),
                     documents={WITHDRAWN_CHAPTER: withdrawn_document(WITHDRAWN_CHAPTER)}),
            ],
            'expect': {
                'outcome': 'failed',
                'host_start_count': 1,
                'device_configure_count': 1,
                'backend_calls': 3,      # 设备配置 1 + 边界快照 1 + s3_run_plan 1
                'stopped_early': True,
                'tasks': [{'id': 'withdraw-1-4', 'outcome': 'failed', 'error_kind': 'none'}],
                'artifacts': ['queue.json', 'state.json',
                              'task-withdraw-1-4.json', 'sortie-1-4.json'],
            },
        },
    ]


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先运行 dotnet build）')
        return 1

    cases = build_cases() + build_queue_cases()
    failures = []
    with TemporaryDirectory(prefix='alas-runtime-') as tmp:
        # 小型导航环境的三条用例（替身宿主，离线）—— 规格见 docs/runtime.md 第十五节。
        # 它们钉住两件事：导航任务的多跳/不可达行为，以及第 156 轮那句"入口可能未解锁"的诊断后缀。
        navigate = json.loads((Path(__file__).resolve().parent / 'navigate_cases.json')
                              .read_text(encoding='utf-8'))['cases']
        cases = list(cases) + navigate
        fixture = Path(tmp) / 'runtime-cases.json'
        fixture.write_text(json.dumps({'cases': cases}, ensure_ascii=False, indent=1),
                           encoding='utf-8')
        verdicts = Path(tmp) / 'runtime-verdicts.json'
        proc = subprocess.run([str(EXE), 'selftest-runtime', '--fixture', str(fixture),
                               '--json', str(verdicts)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=180)
        for line in (proc.stdout or '').strip().splitlines():
            print('  ' + line)
        if proc.stderr:
            print('  stderr: ' + proc.stderr.strip()[:400])
        if proc.returncode != 0:
            failures.append(f'selftest-runtime 退出码 {proc.returncode}')
        if not verdicts.is_file():
            print('**失败**：自检没有产出裁决文件')
            return 1

        actual = {c['name']: c for c in json.loads(verdicts.read_text(encoding='utf-8'))['cases']}

    print()
    print('=== 逐例核对（Python 侧判据）===')
    for case in cases:
        name = case['name']
        got = actual.get(name)
        if got is None:
            failures.append(f'{name}: 自检没有这一例')
            continue
        problems = list(got['problems'])
        # 期望值由 Python 说了算：C# 只报事实，判据在这里（与结果合同同样的分工）。
        expect = case['expect']
        for key, want in expect.items():
            if key in ('stages', 'tasks', 'artifacts', 'backend_calls'):
                continue
            have = got.get(key)
            if want != have:
                problems.append(f'{key} 期望 {want} 实为 {have}')
        if 'backend_calls' in expect and expect['backend_calls'] != got['backend_calls']:
            problems.append(f'后端调用次数 期望 {expect["backend_calls"]} 实为 {got["backend_calls"]}')
        for index, want_stage in enumerate(expect.get('stages', [])):
            have_stage = got['stages'][index] if index < len(got['stages']) else None
            if have_stage is None:
                problems.append(f'第{index + 1}关缺失')
                continue
            for key in ('chapter', 'outcome', 'cleared', 'failed', 'skipped'):
                if want_stage.get(key) != have_stage[key]:
                    problems.append(f'第{index + 1}关 {key} 期望 {want_stage.get(key)} '
                                    f'实为 {have_stage[key]}')
            if sorted(want_stage.get('violations', [])) != sorted(have_stage['violations']):
                problems.append(f'第{index + 1}关 违例 期望 {sorted(want_stage.get("violations", []))} '
                                f'实为 {sorted(have_stage["violations"])}')
        for index, want_task in enumerate(expect.get('tasks', [])):
            have_task = got['tasks'][index] if index < len(got['tasks']) else None
            if have_task is None:
                problems.append(f'第{index + 1}个任务缺失')
                continue
            for key in ('id', 'outcome', 'error_kind'):
                if key in want_task and want_task[key] != have_task[key]:
                    problems.append(f'第{index + 1}个任务 {key} 期望 {want_task[key]} '
                                    f'实为 {have_task[key]}')

        # 边界快照（R2 跨任务复位）：真正跑过的任务必须有 boundary_state，
        # 跳过（含断点续跑）的任务不该有 —— 它压根没开始，"边界"无从谈起。
        if got.get('run_directory'):
            import glob as _glob
            for have_task in got['tasks']:
                pattern = f"{got['run_directory']}/task-{have_task['id']}.json"
                files = _glob.glob(pattern)
                if not files:
                    problems.append(f"缺少任务工件: {have_task['id']}")
                    continue
                artifact = json.loads(Path(files[0]).read_text(encoding='utf-8'))
                boundary = artifact.get('boundary_state')
                # "跑过"不能只看结论：`failed` 也可能是"前置条件不满足、根本没开始"
                # （required 的前置失败记 failed 但不会执行任务）。用 unmet_preconditions 区分。
                ran = (have_task['outcome'] != 'skipped'
                       and not (artifact.get('unmet_preconditions') or []))
                if ran and not (isinstance(boundary, dict) and 'available' in boundary):
                    problems.append(f"跑过的任务 {have_task['id']} 缺 boundary_state：{boundary}")
                if not ran and boundary is not None:
                    problems.append(f"没跑的任务 {have_task['id']} 不该有 boundary_state：{boundary}")
        for want_file in expect.get('artifacts', []):
            if want_file not in got['artifact_names']:
                problems.append(f'缺少工件 {want_file}（实际 {got["artifact_names"]}）')
        if case['name'] == 'cancel_at_stage_boundary_keeps_evidence':
            if not got['stopped_early'] or got['stop_reason'] != 'cancelled':
                problems.append(f'取消没有如实记录: stopped_early={got["stopped_early"]} '
                                f'stop_reason={got["stop_reason"]}')

        status = 'ok' if not problems else 'FAIL'
        print(f'  {status:<4} {name:<42} outcome={got["outcome"]:<10} '
              f'cleared={got["cleared"]}')
        for problem in problems:
            print(f'        ← {problem}')
        if problems:
            failures.append(f'{name}: {len(problems)} 项不符')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（常驻会话只初始化一次、失败即停、取消在边界生效、证据完整）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
