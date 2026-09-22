# -*- coding: utf-8 -*-
"""统一验收：界面与控件识别全量扫描。"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import alas_vision as av  # noqa: E402

ADB = os.environ['STUB_ADB']
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')
subprocess.run([ADB, '-s', '127.0.0.1:16384', 'exec-out', 'screencap', '-p'],
               stdout=open(PROBE, 'wb'), check=False)
av.handle_line(json.dumps({'id': 1, 'op': 'screenshot_load', 'args': {'path': PROBE}}))

raw = av.handle_line(json.dumps({'id': 2, 'op': 'ui_rules_sweep'}))
resp = json.loads(raw)
if not resp.get('ok'):
    print('SWEEP FAILED:', resp.get('error'))
    print(json.dumps(resp.get('traceback'), ensure_ascii=False))
    raise SystemExit(1)

r = resp['result']
print('SUMMARY', json.dumps(r['summary'], ensure_ascii=False))
for key in ('pages', 'module_level', 'cached_property'):
    v = r[key]
    print('  %-16s total=%3d driven=%3d hit=%3d errors=%d'
          % (key, v['total'], v['driven'], len(v['hit']), len(v['errors'])))
    for e in v['errors'][:2]:
        print('     ERR', str(e)[:160])
print('页面命中:', r['pages']['hit'])
print('模块级命中:', r['module_level']['hit'])
