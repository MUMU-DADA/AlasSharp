"""Report sortie outcomes from upstream execution evidence.

CampaignEnd means the sortie ended; upstream raises it for withdrawal too.
Stage clearance percentage is cumulative and may already be 100% at entry.
Neither value alone proves that this sortie defeated the boss.

判定结果统一交给 `sortie_contract`（sortie-result/1）：这里只负责**取证据**
（调用栈帧、战果点击），结论与"结论是否站得住"由那张不变量表说了算。
"""
from contextlib import contextmanager

from sortie_contract import stamp


@contextmanager
def observe_battle_result(inst):
    """Observe result buttons upstream clicks, without adding image decisions."""
    evidence = {'battle_rank': None, 'rank_source': None}
    device = getattr(inst, 'device', None)
    click = getattr(device, 'click', None)
    if not callable(click):
        yield evidence
        return
    had_own_click = 'click' in vars(device)
    own_click = vars(device).get('click')

    def tracked_click(button, *args, **kwargs):
        name = getattr(button, 'name', '')
        for prefix in ('BATTLE_STATUS_', 'EXP_INFO_'):
            if name.startswith(prefix) and name[len(prefix):] in ('S', 'A', 'B', 'C', 'D'):
                # EXP_INFO templates may overlap; retain the actual battle
                # result once seen rather than replacing it with a later page.
                if prefix == 'BATTLE_STATUS_' or evidence['rank_source'] != 'BATTLE_STATUS_':
                    evidence['battle_rank'] = name[len(prefix):]
                    evidence['rank_source'] = prefix
        return click(button, *args, **kwargs)

    device.click = tracked_click
    try:
        yield evidence
    finally:
        if had_own_click:
            device.click = own_click
        else:
            del device.click


def classify_campaign_end(error, evidence):
    """Distinguish withdrawal, successful combat and an unexplained map exit.

withdraw() calls handle_in_stage(), which raises before Withdraw is attached
to the exception. Inspect the executing frames, not just the exception text.
"""
    frames = []
    trace = error.__traceback__
    combat_status = False
    stage_observed = False
    expected_end = None
    while trace is not None:
        frame = trace.tb_frame
        module = frame.f_globals.get('__name__', '')
        name = frame.f_code.co_name
        frames.append(f'{module}.{name}')
        if name == 'combat_status' and module == 'module.combat.combat':
            combat_status = True
            expected_end = frame.f_locals.get('expected_end')
        if name == 'handle_in_stage' and module == 'module.handler.enemy_searching':
            stage_observed = True
        trace = trace.tb_next

    reason = str(error) or 'CampaignEnd'
    withdrew = any(name.rsplit('.', 1)[-1] in ('withdraw', '_withdraw') for name in frames)
    withdrew = withdrew or 'withdraw' in reason.lower()
    rank = evidence.get('battle_rank')
    if withdrew:
        outcome = 'withdrawn'
    elif rank in ('C', 'D'):
        outcome = 'defeated'
    elif combat_status and stage_observed and rank in ('S', 'A', 'B'):
        outcome = 'cleared'
    else:
        outcome = 'ended_unknown'
    return {
        'campaign_end': True,
        'completed': outcome == 'cleared',
        'cleared': outcome == 'cleared',
        'outcome': outcome,
        'reason': reason,
        'end_evidence': {
            'battle_rank': rank,
            'rank_source': evidence.get('rank_source'),
            'combat_status': combat_status,
            'stage_observed': stage_observed,
            'expected_end': expected_end if isinstance(expected_end, str) else None,
            'withdrawn': withdrew,
            'call_path': frames,
        },
    }


def finalize_sortie_result(out, steps, end=None):
    """Do not let a later 'skipped' step erase an earlier failure.

    收尾时统一盖上 sortie-result/1 合同戳：补 `contract` / `cleared` / `failure` /
    `failure_frames`，并让生产方**自报**违例（`contract_violations`）。
    真出违例时不在这里抛异常：结果本身要留给调用方和工件，消费方（C#）会拒绝它。
    """
    failures = [step for step in steps if step.get('error')]
    if end:
        for key in ('campaign_end', 'cleared', 'outcome', 'end_evidence'):
            out[key] = end[key]
        out['end_reason'] = end['reason']
        out['stop_reason'] = end['outcome']
    elif failures:
        out.update(campaign_end=False, cleared=False, outcome='error')
        out['error'] = failures[0]['error']
        out['stop_reason'] = 'error'
    else:
        out.update(campaign_end=False, cleared=False, outcome='incomplete')
        out.setdefault('stop_reason', 'round_limit')
    out['stopped_early'] = not out['cleared']
    return stamp(out)
