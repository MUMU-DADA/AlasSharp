# -*- coding: utf-8 -*-
"""定点定位两个不一致：BGR2GRAY 的取整方式、TM_CCOEFF_NORMED 的公式差异。

方法：拿基准里失败的真实样本，在 numpy 里逐一试候选公式，看哪个与 cv2 逐像素相等。
"""
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

DATA = r"<developer-home>\source\ALAS fork project\csharp\data"
fx = json.load(open(os.path.join(DATA, 'fixtures', 'matching.json'), encoding='utf-8'))


def load(rel):
    return load_image(os.path.join(FORK, rel.replace('./', '').replace('/', os.sep)))


print('=' * 70)
print('一、BGR2GRAY 的取整方式')
print('=' * 70)
R2Y, G2Y, B2Y, SHIFT = 4899, 9617, 1868, 14


def _f32(x):
    return x.astype(np.float32)


candidates = {
    'fixed14_round': lambda c0, c1, c2: (c0 * B2Y + c1 * G2Y + c2 * R2Y + (1 << 13)) >> SHIFT,
    'fixed14_trunc': lambda c0, c1, c2: (c0 * B2Y + c1 * G2Y + c2 * R2Y) >> SHIFT,
    'fixed15_round': lambda c0, c1, c2: (c0 * 3735 + c1 * 19235 + c2 * 9798 + (1 << 14)) >> 15,
    'fixed16_round': lambda c0, c1, c2: (c0 * 7471 + c1 * 38470 + c2 * 19595 + (1 << 15)) >> 16,
    'float64_rint': lambda c0, c1, c2: np.rint(0.114 * c0 + 0.587 * c1 + 0.299 * c2),
    'float64_floor_half_up': lambda c0, c1, c2: np.floor(0.114 * c0 + 0.587 * c1 + 0.299 * c2 + 0.5),
    'float32_rint': lambda c0, c1, c2: np.rint((0.114 * _f32(c0) + 0.587 * _f32(c1) + 0.299 * _f32(c2))),
    'float32_round_half_up': lambda c0, c1, c2: np.floor((0.114 * _f32(c0) + 0.587 * _f32(c1) + 0.299 * _f32(c2)) + 0.5),
    'intweights_114_587_299': lambda c0, c1, c2: (c0 * 114 + c1 * 587 + c2 * 299 + 500) // 1000,
}

score = {k: 0 for k in candidates}
fails = []
for case in fx['gray']:
    img = load(case['file'])
    region = crop(img, case['rect'], copy=True)
    if region.ndim != 3:
        continue
    truth = cv2.cvtColor(region, cv2.COLOR_BGR2GRAY).astype(np.int64)
    c0 = region[:, :, 0].astype(np.int64)
    c1 = region[:, :, 1].astype(np.int64)
    c2 = region[:, :, 2].astype(np.int64)
    for name, fn in candidates.items():
        got = np.asarray(fn(c0, c1, c2)).astype(np.int64)
        diff = int(np.abs(got - truth).sum())
        score[name] += diff
        if diff and name == 'fixed_round(+1<<13)>>14' and len(fails) < 3:
            n = int((got != truth).sum())
            idx = np.argwhere(got != truth)[:3]
            fails.append({'case': case['id'], 'diff_pixels': n, 'sum_diff': diff,
                          'examples': [{'pos': list(map(int, p)), 'cv2': int(truth[tuple(p)]),
                                        'mine': int(got[tuple(p)])} for p in idx]})

print(json.dumps({'total_abs_diff_by_formula': score, 'fixed_round_examples': fails},
                 ensure_ascii=False, indent=2))

print()
print('=' * 70)
print('二、TM_CCOEFF_NORMED：我的公式 vs cv2 的参数顺序')
print('=' * 70)


def my_ccoeff_normed(image, templ):
    """与 C# 实现相同的公式，含 OpenCV 的「templ 更大则交换」行为。"""
    if templ.shape[0] > image.shape[0] or templ.shape[1] > image.shape[1]:
        image, templ = templ, image
    ih, iw = image.shape[:2]
    th, tw = templ.shape[:2]
    rh, rw = ih - th + 1, iw - tw + 1
    if rh <= 0 or rw <= 0:
        raise ValueError(f'模板 {templ.shape} 大于图像 {image.shape}')
    I = image.astype(np.float64)
    T = templ.astype(np.float64)
    n = th * tw * I.shape[2]
    sumT, sumT2 = T.sum(), (T * T).sum()
    denomT = sumT2 - sumT * sumT / n

    cs = I.sum(axis=2)
    cs2 = (I * I).sum(axis=2)
    ii = np.zeros((ih + 1, iw + 1))
    ii2 = np.zeros((ih + 1, iw + 1))
    ii[1:, 1:] = cs.cumsum(0).cumsum(1)
    ii2[1:, 1:] = cs2.cumsum(0).cumsum(1)

    out = np.zeros((rh, rw), dtype=np.float64)
    for y in range(rh):
        for x in range(rw):
            s = ii[y + th, x + tw] - ii[y, x + tw] - ii[y + th, x] + ii[y, x]
            s2 = ii2[y + th, x + tw] - ii2[y, x + tw] - ii2[y + th, x] + ii2[y, x]
            cross = float((T * I[y:y + th, x:x + tw]).sum())
            num = cross - sumT * s / n
            denomI = s2 - s * s / n
            denom = denomT * denomI
            out[y, x] = num / np.sqrt(denom) if denom > 0 else 0.0
    return out


compared = 0
report = []
for case in fx['match'][:4]:
    img = load(case['file'])
    area = np.array(case['area'])
    offset = case['offset']
    template = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)),
                          case['area'])
    search = crop(img, np.array((-3, -offset, 3, offset)) + area, copy=False)
    ref = cv2.matchTemplate(template, search, cv2.TM_CCOEFF_NORMED)
    ref_swapped = cv2.matchTemplate(search, template, cv2.TM_CCOEFF_NORMED)
    try:
        mine = my_ccoeff_normed(template, search)
    except Exception as e:
        report.append({'case': case['id'], 'error': f'{type(e).__name__}: {e}'})
        continue
    compared += 1
    d = np.abs(mine - ref)
    report.append({
        'case': case['id'],
        'template_shape': list(template.shape), 'search_shape': list(search.shape),
        'ref_shape': list(ref.shape), 'mine_shape': list(mine.shape),
        'ref_equals_swapped': bool(np.array_equal(ref, ref_swapped)),
        'maxdiff': float(d.max()), 'meandiff': float(d.mean()),
        'ref_max': float(ref.max()), 'mine_max': float(mine.max()),
        'ref_min': float(ref.min()), 'mine_min': float(mine.min()),
        'ref_sum': float(ref.astype(np.float64).sum()),
        'mine_sum': float(mine.sum()),
        'worst_pos': [int(v) for v in np.unravel_index(int(d.argmax()), d.shape)],
        'worst_ref': float(ref.ravel()[int(d.argmax())]),
        'worst_mine': float(mine.ravel()[int(d.argmax())]),
    })
print(json.dumps({'compared': compared, 'report': report}, ensure_ascii=False, indent=2))
