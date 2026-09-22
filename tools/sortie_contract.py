"""sortie-result/1：一次出击的结果合同（唯一真值）。

为什么需要它（路线 R0）：S3 之前把结果散在 `cleared` / `outcome` / `stop_reason` /
`campaign_end` 几个字段里，判定口径只在 `classify_campaign_end` 的实现里隐式存在。
只要有人把 `cleared` 写成 `campaign_end`，表面上一切照旧、却会把"撤退"记成"通关"。
本模块把这些口径**显式化**成一张可执行的不变量表：

  * 生产方：`tools/s3_campaign_outcome.py`（`finalize_sortie_result` 末尾 stamp 一次）
  * 消费方：`src/Alas.Core/Campaign/SortieResult.cs`（同一套规则，逐条对齐）
  * 对拍方：`tools/diagnostics/verify_result_contract.py`（两侧裁决必须逐例一致）

四条硬规则（对应 R0 阶段门槛）：

  1. `cleared` 恒等于 `outcome == 'cleared'`，不接受"两个字段各说各话"。
  2. `outcome == 'cleared'` 必须带**结算证据**：胜方战果（S/A/B）+ 战果来源 +
     `combat_status` + `handle_in_stage` 都在调用栈里 + 没有走撤退 + 真的打过一仗。
  3. `campaign_end` 只表示"本次出击结束"，**单独不能证明通关**（撤退也抛 `CampaignEnd`）。
  4. 失败必须可定位：`error` 要带调用栈尾部，存下来的失败帧要登记在 `failure_frames` 里。

改这里的规则时，必须同步改 C# 侧同一张表并跑 `verify_result_contract.py`：
两侧不一致会直接对拍失败，不是"文档没更新"这种软问题。
"""
from __future__ import annotations

import os
from pathlib import Path

#: 合同版本。字段语义变化时递增，旧文档由 `contract_version_mismatch` 挑出来。
CONTRACT = 'sortie-result/1'

#: 结果词表。`ended_unknown` 是"出击结束了但说不出为什么"——它是**合法结论**，
#: 不是错误：上游确实存在既无胜方战果、也非撤退的结束路径。
OUTCOMES = (
    'cleared',          # 通关：本次出击击败 BOSS 并成功结算
    'withdrawn',        # 撤退：主动/自动离开地图，不算通关
    'defeated',         # 战败：拿到 C/D 战果
    'ended_unknown',    # 出击结束但证据不足，**禁止**当成通关
    'error',            # 报错中断（含地图初始化失败、上游异常）
    'incomplete',       # 没打完：轮次/时间上限到顶，或上游返回但没给结束信号
    'refused',          # 拒绝执行：安全联锁未开（dry-run 之外的真跑需要 allow_actions）
)

#: 胜方战果 / 败方战果。上游 `BATTLE_STATUS_*` 与 `EXP_INFO_*` 两套模板都用这几个字母。
WIN_RANKS = ('S', 'A', 'B')
LOSS_RANKS = ('C', 'D')

#: `incomplete` 的合法理由。没有理由的"没打完"无法复核。
LIMIT_REASONS = (
    'round_limit',
    'time_limit',
    'upstream_returned_without_clear',
    'stopped_after_map_init',
)

#: 真正驱动游戏的操作步。dry-run 里出现任何一个都说明纯度破了。
ACTION_STEPS = (
    'prepare_campaign_navigation',
    'abort_unfinished',
    'ensure_campaign_ui',
    'prepare_campaign_run',
    'enter_map',
    'handle_map_fleet_lock',
    'map_init',
    'execute_a_battle',
    'auto_search_execute_a_battle',
    'withdraw',
)

#: 打过一仗的步骤名（`cleared` 必须至少有一个且无 error）。
BATTLE_STEPS = ('execute_a_battle', 'auto_search_execute_a_battle')

#: 违例码。**跨语言共享**：C# 侧 `SortieContract` 里是同一组字符串。
VIOLATION_CODES = (
    'contract_version_mismatch',
    'outcome_missing',
    'unknown_outcome',
    'cleared_flag_mismatch',
    'cleared_without_settlement_evidence',
    'cleared_requires_win_rank',
    'cleared_requires_rank_source',
    'cleared_requires_combat_status',
    'cleared_requires_stage_observed',
    'cleared_requires_no_withdrawal',
    'cleared_requires_executed_battle',
    'withdrawn_requires_withdrawal_evidence',
    'defeated_requires_loss_rank',
    'ended_unknown_requires_campaign_end',
    'error_requires_failure',
    'error_requires_traceback',
    'error_step_without_traceback',
    'incomplete_requires_limit_reason',
    'refused_requires_reason',
    'failure_frame_not_listed',
    'failure_frame_missing_on_disk',
    'dry_run_must_not_execute',
)


