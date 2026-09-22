# -*- coding: utf-8 -*-
"""放大截图的一块区域，用来看清游戏图标的像素长相。

    python tools/diagnostics/oneoff/zoom.py --src data/_map_now3.png \
        --box 540,240,740,440 --out data/_zoom_boss.png --scale 3
"""
import argparse
import os
import sys

import cv2

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, '..', '..', '..'))


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--src', required=True)
    p.add_argument('--box', required=True, help='x1,y1,x2,y2（原图像素坐标）')
    p.add_argument('--out', required=True)
    p.add_argument('--scale', type=int, default=3)
    args = p.parse_args()

    src = args.src if os.path.isabs(args.src) else os.path.join(REPO, args.src)
    out = args.out if os.path.isabs(args.out) else os.path.join(REPO, args.out)
    x1, y1, x2, y2 = [int(v) for v in args.box.split(',')]
    img = cv2.imread(src)
    if img is None:
        print(f'读不到图: {src}')
        return 1
    crop = img[y1:y2, x1:x2]
    big = cv2.resize(crop, None, fx=args.scale, fy=args.scale, interpolation=cv2.INTER_NEAREST)
    os.makedirs(os.path.dirname(out) or '.', exist_ok=True)
    ok = cv2.imwrite(out, big)
    print(f'src={src} shape={img.shape} crop={crop.shape} -> {out} ok={ok}')
    # 顺带报一下这块区域的均值/主色，便于判断"用什么颜色特征去认它"
    import numpy as np
    b, g, r = [float(v) for v in crop.reshape(-1, 3).mean(axis=0)]
    print(f'mean BGR = ({b:.1f}, {g:.1f}, {r:.1f})')
    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    h, s, v = [float(x) for x in hsv.reshape(-1, 3).mean(axis=0)]
    print(f'mean HSV = ({h:.1f}, {s:.1f}, {v:.1f})')
    print('top hues:', np.bincount(hsv[:, :, 0].ravel(), minlength=180).argsort()[-5:][::-1].tolist())
    return 0


if __name__ == '__main__':
    sys.exit(main())
