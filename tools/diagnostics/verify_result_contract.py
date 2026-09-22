# -*- coding: utf-8 -*-
"""R0 验收：结果合同 sortie-result/1 —— 四类结果判别 + 跨语言对拍。

为什么需要它（对应 R0 阶段门槛）：

1. **同一组替身用例能稳定区分四类结果**。这里用 `s3_stub_campaign.py` 的替身
   （真上游方法 + 假屏幕/设备）跑出通关、撤退、报错、限额四类文档，逐例要求裁决合规。
2. **没有任何代码以 CampaignEnd 单字段判定通关**。反例集里专门放了一条
   "`campaign_end=true` 就声称 cleared"的文档，两侧都必须拒绝它。
   静态守卫在 `verify_architecture.py`（扫源码），这里管行为。
3. **两侧口径不分叉**。合同有两份实现（生产方 Python / 消费方 C#），
   这里把同一批文档分别喂给两边，要求 `outcome`、`cleared`、违例码集合逐例相同。

用法：
    python tools/diagnostics/verify_result_contract.py
未构建 Release 的 alashub 时，只跳过跨语言那一半并**显式说明**（不静默通过）。
"""
from __future__ import annotations

import json
import os
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

import alas_vision as av                                    # noqa: E402
from sortie_contract import CONTRACT, evaluate              # noqa: E402
from s3_campaign_execution import run_native_campaign       # noqa: E402
from s3_campaign_outcome import finalize_sortie_result      # noqa: E402
from s3_stub_campaign import FakeScreenCampaign, NativeRunCampaign  # noqa: E402

EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'
# 规则词表里的"真跑过一仗"步骤：手工正例要按生产形态带上它。
BATTLE_STEP = {'step': 'execute_a_battle', 'round': 1, 'ms': 12.0}


def case(name, document, outcome, violations, note=''):
    return {'name': name, 'document': document, 'expect': {
        'outcome': outcome, 'violations': sorted(violations)}, 'note': note}


# --------------------------------------------------------------------------
# 一、用替身跑出来的真实文档（生产路径，不是我手写的形状）
# --------------------------------------------------------------------------

def _native(**kwargs):
    """原生 run 调度：走 run_native_campaign 的真实记录逻辑。"""
    return run_native_campaign(NativeRunCampaign(**kwargs))


def produced_documents(artifact_dir: Path):
    """四类结果 + 边界类，全部由生产代码产出。"""
    documents = {}

    # 通关：BOSS 结算拿到胜方战果（S/A/B 三档都跑一遍）
    for rank in ('S', 'A', 'B'):
        documents[f'produced_cleared_{rank}'] = (_native(rank=rank), 'cleared', [])
    # 战败：拿到败方战果
    for rank in ('C', 'D'):
        documents[f'produced_defeated_{rank}'] = (_native(rank=rank), 'defeated', [])
    # 撤退：上游 withdraw() 抛的 CampaignEnd，**不是**通关
    documents['produced_withdrawn'] = (_native(withdraw=True), 'withdrawn', [])
    # 出击结束了但结算证据不足：只能是 ended_unknown
    documents['produced_ended_unknown'] = (_native(unknown=True), 'ended_unknown', [])
    documents['produced_no_rank'] = (_native(rank=None), 'ended_unknown', [])

    # 限额：轮次到顶
    limited = NativeRunCampaign()

    def never_ending_battle():
        limited.battle_count += 1
        return True
    limited.execute_a_battle = never_ending_battle
    documents['produced_round_limit'] = (
        run_native_campaign(limited, max_rounds=3), 'incomplete', [])

    # 限额：时间到顶
    timed = NativeRunCampaign()
    clock = [0.0]

    def slow_enter(*args, **kwargs):
        clock[0] = 2.0
    timed.enter_map = slow_enter
    import s3_campaign_execution as execution
    original_monotonic = execution.time.monotonic
    execution.time.monotonic = lambda: clock[0]
    try:
        documents['produced_time_limit'] = (
            run_native_campaign(timed, max_seconds=1), 'incomplete', [])
    finally:
        execution.time.monotonic = original_monotonic

    # 报错：地图初始化失败 → 必须带调用栈 + 现场失败帧
    import numpy as np
    broken = NativeRunCampaign()
    # 失败帧来自"设备当前那一帧"（不重新截图）；替身里给它种一帧。
    broken.device.image = np.zeros((4, 4, 3), dtype=np.uint8)

    def broken_map_init(*args):
        raise RuntimeError('fixture map failure')
    broken.map_init = broken_map_init
    documents['produced_error_with_frame'] = (
        run_native_campaign(broken, artifact_dir=str(artifact_dir)), 'error', [])

    return documents