def _failures(steps):
    """按出现顺序取出真正失败的步骤（`error` 非空）。"""
    return [step for step in steps or [] if isinstance(step, dict) and step.get('error')]


def _has_content(obj):
    """对象是否**真的带了信息**（全 None/空串/空容器视为没有）。

    为什么不用真值判断：`{"battle_rank": null}` 在 Python 里是真值、在 C# 反序列化后
    是个字段全空的对象 —— 两侧必须用同一个谓词，否则跨语言对拍会在这种边角上分叉。
    """
    if not isinstance(obj, dict):
        return False
    return any(value not in (None, '', [], {}) for value in obj.values())


def _frames(result, steps):
    """收集本次结果声明的失败帧路径（顶层 failure 与各步都算）。"""
    frames = []
    top = result.get('failure') or {}
    frame = top.get('frame')
    if isinstance(frame, str) and frame:
        frames.append(frame)
    for step in steps or []:
        if not isinstance(step, dict):
            continue
        step_frame = step.get('failure_frame')
        if isinstance(step_frame, str) and step_frame:
            frames.append(step_frame)
    return frames


def _path_exists(frame, artifact_root):
    """失败帧存在性检查：绝对路径直接用，相对路径相对 `artifact_root` 解析。"""
    path = Path(frame)
    if not path.is_absolute():
        if artifact_root is None:
            return None                  # 没有根目录就无法判断，不冤枉也不放过
        path = Path(artifact_root) / path
    return path.is_file()


def evaluate(result, artifact_root=None):
    """给一份结果文档做合同裁决。

    返回 `{'contract', 'outcome', 'cleared', 'violations': [code...], 'details': [...]}`。
    跨语言对拍只比 `violations`（**码的集合**）：detail 是给人看的，措辞可以不同，
    码必须一致 —— 码不一致就说明两侧规则表已经分叉。
    """
    if not isinstance(result, dict):
        return {'contract': None, 'outcome': None, 'cleared': False,
                'violations': ['outcome_missing'], 'details': ['结果不是对象']}

    problems = []

    def bad(code, detail=''):
        problems.append((code, detail or code))

    contract = result.get('contract')
    if contract is not None and contract != CONTRACT:
        bad('contract_version_mismatch', f'合同版本 {contract!r} 不是 {CONTRACT}')

    outcome = result.get('outcome')
    cleared = bool(result.get('cleared'))
    dry_run = bool(result.get('dry_run'))
    campaign_end = bool(result.get('campaign_end'))
    reason = result.get('reason') or result.get('end_reason') or ''
    stop_reason = result.get('stop_reason')
    steps = result.get('steps') or []
    evidence = result.get('end_evidence')

    if outcome is None:
        if not dry_run:
            bad('outcome_missing', '非 dry-run 的结果必须有 outcome')
    elif outcome not in OUTCOMES:
        bad('unknown_outcome', f'词表外的结果: {outcome!r}')

    if cleared != (outcome == 'cleared'):
        bad('cleared_flag_mismatch',
            f'cleared={cleared} 与 outcome={outcome!r} 不一致')

    if outcome == 'cleared':
        if not _has_content(evidence):
            # campaign_end 单独不能通关：这条就是 R0 明确要堵的口子。
            bad('cleared_without_settlement_evidence',
                'cleared 但没有 end_evidence（campaign_end 不能单独证明通关）')
        else:
            rank = evidence.get('battle_rank')
            if rank not in WIN_RANKS:
                bad('cleared_requires_win_rank', f'结算战果 {rank!r} 不是胜方')
            if not evidence.get('rank_source'):
                bad('cleared_requires_rank_source', '结算战果没有来源模板')
            if not evidence.get('combat_status'):
                bad('cleared_requires_combat_status', '调用栈里没有 combat_status')
            if not evidence.get('stage_observed'):
                bad('cleared_requires_stage_observed', '调用栈里没有 handle_in_stage')
            if evidence.get('withdrawn'):
                bad('cleared_requires_no_withdrawal', '撤退路径不能判通关')
        battles = [s for s in steps if isinstance(s, dict)
                   and s.get('step') in BATTLE_STEPS and not s.get('error')]
        if not battles:
            bad('cleared_requires_executed_battle', '没有一次无错误的战斗步骤')

    if outcome == 'withdrawn':
        withdrew = bool(evidence.get('withdrawn')) if isinstance(evidence, dict) else False
        if not (withdrew or 'withdraw' in str(reason).lower()):
            bad('withdrawn_requires_withdrawal_evidence', '撤退结果没有撤退证据')

    if outcome == 'defeated':
        rank = (evidence or {}).get('battle_rank')
        if rank not in LOSS_RANKS:
            bad('defeated_requires_loss_rank', f'战败结果的战果是 {rank!r}')

    if outcome == 'ended_unknown' and not campaign_end:
        bad('ended_unknown_requires_campaign_end', 'ended_unknown 必须来自 CampaignEnd')

    if outcome == 'error':
        failures = _failures(steps)
        top = result.get('failure') if _has_content(result.get('failure')) else None
        if top is None and not failures:
            bad('error_requires_failure', 'error 结果没有任何失败步骤')
        else:
            if top is None:
                top = failures[0]
            if not top.get('traceback_tail'):
                bad('error_requires_traceback', '失败没有调用栈尾部')

    for step in steps:
        if isinstance(step, dict) and step.get('error') and not step.get('traceback_tail'):
            bad('error_step_without_traceback', f'步骤 {step.get("step")!r} 的失败没有调用栈')
            break

    if outcome == 'incomplete' and stop_reason not in LIMIT_REASONS:
        bad('incomplete_requires_limit_reason', f'没打完但理由 {stop_reason!r} 不在允许集内')

    if outcome == 'refused' and not reason:
        bad('refused_requires_reason', '拒绝执行必须给出理由')

    frames = _frames(result, steps)
    listed = [f for f in (result.get('failure_frames') or []) if isinstance(f, str)]
    for frame in frames:
        if frame not in listed:
            bad('failure_frame_not_listed', f'失败帧没有登记: {frame}')
    for frame in frames:
        exists = _path_exists(frame, artifact_root)
        if exists is False:
            bad('failure_frame_missing_on_disk', f'失败帧文件不存在: {frame}')

    if dry_run:
        for step in steps:
            if isinstance(step, dict) and step.get('step') in ACTION_STEPS:
                bad('dry_run_must_not_execute', f'dry-run 却执行了 {step.get("step")}')
                break

    return {
        'contract': contract,
        'outcome': outcome,
        'cleared': cleared,
        'violations': [code for code, _ in problems],
        'details': [detail for _, detail in problems],
    }


