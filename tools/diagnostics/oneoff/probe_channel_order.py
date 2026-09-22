# -*- coding: utf-8 -*-
"""一句话结论：**引擎给上游的图，通道顺序对不对**（用设备裸 adb 截图当真值，全程不落盘读回）。

为什么必须查清：BOSS 眼睛的"红/蓝"、关卡面板"立即前往"按钮的"黄/蓝"，都表现为
"实测颜色 = 上游素材颜色把 R 与 B 对调"。两种解释后果完全不同：
  (a) 图是对的 → 是本客户端把这两个图标画成了另一种颜色 → 只能逐个适配；
  (b) 图的通道顺序反了 → **一条修正修好一整类颜色判据**（上游素材是按 RGB 编的）。

做法上刻意**避免任何"写盘再读回"**：`cv2.imwrite` 把数组当 BGR 写、`load_image` 走 PIL 读，
两者叠加就是一次 R/B 互换，会把结论整个带反（我自己踩过 ✗）。
这里只在内存里取色：`get_color(dev.screenshot(), area)` 的通道顺序 = 引擎给上游的通道顺序。

    python tools/diagnostics/oneoff/probe_channel_order.py
"""
import json
import os
import sys

import cv2
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..')))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..')))
import alas_vision as av          # noqa: E402
import adb_util                   # noqa: E402

SERIAL = os.environ.get('SERIAL', '127.0.0.1:16384')
REPO = os.path.normpath(os.path.join(HERE, '..', '..', '..'))
# 只挑**强彩色**区域：灰色区域通道对调也看不出来（这正是我上次判断失误的原因之一）
AREAS = {
    '立即前往按钮': (974, 497, 1094, 525),
    '右上资源条': (700, 10, 900, 40),
}


def op(_n, **a):
    r = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _n, 'args': a})))
    if not r.get('ok'):
        raise RuntimeError(f'{_n}: {r.get("error")}')
    return r['result']


def main():
    from module.base.utils import get_color

    truth_path = os.path.join(REPO, 'data', '_truth_adb.png')
    n = adb_util.screencap(truth_path, SERIAL)
    t_bgr = cv2.imread(truth_path)
    t_rgb = cv2.cvtColor(t_bgr, cv2.COLOR_BGR2RGB)
    print(f'设备裸 adb 截图（真值）: {n} bytes   shape={t_rgb.shape}', flush=True)

    rows = {}
    for backend in ('scrcpy', 'adb'):
        op('device_configure', serial=SERIAL, screenshot=backend, control='MaaTouch')
        try:
            img = av._device_engine().screenshot()
        except Exception as e:
            print(f'{backend}: 截图失败 {type(e).__name__}: {e}', flush=True)
            continue
        rows[backend] = {name: [round(float(v), 1) for v in get_color(img, area)]
                         for name, area in AREAS.items()}

    print(f'\n{"区域":14s} {"真值 RGB":26s} ' +
          ' '.join(f'{b:26s}' for b in rows), flush=True)
    for name, area in AREAS.items():
        x1, y1, x2, y2 = area
        t = t_rgb[y1:y2, x1:x2].reshape(-1, 3).mean(axis=0)
        line = f'{name:14s} {str(np.round(t, 1)):26s} '
        for b in rows:
            v = np.array(rows[b][name])
            same = float(np.abs(v - t).max())
            swap = float(np.abs(v - t[::-1]).max())
            verdict = '一致' if same < 12 else ('**R/B 对调**' if swap < 12 else '都不一致')
            line += f'{str(v):14s}{verdict:12s} '
        print(line, flush=True)
    print('\n判定口径：引擎列与"真值 RGB"一致 => 引擎给上游的图通道顺序正确；'
          '若只有把真值的 R/B 对调后才一致 => 引擎给的是 BGR，上游所有颜色判据都会看反。', flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
