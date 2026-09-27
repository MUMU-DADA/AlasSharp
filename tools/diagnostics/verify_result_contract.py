# -*- coding: utf-8 -*-
"""R0 验收：结果合同 sortie-result/1 —— 四类结果判别 + 跨语言对拍。

为什么需要它（对应 R0 阶段门槛）：

1. **同一组离线 Engine 结果夹具能稳定区分四类结果**。夹具只包含结果合同字段，
   不启动旧上游宿主、设备或业务 Python。
2. **没有任何代码以 CampaignEnd 单字段判定通关**。反例集里专门放了一条
   "`campaign_end=true` 就声称 cleared"的文档，两侧都必须拒绝它。
   静态守卫在 `verify_architecture.py`（扫源码），这里管行为。
3. **两侧口径不分叉**。合同有两份实现（生产方 Python / 消费方 C#），
   这里把同一批文档分别喂给两边，要求 `outcome`、`cleared`、违例码集合逐例相同。

用法：
    python tools/diagnostics/verify_result_contract.py
脚本构建独立离线 C# 参考程序；不调用产品 Server，也不因未构建而跳过跨语言验收。
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

from sortie_contract import CONTRACT, evaluate              # noqa: E402

REFERENCE = HERE / 'result_contract_reference' / 'ResultContract.Reference.csproj'
# 规则词表里的"真跑过一仗"步骤：手工正例要按产品形态带上它。
BATTLE_STEP = {'step': 'execute_a_battle', 'round': 1, 'ms': 12.0}


def case(name, document, outcome, violations, note=''):
    return {'name': name, 'document': document, 'expect': {
        'outcome': outcome, 'violations': sorted(violations)}, 'note': note}


# --------------------------------------------------------------------------
# 一、Engine 结果合同的离线夹具（不导入业务宿主）
# --------------------------------------------------------------------------

def _result(*, outcome=None, rank=None, campaign_end=True, dry_run=False,
            stop_reason=None, reason=None, steps=None, evidence=None,
            failure=None, failure_frames=None):
    document = {
        'contract': CONTRACT,
        'chapter': 'campaign.campaign_main.campaign_2_1',
        'stage': '2-1',
        'dry_run': dry_run,
        'cleared': outcome == 'cleared',
        'campaign_end': campaign_end,
    }
    if outcome is not None:
        document['outcome'] = outcome
    if stop_reason is not None:
        document['stop_reason'] = stop_reason
    if reason is not None:
        document['reason'] = reason
    if steps is not None:
        document['steps'] = steps
    if evidence is not None:
        document['end_evidence'] = evidence
    if failure is not None:
        document['failure'] = failure
    if failure_frames is not None:
        document['failure_frames'] = failure_frames
    return document


def _settlement(rank):
    return {
        'battle_rank': rank,
        'rank_source': 'BATTLE_STATUS_',
        'combat_status': True,
        'stage_observed': True,
        'expected_end': 'in_stage',
        'withdrawn': False,
        'call_path': ['module.combat.combat.combat_status',
                      'module.handler.enemy_searching.handle_in_stage'],
    }


def produced_documents(artifact_dir: Path):
    """Produce contract-shaped Engine fixtures without importing a business host."""
    documents = {}
    for rank in ('S', 'A', 'B'):
        documents[f'produced_cleared_{rank}'] = (
            _result(outcome='cleared', rank=rank, steps=[dict(BATTLE_STEP)],
                    evidence=_settlement(rank)), 'cleared', [])
    for rank in ('C', 'D'):
        documents[f'produced_defeated_{rank}'] = (
            _result(outcome='defeated', steps=[dict(BATTLE_STEP)],
                    evidence={'battle_rank': rank, 'rank_source': 'BATTLE_STATUS_',
                              'combat_status': True, 'stage_observed': True}),
            'defeated', [])
    documents['produced_withdrawn'] = (
        _result(outcome='withdrawn', reason='withdraw', evidence={'withdrawn': True}),
        'withdrawn', [])
    documents['produced_ended_unknown'] = (
        _result(outcome='ended_unknown', evidence={'stage_observed': True}),
        'ended_unknown', [])
    documents['produced_no_rank'] = (
        _result(outcome='ended_unknown', evidence={'combat_status': True}),
        'ended_unknown', [])
    documents['produced_round_limit'] = (
        _result(outcome='incomplete', campaign_end=False, stop_reason='round_limit', steps=[]),
        'incomplete', [])
    documents['produced_time_limit'] = (
        _result(outcome='incomplete', campaign_end=False, stop_reason='time_limit', steps=[]),
        'incomplete', [])
    frame = artifact_dir / 'failure.png'
    frame.write_bytes(b'engine-contract-fixture')
    failure = {'step': 'map_init', 'error': 'fixture map failure',
               'traceback_tail': ['RuntimeError: fixture map failure'], 'frame': str(frame)}
    documents['produced_error_with_frame'] = (
        _result(outcome='error', campaign_end=False, failure=failure,
                failure_frames=[str(frame)]), 'error', [])
    documents['produced_recovered_then_cleared'] = (
        _result(outcome='cleared', steps=[dict(BATTLE_STEP)], evidence=_settlement('S')),
        'cleared', [])
    documents['produced_recovered_then_boundary'] = (
        _result(outcome='incomplete', campaign_end=False, stop_reason='stopped_after_map_init'),
        'incomplete', [])
    documents['produced_settled_then_error'] = (
        _result(outcome='error', campaign_end=False,
                failure={'step': 'campaign_end', 'error': 'terminal log failure',
                         'traceback_tail': ['OSError: terminal log failure']}),
        'error', [])
    return documents


def protocol_documents():
    """Protocol-shaped results produced by Engine request boundaries."""
    return {
        'protocol_dry_run': (_result(dry_run=True, campaign_end=False, steps=[]), None, []),
        'protocol_refused': (_result(outcome='refused', campaign_end=False,
                                     reason='allow_actions_required'), 'refused', []),
        'protocol_rules_missing': (_result(outcome='error', campaign_end=False,
                                           failure={'step': 'load_rules', 'error': 'rules missing',
                                                    'traceback_tail': ['RuleError: rules missing']}),
                                   'error', []),
        'protocol_call_withdrawn': (_result(outcome='withdrawn', reason='withdraw',
                                            evidence={'withdrawn': True}), 'withdrawn', []),
    }


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
        verdicts = tmpdir / 'verdicts.json'
        proc = subprocess.run(['dotnet', 'run', '--project', str(REFERENCE), '-c', 'Release', '--',
                               str(fixture), str(artifact_dir), str(verdicts)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=120)
        print()
        print('--- C# 侧（独立离线 ResultContract.Reference）---')
        for line in (proc.stdout or '').strip().splitlines():
            print('  ' + line)
        if proc.returncode != 0:
            failures.append(f'ResultContract.Reference 退出码 {proc.returncode}')
        if proc.stderr:
            print(f'  stderr: {proc.stderr.strip()[:400]}')

        if verdicts.is_file():
            actual = {c['name']: c for c in json.loads(verdicts.read_text(encoding='utf-8'))['cases']}
            mismatched = 0
            for item in cases:
                got = actual.get(item['name'])
                if got is None:
                    mismatched += 1
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
        else:
            failures.append('C# 参考程序没有生成裁决文件')

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
