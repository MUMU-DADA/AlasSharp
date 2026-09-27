# -*- coding: utf-8 -*-
"""给 s3_run_plan 的轮循环插入"开打前一致性校验"（幂等：已插过就跳过）。"""
import io
import os
import sys

P = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'tools', 'alas_vision.py')
P = os.path.normpath(P)
s = io.open(P, encoding='utf-8').read()

ANCHOR = "        for _step_name in ('full_scan_find_boss', 'execute_a_battle'):\n"
if ANCHOR not in s:
    print('ANCHOR NOT FOUND')
    sys.exit(1)
if 'pre_battle_health' in s:
    print('ALREADY PATCHED')
    sys.exit(0)

INSERT = '''            # **开打前再查一次"视图 vs 地图"的一致性**：`map_init` 返回时一致、开打前已经错位
            # 是实测到的真实情况（1-4：`map_init` 成功，但 `battle_0` 仍报 `No battle executed`
            # → 十次无战果 → 撤退）。判据与 `map_init` 那段同一套：视图认出了船、地图侧一艘都没有
            # ⇒ 映射错位（敌人落到非 `may_enemy` 格被丢掉）。命中就换机位 + 重跑 `map_init`。
            if _step_name == 'execute_a_battle':
                try:
                    _h = _map_init_health()
                except Exception as _e:
                    _h = {'health_error': f'{type(_e).__name__}: {_e}'}
                steps.append({'round': _round, 'check': 'pre_battle_health', **_h})
                if _h.get('consistent') is False:
                    try:
                        op_device_swipe({'x1': 760, 'y1': 394, 'x2': 960, 'y2': 394,
                                         'duration': 0.4})
                    except Exception:
                        pass
                    _rr = op_s3_campaign_call({'name': 'map_init', 'args': ['@MAP'],
                                               'allow_actions': True})
                    steps.append({'round': _round, 'step': 'map_init_retry_pre_battle',
                                  'error': _rr.get('error')})
'''

s = s.replace(ANCHOR, ANCHOR + INSERT, 1)
io.open(P, 'w', encoding='utf-8', newline='\n').write(s)
print('PATCHED')

