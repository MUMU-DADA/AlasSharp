# -*- coding: utf-8 -*-
"""设备引擎（多后端）回归：后端可切换、能抓到合法帧、点击可用。

为什么需要：设备层是本项目整合的一整块子系统（15 个后端、scrcpy 默认、
MaaTouch 输入），此前**没有任何自动化测试** —— 换了后端或改了宿主接线，
只能靠人工想起来去验。本脚本把它变成一次可重复的检查。

需要**真机/模拟器在线**（会读屏；点击只点在安全的空白坐标上）。
用法：`python tools/diagnostics/verify_device_engine.py`
"""
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

import alas_vision as av          # noqa: E402

SERIAL = os.environ.get('SERIAL', '127.0.0.1:16384')
# 本机实测可用且已设为默认的组合（见 docs/archive/history/device-engine.md）
CASES = [
    ('scrcpy', 'MaaTouch'),
    ('adb', 'ADB'),
]


def op(_op, **args):
    resp = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _op, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(f'{_op}: {resp.get("error")}')
    return resp['result']


def main():
    ok = True
    print('%-10s %-10s %-8s %-16s %s' % ('截图后端', '输入后端', '抓图ms', '帧尺寸', '点击ms'))
    for shot, ctrl in CASES:
        try:
            op('device_configure', serial=SERIAL, screenshot=shot, control=ctrl)
            info = op('device_info')
            if info.get('screenshot_method') != shot:
                print('%-10s %-10s %s' % (shot, ctrl, '**后端没切过去**'))
                ok = False
                continue
            cap = op('device_capture_set')          # 抓图并置入宿主
            size = cap.get('shape') or []
            clicks = []
            for _ in range(2):
                c = op('device_click', x=640, y=360)   # 屏幕中心，安全坐标
                clicks.append(c.get('ms'))
            ok = ok and size and len(size) >= 2
            print('%-10s %-10s %-8s %-16s %s' % (
                shot, ctrl, cap.get('capture_ms'), str(size[:2]),
                '/'.join(str(x) for x in clicks)))
        except Exception as e:
            print('%-10s %-10s **失败**: %s' % (shot, ctrl, str(e)[:60]))
            ok = False
    print()
    print('设备引擎回归：%s' % ('通过' if ok else '**失败**'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
