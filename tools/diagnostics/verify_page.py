# -*- coding: utf-8 -*-
# ⚠️ 状态：**已被取代，保留仅作参考**（2026-09-23 决定，第 166 轮）。
#
# 原因：全仓库（文档 / 脚本 / README）**没有任何引用**它，而它做的事情已被两处覆盖：
#   * `verify_pages.py` —— 批量逐段页面验证（需要 SEGMENTS 环境变量，属驱动）；
#   * `regress_pages.py` —— 用**产品导航器**跑全量页面回归（当前的标准入口，产出 docs/archive/reports/regression.md）。
# 它没有登记进 `verify_all.py`（原因写在 STEPS 上方），所以不会在套件里跑。
#
# 为什么保留而不删：删是一个不可逆的决定，而它作为"单页验证"的最小示例仍有参考价值；
# git 历史里也能找回。**要复用它，先确认它的判据与上面两处不重复。**
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
print('期望页面 %s: %s' % (EXPECT_PAGE, 'OK 命中' if EXPECT_PAGE in after['pages']['hit'] else 'NG 未命中'))
