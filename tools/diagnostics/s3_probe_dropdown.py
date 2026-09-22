# -*- coding: utf-8 -*-
"""判定「点『选择』是否展开舰队下拉」——一次跑通，带状态前置校验。

背景：`map_fleet_preparation.FleetOperator` 的文档说 `choose` 是"打开/关闭下拉菜单"的按钮。
ALAS 的流程是：点 choose → 展开下拉(`FLEET_1_BAR`) → `parse_fleet_bar()` 读下拉找序号 → 点对应项。
若点了不展开，就会反复点 → `GameTooManyClickError: Too many click for a button: FLEET_1_CHOOSE`（实测）。

上一轮这个实验**因为画面状态漂移而没做成**（那时并不在 page_campaign），所以本轮：
  1. 先校验状态，不满足就**直接退出**（绝不带病往下跑）；
  2. 逐步骤打印素材分数，让"在哪一步、看到什么"可复盘；
  3. 结束时把浮层关掉并复验干净状态。

安全：全程只在**出击前的准备界面**里操作（点关卡节点、点准备面板、点选择），不点「立刻前往」，
不进入战斗、不耗油。
"""
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
sys.path.insert(0, HERE)

import adb_util                      # noqa: E402
import alas_vision as av             # noqa: E402

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

SHOT = os.path.join(ROOT, 'data', '_dropdown_%s.png')


def op(_op, **args):
    """发一次协议请求。参数名用 `_op` 而不是 `name` —— 业务参数里也有 `name`（调用哪个方法），
    撞名会报 `TypeError: op() got multiple values for argument 'name'`（实测踩过）。"""
    resp = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _op, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(f'{_op}: {resp.get("error")}')
    return resp['result']


def snap(tag):
    """重新抓一帧并交给宿主（后续素材判定都基于这一帧）。"""
    path = SHOT % tag
    adb_util.screencap(path)
    op('screenshot_load', path=path)
    return path


def score(asset):
    m = op('button_match', asset=asset, probe_score=True)
    return (bool(m.get('match')), round(m.get('score') or -1, 4))


def tap_asset(asset):
    b = op('asset_button_center', asset=asset)
    c = b.get('center') or b.get('button_center')
    if not c:
        return False
    adb_util.shell('input', 'tap', str(int(c[0])), str(int(c[1])))
    return True


def state(tag):
    snap(tag)
    return (op('page_current').get('hit') or [],
            score('map/MAP_PREPARATION'), score('map/FLEET_PREPARATION'))


def main():
    adb_util.ensure()
    pages, prep, overlay = state('s0')
    print(f'[状态] pages={pages} prep={prep} overlay={overlay}', flush=True)
    if 'page_campaign' not in pages:
        print('[退出] 当前不在 page_campaign —— 前置条件不满足，直接停下（不带着漂移的状态往下跑）')
        return 1

    # 1) 点关卡节点（经 ALAS：ensure_chapter 必须在 get_entrance 之前）
    op('s3_campaign_init', chapter='campaign.campaign_main.campaign_1_1')
    op('s3_campaign_call', name='campaign_ensure_chapter', args=[1], allow_actions=True)
    op('s3_campaign_call', name='campaign_get_entrance', args=['1-1'],
       store='ENTRANCE', allow_actions=True)
    op('s3_campaign_call', name='device.click', args=['@ENTRANCE'], allow_actions=True)
    time.sleep(2.5)
    print(f'[节点] prep={score("map/MAP_PREPARATION")}', flush=True)

    # 2) 点准备面板 → 打开舰队选择浮层
    r = op('s3_campaign_call', name='handle_map_preparation', store='PREP', allow_actions=True)
    print(f'[准备] handle_map_preparation ms={r.get("ms")} stored={r.get("stored")}', flush=True)
    if not r.get('stored'):
        print('[退出] 没拿到准备面板按钮，停下')
        return 1
    op('s3_campaign_call', name='device.click', args=['@PREP'], allow_actions=True)
    time.sleep(3.0)
    pages2, prep2, overlay2 = state('s1')
    print(f'[浮层] pages={pages2} prep={prep2} overlay={overlay2}', flush=True)
    if not overlay2[0]:
        print('[退出] 舰队选择浮层没打开，无法做下拉实验')
        return 1

    # 3) 关键：点「选择」前 / 后，量下拉素材
    before = {a: score(a) for a in ('map/FLEET_1_BAR', 'map/FLEET_1_CHOOSE',
                                    'map/FLEET_1_IN_USE')}
    print(f'[点选前] {json.dumps(before, ensure_ascii=False)}', flush=True)
    clicked = tap_asset('map/FLEET_1_CHOOSE')
    time.sleep(2.5)
    snap('s2')
    after = {a: score(a) for a in ('map/FLEET_1_BAR', 'map/FLEET_1_CHOOSE',
                                   'map/FLEET_1_IN_USE')}
    print(f'[点选后] clicked={clicked} {json.dumps(after, ensure_ascii=False)}', flush=True)

    changed = [a for a in before if before[a] != after[a]]
    print(f'[判定] 变化的素材: {changed or "无"}；BAR 点选前={before["map/FLEET_1_BAR"]} '
          f'点选后={after["map/FLEET_1_BAR"]}', flush=True)

    # 4) 收尾：关浮层并复验
    for _ in range(3):
        snap('s3')
        if not score('map/FLEET_PREPARATION')[0]:
            break
        adb_util.shell('input', 'tap', '1160', '124')
        time.sleep(1.5)
    snap('s4')
    print(f'[收尾] pages={op("page_current").get("hit")} '
          f'overlay={score("map/FLEET_PREPARATION")}', flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
