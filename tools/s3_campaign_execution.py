import os
"""Invoke native Campaign.run() while observing outcomes and operation limits.

The campaign retains ownership of entry, initialization, dispatch, scanning,
special run hooks and auto search. This adapter records existing calls only.
"""
import time
from datetime import datetime, timezone
from pathlib import Path

from module.logger import logger

from s3_campaign_outcome import (
    classify_campaign_end, finalize_sortie_result, observe_battle_result,
)


class _ExecutionBoundary(Exception):
    def __init__(self, reason):
        self.reason = reason
        super().__init__(reason)


def run_native_campaign(inst, *, max_rounds=20, max_seconds=1500, withdraw_file=None,
                        stop_after=None, battle_count=None, artifact_dir=None):
    from module.exception import CampaignEnd

    started = time.monotonic()
    steps = []
    state = {'round': 0, 'end': None}
    originals = {}
    failure_evidence = []
    terminal_failure = None
    out = {'execution': 'upstream_run'}
    max_rounds = int(max_rounds)
    max_seconds = float(max_seconds)
    if max_rounds <= 0 or max_seconds <= 0:
        raise ValueError('max_rounds and max_seconds must be positive')

    def record_failure(step, error):
        # Nested wrappers see the same exception. Keep the earliest image and
        # share its path rather than overwriting it during exception unwinding.
        for captured_error, details, first_step in failure_evidence:
            if captured_error is error:
                step.update(details)
                return first_step
        details = {}
        try:
            image = getattr(getattr(inst, 'device', None), 'image', None)
            if image is None:
                details['failure_frame_error'] = 'No captured device image is available'
            else:
                # The device array is already RGB. No screenshot, device call or
                # BGR conversion is needed; copy now to preserve the failure frame.
                from PIL import Image
                captured = Image.fromarray(image.copy())
                directory = (Path(artifact_dir) if artifact_dir is not None else
                             Path(__file__).resolve().parent.parent / 'data' / 's3_failures')
                directory = directory.resolve()
                directory.mkdir(parents=True, exist_ok=True)
                stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')
                path = directory / f'{stamp}_{step["step"]}.png'
                captured.save(path, format='PNG')
                details['failure_frame'] = str(path)
                out.setdefault('failure_frame', str(path))
                out.setdefault('failure_frames', []).append(str(path))
        except Exception as save_error:
            # Saving diagnostic evidence must not replace the upstream failure.
            details['failure_frame_error'] = f'{type(save_error).__name__}: {save_error}'
        failure_evidence.append((error, details, step))
        step.update(details)
        return step

    def wrap(name):
        original = getattr(inst, name, None)
        if not callable(original):
            return
        originals[name] = (name in vars(inst), vars(inst).get(name))

        def observed(*args, **kwargs):
            if time.monotonic() - started >= max_seconds:
                raise _ExecutionBoundary('time_limit')
            is_battle = name in ('execute_a_battle', 'auto_search_execute_a_battle')
            if is_battle:
                if state['round'] >= max_rounds:
                    raise _ExecutionBoundary('round_limit')
                state['round'] += 1
            step = {'step': name}
            if is_battle:
                step['round'] = state['round']
            steps.append(step)
            operation_started = time.monotonic()
            try:
                if is_battle and withdraw_file and os.path.exists(withdraw_file):
                    # 运行中请求撤退（请求文件出现即触发）—— 调用**上游自己的** withdraw()：
                    # 它以 `raise CampaignEnd('Withdraw')` 收尾（module/map/map_operation.py:410），
                    # 于是异常调用栈里有 `withdraw` 帧，合同的判据（s3_campaign_outcome.py:72）
                    # 会判 outcome=withdrawn。挂钩点选"每次战斗之前"：那正是地图界面、可撤退的状态。
                    logger.warning('Withdraw requested (file: %s)', withdraw_file)
                    with observe_battle_result(inst) as evidence:
                        inst.withdraw()
                else:
                    with observe_battle_result(inst) as evidence:
                        value = original(*args, **kwargs)
            except CampaignEnd as error:
                end = classify_campaign_end(error, evidence)
                # An enclosing wrapper must not replace an inner withdrawal's
                # evidence after the original upstream exception is rethrown.
                if state['end'] is None or end['outcome'] == 'withdrawn':
                    state['end'] = end
                step.update({key: end[key] for key in (
                    'campaign_end', 'completed', 'cleared', 'outcome', 'reason')})
                raise
            except _ExecutionBoundary:
                raise
            except Exception as error:
                import traceback
                step['error'] = f'{type(error).__name__}: {error}'
                step['traceback_tail'] = traceback.format_exc().strip().splitlines()[-8:]
                record_failure(step, error)
                raise
            finally:
                step['ms'] = round((time.monotonic() - operation_started) * 1000, 1)
                if is_battle:
                    step['battle_count'] = getattr(inst, 'battle_count', None)
            if name == 'map_init':
                if battle_count is not None:
                    inst.battle_count = int(battle_count)
                    step['resumed_battle_count'] = inst.battle_count
                if stop_after == 'map_init':
                    raise _ExecutionBoundary('stopped_after_map_init')
            return value

        setattr(inst, name, observed)

    for method in ('enter_map', 'handle_map_fleet_lock', 'map_init',
                   'execute_a_battle', 'auto_search_execute_a_battle', 'withdraw'):
        wrap(method)
    try:
        with observe_battle_result(inst) as evidence:
            out['upstream_returned'] = inst.run()
    except CampaignEnd as error:
        if state['end'] is None:
            state['end'] = classify_campaign_end(error, evidence)
    except _ExecutionBoundary as boundary:
        out['stop_reason'] = boundary.reason
        if boundary.reason == 'stopped_after_map_init':
            out['stopped_after'] = 'map_init'
    except Exception as error:
        import traceback
        step = {'step': 'run', 'error': f'{type(error).__name__}: {error}',
                'traceback_tail': traceback.format_exc().strip().splitlines()[-8:]}
        # An earlier error may have been recovered by the native subclass.
        # Only the exception escaping run is terminal; reuse its observed step.
        terminal_failure = record_failure(step, error)
        if terminal_failure is step:
            steps.append(step)
    finally:
        for name, (owned, value) in originals.items():
            if owned:
                setattr(inst, name, value)
            else:
                delattr(inst, name)
    out['steps'] = steps
    out['elapsed_s'] = round(time.monotonic() - started, 1)
    if state['end'] is None and terminal_failure is None:
        out.setdefault('stop_reason', 'upstream_returned_without_clear')
    return finalize_sortie_result(out, steps, state['end'], terminal_failure=terminal_failure)