def protocol_documents():
    """协议入口的三种"没开打"结果：dry-run / 拒绝 / 规则缺失。"""
    documents = {}
    chapter = 'campaign.campaign_main.campaign_2_1'
    documents['protocol_dry_run'] = (
        av.op_s3_run_plan({'chapter': chapter, 'dry_run': True}), None, [])
    documents['protocol_refused'] = (
        av.op_s3_run_plan({'chapter': chapter, 'dry_run': False}), 'refused', [])
    documents['protocol_rules_missing'] = (
        av.op_s3_run_plan({'chapter': 'campaign.event_missing.a1',
                           'dry_run': False, 'allow_actions': True}), 'error', [])
    # 单次调用级：证明 `op_s3_campaign_call` 的异常路径也走同一套判别
    inst = FakeScreenCampaign(withdraw=True)
    av._CAMPAIGN['obj'] = inst
    av.op_s3_campaign_call({'name': 'execute_a_battle', 'allow_actions': True})
    documents['protocol_call_withdrawn'] = (
        finalize_sortie_result({'chapter': chapter}, [], inst._s3_last_end),
        'withdrawn', [])
    return documents


# --------------------------------------------------------------------------
# 二、手写反例（每条都要被拒绝）+ 一条手写正例（合同不是只有我们的生产方能满足）
# --------------------------------------------------------------------------

