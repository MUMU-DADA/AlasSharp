# -*- coding: utf-8 -*-
"""就地观测 `enter_map` 的失败现场（不再手工复刻它的步骤）。

为什么用"就地观测"而不是"复刻"：`enter_map` 内部有计时器与状态
（campaign_timer/map_timer/fleet_timer、campaign_click/map_click/fleet_click、checked_in_map），
手工按顺序单独调用会被内部状态跳过或走错分支 —— 实测过，**复刻不等价**（见
docs/archive/history/s3-entry-sequence.md）。

流程（每一步都先校验，不满足就停）：
  0. **导航到 page_campaign 并校验**（上一轮 A 就是因为游戏停在 page_main 而一开局就失败）；
  1. 后台起一个进程，让它**自己跑 `enter_map`**（它会在舰队选择浮层上反复点「选择」）；
  2. 本进程每 ~1.8s 抓一帧并量素材分数，记录时间线；
  3. 打印时间线 + A 的结局。

安全：全程只到"出击前的准备/浮层"为止，**不点「立刻前往」、不进入战斗、不耗油**。
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
sys.path.insert(0, HERE)

import adb_util                      # noqa: E402
import alas_vision as av             # noqa: E402
from queue_navigation import run_navigation  # noqa: E402

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

ADB = os.environ.get('STUB_ADB', '')
ALAS_SERVER = os.path.join(ROOT, 'src', 'Alas.Server', 'bin', 'Release', 'net10.0', 'Alas.Server.exe')

DRIVER = r'''
import json, sys, time
sys.path.insert(0, r"{tools}")
import alas_vision as av

def op(_op, **a):
    r = json.loads(av.handle_line(json.dumps({{"id": 1, "op": _op, "args": a}})))
    return r["result"] if r.get("ok") else {{"ERR": str(r.get("error"))[:120]}}

op("s3_campaign_init", chapter="campaign.campaign_main.campaign_1_1")
op("s3_campaign_call", name="campaign_ensure_chapter", args=[1], allow_actions=True)
op("s3_campaign_call", name="campaign_get_entrance", args=["1-1"],
   store="ENTRANCE", allow_actions=True)
print("A_START", flush=True)
r = op("s3_campaign_call", name="enter_map", args=["@ENTRANCE", "normal"], allow_actions=True)
print("A_END %s" % str(r.get("error"))[:90], flush=True)
'''


def op(_op, **args):
    resp = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _op, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(f'{_op}: {resp.get("error")}')
    return resp['result']


def snap(tag):
    path = os.path.join(ROOT, 'data', f'_ob_{tag}.png')
    adb_util.screencap(path)
    op('screenshot_load', path=path)
    return path


def score(asset):
    m = op('button_match', asset=asset, probe_score=True)
    return round(m.get('score') or -1, 3)


def main():
    adb_util.ensure()

    # 0) 前置：导航到 page_campaign 并校验（不满足就停，绝不带病往下跑）
    print('[前置] 导航到 page_campaign ...', flush=True)
    navigation = run_navigation(ALAS_SERVER, 'page_campaign', '127.0.0.1:16384',
                                adb=ADB, timeout=300)
    if navigation.returncode != 0:
        print('[退出] 前置导航队列未完成')
        return navigation.returncode
    snap('pre')
    pages = op('page_current').get('hit') or []
    print(f'[前置] pages={pages}', flush=True)
    if 'page_campaign' not in pages:
        print('[退出] 前置不满足：不在 page_campaign')
        return 1

    # 1) 后台起 A：让它自己跑 enter_map
    driver = os.path.join(os.environ.get('TEMP', '.'), 'insitu_driver.py')
    with open(driver, 'w', encoding='utf-8') as f:
        f.write(DRIVER.format(tools=os.path.join(ROOT, 'tools')))
    log = os.path.join(os.environ.get('TEMP', '.'), 'insitu_driver.out')
    proc = subprocess.Popen([sys.executable, driver], stdout=open(log, 'w', encoding='utf-8'),
                            stderr=subprocess.STDOUT)
    time.sleep(2)

    # 2) 观测
    t0 = time.time()
    i = 0
    while time.time() - t0 < 26:
        i += 1
        snap(f'{i:02d}')
        # 成组量舰队素材：目的是找出**哪些**素材低于 ALAS 的 0.85 阈值（只量 BAR 不够）
        assets = ('map/MAP_PREPARATION', 'map/FLEET_PREPARATION', 'map/FLEET_1_CHOOSE',
                  'map/FLEET_1_CLEAR', 'map/FLEET_1_BAR', 'map/FLEET_1_IN_USE',
                  'map/FLEET_1_ADVICE', 'map/FLEET_1_HARD_SATIESFIED')
        vals = {a.split('/')[-1]: score(a) for a in assets}
        low = [k for k, v in vals.items() if v < 0.85]
        print('T%02d %4.1fs %s' % (i, time.time() - t0,
              ' '.join('%s=%.2f' % (k, v) for k, v in vals.items())), flush=True)
        print('       低于0.85: %s' % (','.join(low) or '无'), flush=True)
        time.sleep(1.8)
    try:
        proc.wait(timeout=60)
    except Exception:
        proc.kill()

    # 3) A 的结局
    print('[A 的输出]', flush=True)
    with open(log, encoding='utf-8') as f:
        for line in f:
            if line.startswith('A_'):
                print('  ' + line.strip(), flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
