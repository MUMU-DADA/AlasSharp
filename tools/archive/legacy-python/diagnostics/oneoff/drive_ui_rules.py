# -*- coding: utf-8 -*-
"""驱动全部模块级 UI 规则实例（Switch/Scroll），验证上游代码能被正确调用。"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import alas_vision as av  # noqa: E402

ADB = os.environ['STUB_ADB']
SERIAL = '127.0.0.1:16384'
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')

subprocess.run([ADB, '-s', SERIAL, 'exec-out', 'screencap', '-p'],
               stdout=open(PROBE, 'wb'), check=False)
av.handle_line(json.dumps({'id': 1, 'op': 'screenshot_load', 'args': {'path': PROBE}}))

inv = json.loads(av.handle_line(json.dumps({'id': 2, 'op': 'ui_rule_list'})))['result']
rules = inv['rules']
ok = fail = 0
for r in rules:
    raw = av.handle_line(json.dumps({'id': 3, 'op': 'ui_rule_check',
                                     'args': {'module': r['module'], 'name': r['name']}}))
    res = json.loads(raw)['result']
    bad = {k: v for k, v in res['results'].items()
           if isinstance(v, str) and ('Error' in v or 'Exception' in v)}
    if bad:
        fail += 1
    else:
        ok += 1
    text = json.dumps(res['results'], ensure_ascii=False)
    print('%-7s %-26s %s%s' % (r['class'], r['name'], text[:88], '   <<< 异常' if bad else ''))

print()
print('驱动成功 %d / 异常 %d / 共 %d' % (ok, fail, len(rules)))