def authored_cases(frame_on_disk: str, missing_frame: str):
    win_evidence = {
        'battle_rank': 'S', 'rank_source': 'BATTLE_STATUS_', 'combat_status': True,
        'stage_observed': True, 'expected_end': 'in_stage', 'withdrawn': False,
        'call_path': ['module.combat.combat.combat_status',
                      'module.handler.enemy_searching.handle_in_stage'],
    }

    def document(**overrides):
        base = {'contract': CONTRACT, 'chapter': 'campaign.campaign_main.campaign_2_1'}
        base.update(overrides)
        return base

    return [
        case('authored_cleared_minimal',
             document(outcome='cleared', cleared=True, campaign_end=True,
                      end_evidence=win_evidence, steps=[dict(BATTLE_STEP)]),
             'cleared', [], '手写正例：证据齐全才允许判通关'),
        case('neg_campaign_end_alone_is_not_clear',
             document(outcome='cleared', cleared=True, campaign_end=True,
                      steps=[{'step': 'execute_a_battle'}]),
             'cleared', ['cleared_without_settlement_evidence'],
             'R0 的核心反例：撤退也抛 CampaignEnd'),
        case('neg_cleared_flag_mismatch',
             document(outcome='withdrawn', cleared=True,
                      end_evidence={'withdrawn': True}),
             'withdrawn', ['cleared_flag_mismatch']),
        case('neg_unknown_outcome',
             document(outcome='victory', cleared=False),
             'victory', ['unknown_outcome']),
        case('neg_cleared_without_battle_step',
             document(outcome='cleared', cleared=True, end_evidence=dict(win_evidence),
                      steps=[{'step': 'map_init', 'ms': 1.0}]),
             'cleared', ['cleared_requires_executed_battle']),
        case('neg_cleared_with_loss_rank',
             document(outcome='cleared', cleared=True, steps=[dict(BATTLE_STEP)],
                      end_evidence=dict(win_evidence, battle_rank='C')),
             'cleared', ['cleared_requires_win_rank']),
        case('neg_cleared_after_withdrawal',
             document(outcome='cleared', cleared=True, steps=[dict(BATTLE_STEP)],
                      end_evidence=dict(win_evidence, withdrawn=True)),
             'cleared', ['cleared_requires_no_withdrawal']),
        case('neg_cleared_without_combat_status',
             document(outcome='cleared', cleared=True, steps=[dict(BATTLE_STEP)],
                      end_evidence=dict(win_evidence, combat_status=False)),
             'cleared', ['cleared_requires_combat_status']),
        case('neg_cleared_without_stage_observed',
             document(outcome='cleared', cleared=True, steps=[dict(BATTLE_STEP)],
                      end_evidence=dict(win_evidence, stage_observed=False)),
             'cleared', ['cleared_requires_stage_observed']),
        case('neg_defeated_with_win_rank',
             document(outcome='defeated', cleared=False, end_evidence=dict(win_evidence)),
             'defeated', ['defeated_requires_loss_rank']),
        case('neg_withdrawn_without_evidence',
             document(outcome='withdrawn', cleared=False, campaign_end=True),
             'withdrawn', ['withdrawn_requires_withdrawal_evidence']),
        case('neg_ended_unknown_without_campaign_end',
             document(outcome='ended_unknown', cleared=False),
             'ended_unknown', ['ended_unknown_requires_campaign_end']),
        case('neg_error_without_failure',
             document(outcome='error', cleared=False, error='boom'),
             'error', ['error_requires_failure']),
        case('neg_error_without_traceback',
             document(outcome='error', cleared=False,
                      failure={'step': 'map_init', 'error': 'boom', 'traceback_tail': []}),
             'error', ['error_requires_traceback']),
        case('neg_incomplete_without_limit_reason',
             document(outcome='incomplete', cleared=False, stop_reason='whatever'),
             'incomplete', ['incomplete_requires_limit_reason']),
        case('neg_refused_without_reason',
             document(outcome='refused', cleared=False, refused=True),
             'refused', ['refused_requires_reason']),
        case('neg_failure_frame_not_listed',
             document(outcome='error', cleared=False,
                      failure={'step': 'map_init', 'error': 'boom',
                               'traceback_tail': ['RuntimeError: boom'],
                               'frame': frame_on_disk}),
             'error', ['failure_frame_not_listed']),
        case('neg_failure_frame_missing_on_disk',
             document(outcome='error', cleared=False, failure_frames=[missing_frame],
                      failure={'step': 'map_init', 'error': 'boom',
                               'traceback_tail': ['RuntimeError: boom'],
                               'frame': missing_frame}),
             'error', ['failure_frame_missing_on_disk']),
        case('neg_dry_run_executed_actions',
             document(dry_run=True, steps=[dict(BATTLE_STEP)]),
             None, ['dry_run_must_not_execute']),
        case('neg_contract_version_mismatch',
             document(contract='sortie-result/0', outcome='incomplete', cleared=False,
                      stop_reason='round_limit'),
             'incomplete', ['contract_version_mismatch']),
    ]


# --------------------------------------------------------------------------

