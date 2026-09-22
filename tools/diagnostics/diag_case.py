# -*- coding: utf-8 -*-
"""定位 combat/READY_AIR_RAID 这类大偏差案例：逐位置对照 cv2 与我的公式。"""
import json
import os
import sys

import cv2
import numpy as np

FORK = r"<developer-home>\source\ALAS fork project\my fork project\AzurLaneAutoScript"
os.chdir(FORK)
sys.path.insert(0, FORK)
import module.device.pkg_resources  # noqa: F401

from module.base.utils import crop, load_image

fx = json.load(open(r"<developer-home>\source\ALAS fork project\csharp\data\fixtures\matching.json",
                    encoding='utf-8'))
target = sys.argv[1] if len(sys.argv) > 1 else 'combat/READY_AIR_RAID'
case = next(c for c in fx['match'] if c['id'] == target)

img = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)))
area = np.array(case['area'])
off = case['offset']
templ = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)),
                   case['area']).astype(np.float64)
search = crop(img, np.array((-3, -off, 3, off)) + area, copy=False).astype(np.float64)
ref = cv2.matchTemplate(templ.astype(np.uint8), search.astype(np.uint8),
                        cv2.TM_CCOEFF_NORMED).astype(np.float64)

th, tw = templ.shape[:2]
c = templ.shape[2]
N = th * tw
sumT = templ.reshape(-1, c).sum(0)
sumT2 = (templ * templ).reshape(-1, c).sum(0)
meanT = sumT / N
denomT = float(np.sum(sumT2 - N * meanT * meanT))
# 逐通道模板方差（用于诊断哪一通道是常量）
dT_c = sumT2 - N * meanT * meanT

print(json.dumps({
    'id': case['id'],
    'template_shape': list(templ.shape), 'search_shape': list(search.shape),
    'template_per_channel_mean': [round(float(v), 4) for v in meanT],
    'template_per_channel_var': [round(float(v), 6) for v in dT_c],
    'denomT': denomT,
    'ref_max': float(ref.max()), 'ref_min': float(ref.min()),
    'template_unique_colors': [int(len(np.unique(templ[:, :, ch]))) for ch in range(c)],
}, ensure_ascii=False, indent=2))

ii = np.zeros((search.shape[0] + 1, search.shape[1] + 1, c))
ii2 = np.zeros_like(ii)
for ch in range(c):
    ii[1:, 1:, ch] = search[:, :, ch].cumsum(0).cumsum(1)
    ii2[1:, 1:, ch] = (search[:, :, ch] ** 2).cumsum(0).cumsum(1)

rh, rw = ref.shape
rows = []
for y in range(rh):
    for x in range(rw):
        num = 0.0
        denomI = 0.0
        for ch in range(c):
            s = ii[y + th, x + tw, ch] - ii[y, x + tw, ch] - ii[y + th, x, ch] + ii[y, x, ch]
            s2 = ii2[y + th, x + tw, ch] - ii2[y, x + tw, ch] - ii2[y + th, x, ch] + ii2[y, x, ch]
            w = search[y:y + th, x:x + tw, ch]
            mI = s / N
            num += float((templ[:, :, ch] * w).sum()) - mI * sumT[ch] - meanT[ch] * s + N * meanT[ch] * mI
            denomI += s2 - s * s / N
        mine = num / np.sqrt(denomT * denomI) if denomI > 0 else 0.0
        rows.append((abs(mine - ref[y, x]), y, x, mine, float(ref[y, x]), denomI))

rows.sort(reverse=True)
print('\n最大的 6 个偏差位置：')
for d, y, x, mine, r, denomI in rows[:6]:
    print(f'  ({x},{y})  mine={mine:+.6f}  cv2={r:+.6f}  diff={d:.6f}  denomI={denomI:.6g}')
print(f'\n窗口方差 <=0 的位置数: {sum(1 for r in rows if r[5] <= 0)} / {len(rows)}')
print(f'窗口方差 <1e-6 的位置数: {sum(1 for r in rows if r[5] < 1e-6)} / {len(rows)}')
