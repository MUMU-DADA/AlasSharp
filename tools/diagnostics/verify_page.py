# -*- coding: utf-8 -*-
"""页面级验证：导航到指定页面后跑全量识别，看该页规则是否真正命中。

这是把 "constructed / driven" 升级为 "在该页命中" 的关键一步：
只有规则在自己的页面上返回真，才算识别正确。
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import alas_vision as av  # noqa: E402

ADB = os.environ['STUB_ADB']
SERIAL = '127.0.0.1:16384'
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')

# 目标：从主界面进仓库/宿舍页，验证该页的规则
TARGET_BUTTON = os.environ.get('TARGET_BUTTON', 'ui/MAIN_GOTO_STORAGE')
EXPECT_PAGE = os.environ.get('EXPECT_PAGE', 'page_storage')


def shot():
    subprocess.run([ADB, '-s', SERIAL, 'exec-out', 'screencap', '-p'],
                   stdout=open(PROBE, 'wb'), check=False)
    av.handle_line(json.dumps({'id': 1, 'op': 'screenshot_load', 'args': {'path': PROBE}}))


def sweep():
    r = json.loads(av.handle_line(json.dumps({'id': 2, 'op': 'ui_rules_sweep'})))['result']
    return r


shot()
before = sweep()
print('点击前: pages=%s  cached_property hit=%s'
      % (before['pages']['hit'], before['cached_property']['hit']))

center = json.loads(av.handle_line(json.dumps(
    {'id': 3, 'op': 'asset_button_center', 'args': {'asset': TARGET_BUTTON}})))['result']['center']
print('点击 %s 中心 %s' % (TARGET_BUTTON, center))
subprocess.run([ADB, '-s', SERIAL, 'shell', 'input', 'tap',
                str(center[0]), str(center[1])], check=False)
time.sleep(3.0)

shot()
after = sweep()
print('点击后: pages=%s' % (after['pages']['hit'],))
print('        cached_property hit=%s' % (after['cached_property']['hit'],))
print('        module_level hit=%s' % (after['module_level']['hit'],))
print()
print('期望页面 %s: %s' % (EXPECT_PAGE, '✓ 命中' if EXPECT_PAGE in after['pages']['hit'] else '✗ 未命中'))