def stamp(result, artifact_root=None):
    """把结果文档补齐成合同形态，并**自报**违例（生产方自己先查一遍）。

    只补齐/校正派生字段，不篡改 `outcome`：`cleared` 一律由 `outcome` 推出，
    这样"把 campaign_end 写进 cleared"这种错误在源头就无法表达。
    """
    if not isinstance(result, dict):
        return result
    result.setdefault('contract', CONTRACT)
    outcome = result.get('outcome')
    if outcome is not None or not result.get('dry_run'):
        result['cleared'] = bool(outcome == 'cleared')
    else:
        result['cleared'] = False

    steps = result.get('steps') or []
    if not _has_content(result.get('failure')):
        failures = _failures(steps)
        if failures:
            first = failures[0]
            result['failure'] = {
                'step': first.get('step'),
                'error': first.get('error'),
                'traceback_tail': list(first.get('traceback_tail') or []),
                'frame': first.get('failure_frame'),
            }
    frames = _frames(result, steps)
    if frames:
        merged = [f for f in (result.get('failure_frames') or []) if isinstance(f, str)]
        for frame in frames:
            if frame not in merged:
                merged.append(frame)
        result['failure_frames'] = merged

    verdict = evaluate(result, artifact_root)
    if verdict['violations']:
        result['contract_violations'] = verdict['violations']
    else:
        result.pop('contract_violations', None)
    return result


def describe(verdict):
    """一行摘要，给 CLI/日志用。"""
    if not verdict['violations']:
        return f"合规 outcome={verdict['outcome']} cleared={verdict['cleared']}"
    return (f"违例 {len(verdict['violations'])} 项: "
            + ', '.join(verdict['violations']))


def default_artifact_root():
    """生产方默认的失败帧目录（与 `s3_campaign_execution.record_failure` 一致）。"""
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'data', 's3_failures')
