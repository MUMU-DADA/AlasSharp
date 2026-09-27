# -*- coding: utf-8 -*-
"""快速截屏到文件（走引擎选定的截图后端）。

用途：看清"当前游戏到底停在哪一屏"。此前每次都要临时拼一段脚本导入
`module.device.pkg_resources` + 造 `Device`，所以固化成脚本。

    python tools/diagnostics/shoot.py --out data/_shot.png --serial 127.0.0.1:16384

也可顺带点一下坐标（--click X,Y，可重复），用于把界面推到想要的屏。
"""
import argparse
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
    p = argparse.ArgumentParser()
    p.add_argument('--out', required=True)
    p.add_argument('--serial', default=os.environ.get('SERIAL', '127.0.0.1:16384'))
    p.add_argument('--screenshot', default='scrcpy')
    p.add_argument('--control', default='MaaTouch')
    p.add_argument('--click', action='append', default=[])
    p.add_argument('--raw', action='store_true')
    args = p.parse_args()

    # **必须在构造设备之前**把输出路径定成绝对路径：引擎在启动时会 `chdir` 到引擎根目录，
    # 之后再 `abspath()` 会解析到 `engine/data/`（该目录不存在，`cv2.imwrite` 只会**静默返回
    # False**，不抛异常）—— 实测踩过：报出来的 path 有文件，磁盘上没有。
    out = os.path.abspath(args.out)
    os.makedirs(os.path.dirname(out) or '.', exist_ok=True)

    print(json.dumps(op('device_configure', serial=args.serial,
                        screenshot=args.screenshot, control=args.control),
                     ensure_ascii=False), flush=True)
    for i, spec in enumerate(args.click):
        x, y = [int(v) for v in spec.split(',')]
        print(f'click #{i + 1} ({x},{y}):',
              json.dumps(op('device_click', x=x, y=y), ensure_ascii=False), flush=True)

    r = op('device_screencap', path=out, raw=args.raw)
    print('screencap:', json.dumps(r, ensure_ascii=False), flush=True)
    if not os.path.exists(out):
        print(f'!! 截图未落盘: {out}', flush=True)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
