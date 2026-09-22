# -*- coding: utf-8 -*-
"""离线复算 `bar_opened()` 的亮度判据：弄清"下拉展开时 ALAS 到底看不看得见"。

背景（见 docs/s3-entry-sequence.md）：`FleetOperator.open()` 会
`if bar_opened(): break else click(choose)`，`Timer(3, count=6)` 点满 6 次就
`GameTooManyClickError`。而判据是**亮度**，不是素材匹配：

    luma = rgb2gray(main.image_crop(self._bar.button))[:, -1]
    return np.sum(luma > 168) / luma.size > 0.5

所以必须按 ALAS 自己的几何来算：先 `appear(FLEET_1_CLEAR, offset=FleetOperator.OFFSET)`
定基准（本客户端 CLEAR 匹配 0.99 ✓），再 `FLEET_1_BAR.load_offset(FLEET_1_CLEAR)`
得到实际裁剪区域 —— 直接调上游对象，不手抄坐标。

用法：`python s3_bar_brightness.py [帧路径...]`（默认扫 data/_ob_*.png）
"""
import glob
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
ENGINE = os.path.normpath(os.path.join(ROOT, '.runtime', 'engine'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
sys.path.insert(0, ENGINE)

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

import module.device.pkg_resources          # noqa: E402,F401  （adbutils 的桩必须先导入）
import numpy as np                          # noqa: E402
from module.base.utils import rgb2gray      # noqa: E402
from module.map import assets as A          # noqa: E402
from module.map.map_fleet_preparation import FleetOperator  # noqa: E402


def ratio(button, image):
    """按上游 `bar_opened()` 的算法算占比（最右一列亮度 >168 的比例）。"""
    x1, y1, x2, y2 = [int(v) for v in button]
    crop = image[y1:y2, x1:x2]
    if crop.size == 0:
        return None, None
    luma = rgb2gray(crop)[:, -1]
    return float(np.sum(luma > 168) / luma.size), luma.size


def main():
    from PIL import Image                            # noqa: E402
    frames = sys.argv[1:] or sorted(glob.glob(os.path.join(ROOT, 'data', '_ob_*.png')))
    if not frames:
        print('没有可用帧（先跑 s3_observe_entry.py 生成 data/_ob_*.png）')
        return 1
    print('阈值：最右列亮度>168 的占比 > 0.5 才认定"下拉已展开"')
    print('%-14s %-18s %-26s %s' % ('frame', 'CLEAR 匹配', 'BAR.button(load_offset 后)', '占比'))
    for path in frames:
        img = np.array(Image.open(path).convert('RGB'))
        clear = A.FLEET_1_CLEAR
        if not clear.match(img, offset=FleetOperator.OFFSET):
            print('%-14s %-18s %s' % (os.path.basename(path), '未匹配', '-'))
            continue
        bar = A.FLEET_1_BAR
        try:
            bar.load_offset(clear)
        except Exception as e:
            print('%-14s %-18s load_offset 失败: %s' % (os.path.basename(path), 'ok', e))
            continue
        r, size = ratio(bar.button, img)
        verdict = '展开' if (r is not None and r > 0.5) else 'ALAS 认为未展开'
        print('%-14s %-18s %-26s %s' % (
            os.path.basename(path), 'ok',
            str(tuple(int(v) for v in bar.button)),
            ('%.3f (n=%d) -> %s' % (r, size, verdict)) if r is not None else '裁剪为空'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
