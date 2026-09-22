# -*- coding: utf-8 -*-
"""跑引擎自带的截图性能基准（`Emulator_ScreenshotMethod='auto'`），看它选哪个。

为什么单独写脚本：基准内部会调 `sys.stdout.fileno()`，所以**不能用 StringIO 劫持 stdout**
（会报 `UnsupportedOperation: fileno`）。这里直接让输出走真实 stdout，由调用方重定向到文件。
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..')))
import alas_vision as av          # noqa: E402


def op(name, **args):
    resp = json.loads(av.handle_line(json.dumps({'id': 1, 'op': name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(f'{name}: {resp.get("error")}')
    return resp['result']


def main():
    serial = os.environ.get('SERIAL', '127.0.0.1:16384')
    print('=== configure screenshot=auto ===', flush=True)
    print(json.dumps(op('device_configure', serial=serial, screenshot='auto',
                        control='MaaTouch'), ensure_ascii=False), flush=True)
    # 首次调用触发基准；基准自身会打印各方案耗时与被选中的方案
    print('=== first capture (triggers benchmark) ===', flush=True)
    try:
        r = op('device_screencap', raw=False)
        print('capture:', json.dumps(r, ensure_ascii=False), flush=True)
    except Exception as e:
        print('capture failed:', e, flush=True)
    try:
        print('chosen:', json.dumps(op('device_info'), ensure_ascii=False), flush=True)
    except Exception as e:
        print('device_info failed:', e, flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
