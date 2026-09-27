# -*- coding: utf-8 -*-
"""临时探针：看清"关卡面板开着"时上游哪些素材认得出、返回键能不能关掉它。"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..')))
import alas_vision as av          # noqa: E402


def op(_n, **a):
    r = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _n, 'args': a})))
    if not r.get('ok'):
        raise RuntimeError(f'{_n}: {r.get("error")}')
    return r['result']


print('capture:', op('device_capture_set'))
for asset in ('map/MAP_PREPARATION', 'map/MAP_PREPARATION_HARD', 'map/MAP_PREPARATION_CANCEL',
              'campaign/CAMPAIGN_CHECK', 'ui/BACK_ARROW'):
    try:
        r = op('appear_on', asset=asset, threshold=10)
        print(f'  {asset}: appear={r.get("appear")} color={r.get("color")} got={r.get("got")}')
    except Exception as e:
        print(f'  {asset}: {type(e).__name__}: {e}')

print('page_current:', op('page_current'))
print('device_back:', op('device_back'))
import time
time.sleep(2)
print('capture2:', op('device_capture_set'))
print('MAP_PREPARATION after back:', op('appear_on', asset='map/MAP_PREPARATION')['appear'])
print('page_current after back:', op('page_current'))
