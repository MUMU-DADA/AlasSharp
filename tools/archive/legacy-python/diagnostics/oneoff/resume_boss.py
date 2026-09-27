# -*- coding: utf-8 -*-
"""从"小怪已清完、只剩 BOSS"的半途状态**直接打 BOSS**，验证 BOSS 识别垫片是否真的打通了链路。

为什么单独写这一个脚本：`map_init` 会把 `battle_count` 清 0（`map_data_init`），而
`battle_count` 决定 `battle_function()` 选哪个 `battle_N`。半途状态下清 0 只会去跑
`battle_0`（清路障）→ 十次无战果 → 上游 `withdraw()`。所以这里在 `map_init` 之后
把 `battle_count` 复到 6，让上游自己走 `battle_6`（11-1 的 BOSS 分支）。

**不调 `enter_map`** —— 游戏此刻已经在这张图里，再进一次是错的。

    python tools/diagnostics/oneoff/resume_boss.py --chapter campaign.campaign_main.campaign_11_1 \
        --battle-count 6 --fleet1 3 --fleet2 6
"""
import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..')))
import alas_vision as av          # noqa: E402


def op(_op_name, **args):
    resp = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _op_name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(f'{_op_name}: {resp.get("error")}')
    return resp['result']


def show_map(inst, tag):
    print(f'===== {tag} =====', flush=True)
    try:
        inst.map.show()
    except Exception as e:
        print('  map.show 失败:', e, flush=True)
    for key in ('is_boss', 'is_enemy', 'may_boss'):
        try:
            grids = inst.map.select(**{key: True})
            print(f'  {key}: {[str(g) for g in grids]}', flush=True)
        except Exception as e:
            print(f'  {key} 查询失败:', e, flush=True)
    print(f'  battle_count={getattr(inst, "battle_count", None)} '
          f'camera={getattr(inst, "camera", None)} '
          f'ammo={getattr(inst, "ammo_count", None)}', flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--chapter', default='campaign.campaign_main.campaign_11_1')
    p.add_argument('--battle-count', type=int, default=6)
    p.add_argument('--fleet1', type=int, default=3)
    p.add_argument('--fleet2', type=int, default=6)
    p.add_argument('--submarine', type=int, default=0)
    p.add_argument('--skip-scan', action='store_true',
                   help='跳过 full_scan_find_boss（只测 map_init 的覆盖）')
    args = p.parse_args()

    print('init:', json.dumps(op('s3_campaign_init', chapter=args.chapter,
                                 fleet1=args.fleet1, fleet2=args.fleet2,
                                 submarine_fleet=args.submarine), ensure_ascii=False),
          flush=True)
    inst = av._CAMPAIGN['obj']

    for step in (('handle_map_fleet_lock', None), ('map_init', ['@MAP'])):
        t0 = time.time()
        r = op('s3_campaign_call', name=step[0], args=step[1] or [], allow_actions=True)
        print(f'--- {step[0]}: {round(time.time() - t0, 1)}s '
              f'error={r.get("error")} completed={r.get("completed")}', flush=True)
        if r.get('error'):
            print('上游报错，停在这里（不再往下打）', flush=True)
            return 1
    show_map(inst, 'map_init 之后')

    if not args.skip_scan:
        t0 = time.time()
        r = op('s3_campaign_call', name='full_scan_find_boss', allow_actions=True)
        print(f'--- full_scan_find_boss: {round(time.time() - t0, 1)}s '
              f'error={r.get("error")}', flush=True)
        show_map(inst, 'full_scan_find_boss 之后')

    if not inst.map.select(is_boss=True):
        print('!! 仍然没有 is_boss 格 —— 垫片没生效或 BOSS 不在 may_boss 格上，不再往下打',
              flush=True)
        return 2

    had = getattr(inst, 'battle_count', None)
    inst.battle_count = args.battle_count
    print(f'--- battle_count: {had} -> {inst.battle_count}', flush=True)

    t0 = time.time()
    r = op('s3_campaign_call', name='execute_a_battle', allow_actions=True)
    print(f'--- execute_a_battle: {round(time.time() - t0, 1)}s '
          f'error={r.get("error")} completed={r.get("completed")}', flush=True)
    show_map(inst, 'execute_a_battle 之后')
    print('in_map =', getattr(inst, 'is_in_map', lambda: None)(), flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