def main() -> int:
    failures = []
    with TemporaryDirectory(prefix='sortie-contract-') as tmp:
        tmpdir = Path(tmp)
        artifact_dir = tmpdir / 'artifacts'
        artifact_dir.mkdir()

        cases = []
        for name, (document, outcome, violations) in produced_documents(artifact_dir).items():
            cases.append(case(name, document, outcome, violations))
        for name, (document, outcome, violations) in protocol_documents().items():
            cases.append(case(name, document, outcome, violations))

        frames = [f for f in artifact_dir.glob('*.png')]
        if len(frames) != 1:
            failures.append(f'失败帧应恰好存下 1 张，实际 {len(frames)} 张')
        frame_on_disk = str(frames[0]) if frames else str(artifact_dir / 'missing.png')
        missing_frame = str(artifact_dir / 'never_written.png')
        cases.extend(authored_cases(frame_on_disk, missing_frame))

        # ---- 一、Python 侧裁决 + 与手写期望比对
        print(f'=== 结果合同 {CONTRACT}：{len(cases)} 例 ===')
        classes = {}
        for item in cases:
            verdict = evaluate(item['document'], artifact_root=str(artifact_dir))
            item['python'] = verdict
            expect = item['expect']
            got = sorted(verdict['violations'])
            problems = []
            if verdict['outcome'] != expect['outcome']:
                problems.append(f"outcome 期望 {expect['outcome']!r} 实为 {verdict['outcome']!r}")
            if got != expect['violations']:
                problems.append(f"违例 期望 {expect['violations']} 实为 {got}")
            if problems:
                failures.append(f"{item['name']}: " + '; '.join(problems))
            for name in expect['violations']:
                classes.setdefault(name, 0)
                classes[name] += 1
            print(f"  {'ok  ' if not problems else 'FAIL'} {item['name']:<36} "
                  f"outcome={str(verdict['outcome']):<14} cleared={str(verdict['cleared']):<5} "
                  f"违例={len(got)}" + (f"  ← {'; '.join(problems)}" if problems else ''))

        # ---- 二、四类结果必须都被替身集覆盖
        seen = {item['python']['outcome'] for item in cases}
        for required in ('cleared', 'withdrawn', 'error', 'incomplete'):
            if required not in seen:
                failures.append(f'替身集没有稳定产出 {required} 类结果')
        print()
        print(f'  四类覆盖: cleared={sum(1 for i in cases if i["python"]["outcome"] == "cleared")} '
              f'withdrawn={sum(1 for i in cases if i["python"]["outcome"] == "withdrawn")} '
              f'error={sum(1 for i in cases if i["python"]["outcome"] == "error")} '
              f'incomplete={sum(1 for i in cases if i["python"]["outcome"] == "incomplete")}')
        print(f'  被拒绝的反例样式: {len(classes)} 种违例码 → {", ".join(sorted(classes))}')

        # ---- 三、跨语言对拍（C# 侧同一批文档）
        fixture = tmpdir / 'cases.json'
        fixture.write_text(json.dumps({'contract': CONTRACT, 'cases': [
            {'name': i['name'], 'document': i['document'], 'expect': i['expect']}
            for i in cases]}, ensure_ascii=False, indent=1), encoding='utf-8')
        if not EXE.is_file():
            print()
            print(f'[跳过] 未构建 {EXE.relative_to(ROOT)}；跨语言对拍未跑'
                  f'（构建: dotnet build src\\Alas.DataTool\\Alas.DataTool.csproj -c Release）')
            return 1 if failures else 0

        verdicts = tmpdir / 'verdicts.json'
        proc = subprocess.run([str(EXE), 'contract', '--fixture', str(fixture),
                               '--artifacts', str(artifact_dir), '--json', str(verdicts)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=120)
        print()
        print('--- C# 侧（alashub contract）---')
        for line in (proc.stdout or '').strip().splitlines():
            print('  ' + line)
        if proc.returncode != 0:
            failures.append(f'alashub contract 退出码 {proc.returncode}')
        if proc.stderr:
            print(f'  stderr: {proc.stderr.strip()[:400]}')

        if verdicts.is_file():
            actual = {c['name']: c for c in json.loads(verdicts.read_text(encoding='utf-8'))['cases']}
            mismatched = 0
            for item in cases:
                got = actual.get(item['name'])
                if got is None:
                    failures.append(f"{item['name']}: C# 没给裁决")
                    continue
                mine = item['python']
                if (got['outcome'] != mine['outcome']
                        or got['cleared'] != mine['cleared']
                        or sorted(got['violations']) != sorted(mine['violations'])):
                    mismatched += 1
                    failures.append(
                        f"{item['name']}: 两侧裁决不一致 "
                        f"Python(outcome={mine['outcome']},cleared={mine['cleared']},"
                        f"违例={sorted(mine['violations'])}) vs "
                        f"C#(outcome={got['outcome']},cleared={got['cleared']},"
                        f"违例={sorted(got['violations'])})")
            print(f'  跨语言逐例一致: {len(cases) - mismatched}/{len(cases)}')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（四类结果判别、反例拒绝、跨语言裁决一致）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
