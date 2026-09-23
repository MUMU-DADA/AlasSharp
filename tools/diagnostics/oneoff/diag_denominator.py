# -*- coding: utf-8 -*-
"""反解 cv2 在 TM_CCOEFF_NORMED 里实际用的窗口统计量。

对若干位置：由 R = num / sqrt(denomT · denomI) 反解出 cv2 隐含的 denomI，
再与几种候选定义（逐通道 / 全局合并 / 单通道）对比，从而确定公式。
"""
import json
import os
import sys

import cv2
import numpy as np

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                     '..', '..', '..'))
FORK = os.path.join(ROOT, '.runtime', 'engine')
os.chdir(FORK)
sys.path.insert(0, FORK)
import module.device.pkg_resources  # noqa: F401

from module.base.utils import crop, load_image

fx = json.load(open(os.path.join(ROOT, 'data', 'fixtures', 'matching.json'),
                    encoding='utf-8'))
target = sys.argv[1] if len(sys.argv) > 1 else 'combat/READY_AIR_RAID'
case = next(c for c in fx['match'] if c['id'] == target)

templ = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)),
                   case['area']).astype(np.float64)
img = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)))
search = crop(img, np.array((-3, -case['offset'], 3, case['offset']))
              + np.array(case['area']), copy=False).astype(np.float64)
ref = cv2.matchTemplate(templ.astype(np.uint8), search.astype(np.uint8),
                        cv2.TM_CCOEFF_NORMED).astype(np.float64)

th, tw = templ.shape[:2]
c = templ.shape[2]
N = th * tw
sumT = templ.reshape(-1, c).sum(0)
sumT2 = (templ * templ).reshape(-1, c).sum(0)
meanT = sumT / N
# 三种模板归一化定义
denomT_perch = float(np.sum(sumT2 - N * meanT * meanT))
pooled_mean_T = templ.mean()
denomT_pooled = float(((templ - pooled_mean_T) ** 2).sum())

positions = [(3, 30), (5, 30), (4, 30), (0, 0), (30, 6)]
print(f'{"pos":>9} {"cv2_R":>11} {"num_mine":>12} {"denomI_mine":>14} '
      f'{"denomI_implied":>15} {"pooled":>14} {"sum_ch_var":>13}')
for (x, y) in positions:
    w = search[y:y + th, x:x + tw]
    num = 0.0
    denomI_perch = 0.0
    for ch in range(c):
        wc = w[:, :, ch]
        tc = templ[:, :, ch]
        mI = wc.mean()
        num += float(((tc - meanT[ch]) * (wc - mI)).sum())
        denomI_perch += float(((wc - mI) ** 2).sum())
    pooled = float(((w - w.mean()) ** 2).sum())
    r = float(ref[y, x])
    implied = (num / (r * np.sqrt(denomT_perch))) ** 2 if r != 0 and denomT_perch > 0 else float('nan')
    print(f'({x:>3},{y:>3}) {r:>11.6f} {num:>12.3f} {denomI_perch:>14.3f} '
          f'{implied:>15.3f} {pooled:>14.3f} {denomI_perch:>13.3f}')

print()
print('模板统计:')
print(f'  denomT_逐通道 = {denomT_perch:.4f}')
print(f'  denomT_全局   = {denomT_pooled:.4f}')
print(f'  每通道均值     = {np.round(meanT, 4).tolist()}')
print(f'  每通道方差×N   = {np.round(sumT2 - N * meanT * meanT, 4).tolist()}')
