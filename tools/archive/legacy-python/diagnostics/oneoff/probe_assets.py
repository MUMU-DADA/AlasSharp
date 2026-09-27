# -*- coding: utf-8 -*-
"""资产体检：在当前屏幕上逐个检查资产是否存在（button_match + 中心坐标）。

用于回答"这个按钮到底在不在屏上"——避免盲点不该点的坐标。
输出刻意保持 ASCII，控制台是 GBK，中文会变乱码但文件与提交不受影响。
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import alas_vision as av  # noqa: E402

ADB = os.environ['STUB_ADB']
SERIAL = os.environ.get('SERIAL', '127.0.0.1:16384')
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')
ASSETS = [a for a in os.environ['ASSETS'].split(',') if a]


def op(name, **args):
    resp = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(resp.get('error'))
    return resp['result']


if os.environ.get('USE_SAVED') != '1':
    subprocess.run([ADB, '-s', SERIAL, 'exec-out', 'screencap', '-p'],
                   stdout=open(PROBE, 'wb'), check=False)
op('screenshot_load', path=PROBE)
print('on page: %s' % (op('ui_rules_sweep')['pages']['hit'],))
for asset in ASSETS:
    try:
        center = op('asset_button_center', asset=asset)['center']
    except Exception as e:  # 资产不存在（不同服务器/版本）
        print('%-34s NO-ASSET %s' % (asset, e))
        continue
    m = op('button_match', asset=asset, probe_score=True)
    score = 'None(%s)' % m['score_error'] if m.get('score') is None else '%.4f' % m['score']
    print('%-34s center=%-12s match=%-6s score=%s' % (
        asset, tuple(center), m['match'], score))
