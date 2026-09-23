# -*- coding: utf-8 -*-
"""验证猜测：与 cv2 的残差是否随模板尺寸增大而出现（OpenCV 对大模板会切到 DFT 相关）。

做法：对基准里全部匹配用例，用与 C# 相同的公式在 numpy 里算一遍，统计残差与模板尺寸的关系。
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


def my_ccoeff(image, templ):
    if templ.shape[0] > image.shape[0] or templ.shape[1] > image.shape[1]:
        image, templ = templ, image
    ih, iw = image.shape[:2]
    th, tw = templ.shape[:2]
    rh, rw = ih - th + 1, iw - tw + 1
    c = image.shape[2]
    N = th * tw
    I = image.astype(np.float64)
    T = templ.astype(np.float64)
    sumT = T.reshape(-1, c).sum(0)
    sumT2 = (T * T).reshape(-1, c).sum(0)
    meanT = sumT / N
    denomT = float(np.sum(sumT2 - N * meanT * meanT))
    ii = np.zeros((ih + 1, iw + 1, c))
    ii2 = np.zeros((ih + 1, iw + 1, c))
    for ch in range(c):
        ii[1:, 1:, ch] = I[:, :, ch].cumsum(0).cumsum(1)
        ii2[1:, 1:, ch] = (I[:, :, ch] ** 2).cumsum(0).cumsum(1)
    out = np.zeros((rh, rw))
    flatT = denomT <= 0
    for y in range(rh):
        for x in range(rw):
            if flatT:
                out[y, x] = 1.0
                continue
            num = 0.0
            denomI = 0.0
            for ch in range(c):
                s = (ii[y + th, x + tw, ch] - ii[y, x + tw, ch]
                     - ii[y + th, x, ch] + ii[y, x, ch])
                s2 = (ii2[y + th, x + tw, ch] - ii2[y, x + tw, ch]
                      - ii2[y + th, x, ch] + ii2[y, x, ch])
                w = I[y:y + th, x:x + tw, ch]
                t = T[:, :, ch]
                mI = s / N
                num += float((t * w).sum()) - mI * sumT[ch] - meanT[ch] * s + N * meanT[ch] * mI
                denomI += s2 - s * s / N
            out[y, x] = num / np.sqrt(denomT * denomI) if denomI > 0 else 0.0
    return out


rows = []
for case in fx['match']:
    img = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)))
    area = np.array(case['area'])
    off = case['offset']
    templ = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)),
                       case['area']).astype(np.float64)
    search = crop(img, np.array((-3, -off, 3, off)) + area, copy=False).astype(np.float64)
    ref = cv2.matchTemplate(templ.astype(np.uint8), search.astype(np.uint8),
                            cv2.TM_CCOEFF_NORMED).astype(np.float64)
    mine = my_ccoeff(search, templ)
    d = float(np.abs(mine - ref).max())
    rows.append((case['id'], templ.shape[0] * templ.shape[1], d,
                 min(search.shape[0] * search.shape[1] / max(templ.shape[0] * templ.shape[1], 1), 1e9)))

buckets = {}
for _id, area_px, d, ratio in rows:
    key = 'template>=2000px' if area_px >= 2000 else ('template>=1000px' if area_px >= 1000 else 'template<1000px')
    b = buckets.setdefault(key, {'n': 0, 'max_diff': 0.0, 'over_1e-5': 0, 'over_1e-4': 0})
    b['n'] += 1
    b['max_diff'] = max(b['max_diff'], d)
    b['over_1e-5'] += 1 if d > 1e-5 else 0
    b['over_1e-4'] += 1 if d > 1e-4 else 0

worst = sorted(rows, key=lambda r: -r[2])[:8]
print(json.dumps({
    'by_template_size': buckets,
    'worst_cases': [{'id': i, 'template_px': a, 'max_diff': d} for i, a, d, _ in worst],
    'note': 'OpenCV 在模板相对搜索区较大时会改用 DFT 相关，残差随之升到 ~1e-4 量级',
}, ensure_ascii=False, indent=2))
