# -*- coding: utf-8 -*-
"""7-1 那类"进图后空转"的定点探针：把"进图 + 图内初始化"单独拿出来量。

假设：进图后屏幕上的信息条（例：「已切换到第三舰队」）挡住地图 → 视图只检出 12–15 格
（该图应为 24 格）→ 目标格映射错位 → 反复 `Arrive B1 (is_fleet)` 却进不了战斗。

做法：`s3_run_plan(stop_after='map_init')` 把进图+map_init 跑完就停（**一场都不打**），
再用 `s3_probe_view` 连续量几次（视图格数 / 信息条计数 / 相机 / 上游认为还剩什么），
最后用上游自己的 `withdraw()` 收尾 —— 全程不改游戏之外的东西。

    python tools/diagnostics/oneoff/probe_71_view.py --chapter campaign.campaign_main.campaign_7_1
"""
import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..')))
import alas_vision as av          # noqa: E402

BRIEF = ('in_map', 'info_bar_count', 'view_cells', 'view_shape', 'camera',
         'battle_count', 'ammo_count', 'update_error', 'view_error')


def op(_n, **a):
    r = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _n, 'args': a})))
    if not r.get('ok'):
        raise RuntimeError(f'{_n}: {r.get("error")}')
    return r['result']


def brief(tag, v):
    print(f'--- {tag}: ' + json.dumps({k: v.get(k) for k in BRIEF if k in v},
                                      ensure_ascii=False), flush=True)
    for row in (v.get('view_show') or []):
        print('      ' + row, flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--chapter', default='campaign.campaign_main.campaign_7_1')
    p.add_argument('--fleet1', type=int, default=3)
    p.add_argument('--fleet2', type=int, default=6)
    p.add_argument('--serial', default=os.environ.get('SERIAL', '127.0.0.1:16384'))
    p.add_argument('--no-withdraw', action='store_true')
    args = p.parse_args()

    print('=== 进图 + map_init（stop_after，不打任何一场）===', flush=True)
    r = op('s3_run_plan', chapter=args.chapter, serial=args.serial,
           fleet1=args.fleet1, fleet2=args.fleet2,
           dry_run=False, allow_actions=True, max_rounds=1, max_seconds=300,
           stop_after='map_init')
    for st in (r.get('steps') or []):
        print('  step:', json.dumps(st, ensure_ascii=False)[:220], flush=True)
    print('  stopped_after =', r.get('stopped_after'), flush=True)

    for i in range(4):
        try:
            brief(f'probe #{i + 1}', op('s3_probe_view'))
        except Exception as e:
            print(f'  probe #{i + 1} 失败: {type(e).__name__}: {e}', flush=True)
        time.sleep(2)

    # 手动处理信息条后复测：若格数因此变多，就说明"信息条遮挡"是主因
    print('=== 调上游 handle_info_bar() 后再量 ===', flush=True)
    try:
        hr = op('s3_campaign_call', name='handle_info_bar', allow_actions=True)
        print('  handle_info_bar:', json.dumps({k: hr.get(k) for k in ('ms', 'error')},
                                               ensure_ascii=False), flush=True)
    except Exception as e:
        print('  handle_info_bar 失败:', e, flush=True)
    time.sleep(2)
    for i in range(3):
        try:
            brief(f'after-info-bar #{i + 1}', op('s3_probe_view'))
        except Exception as e:
            print(f'  probe 失败: {type(e).__name__}: {e}', flush=True)
        time.sleep(2)

    if not args.no_withdraw:
        print('=== 收尾（上游 withdraw）===', flush=True)
        try:
            op('s3_campaign_call', name='withdraw', allow_actions=True)
            print('  in_map =', op('s3_probe_view').get('in_map'), flush=True)
        except Exception as e:
            print('  withdraw 失败:', e, flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
