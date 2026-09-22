# -*- coding: utf-8 -*-
"""分别确认三条转换路径的定点精度：BGR2GRAY / RGB2GRAY / RGB2YUV 的 Y。"""
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

# 14 位（经典）与 15 位两套系数，按位置区分 RGB / BGR
F14 = {'R': 4899, 'G': 9617, 'B': 1868, 'shift': 14, 'half': 1 << 13}
F15 = {'R': 9798, 'G': 19235, 'B': 3735, 'shift': 15, 'half': 1 << 14}


def conv(c0, c1, c2, order, f):
    """order='RGB' 表示 c0/c1/c2 依次是 R/G/B。"""
    w = {'R': f['R'], 'G': f['G'], 'B': f['B']}
    v = {'R': c0, 'G': c1, 'B': c2} if order == 'RGB' else {'B': c0, 'G': c1, 'R': c2}
    return (v['R'] * 0 + v['R'] * w['R'] + v['G'] * w['G'] + v['B'] * w['B'] + f['half']) >> f['shift']


results = {}
for path_name, cv_code, order in (
        ('BGR2GRAY', cv2.COLOR_BGR2GRAY, 'BGR'),
        ('RGB2GRAY', cv2.COLOR_RGB2GRAY, 'RGB')):
    for fname, f in (('14bit', F14), ('15bit', F15)):
        total = 0
        for case in fx['gray']:
            img = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)))
            region = crop(img, case['rect'], copy=True)
            if region.ndim != 3:
                continue
            truth = cv2.cvtColor(region, cv_code).astype(np.int64)
            got = conv(region[:, :, 0].astype(np.int64), region[:, :, 1].astype(np.int64),
                       region[:, :, 2].astype(np.int64), order, f).astype(np.int64)
            total += int(np.abs(got - truth).sum())
        results[f'{path_name}_{fname}'] = total

# RGB2YUV 的 Y
for fname, f in (('14bit', F14), ('15bit', F15)):
    total = 0
    for case in fx['gray']:
        img = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)))
        region = crop(img, case['rect'], copy=True)
        if region.ndim != 3:
            continue
        truth = cv2.cvtColor(region, cv2.COLOR_RGB2YUV)[:, :, 0].astype(np.int64)
        got = conv(region[:, :, 0].astype(np.int64), region[:, :, 1].astype(np.int64),
                   region[:, :, 2].astype(np.int64), 'RGB', f).astype(np.int64)
        total += int(np.abs(got - truth).sum())
    results[f'RGB2YUV_Y_{fname}'] = total

print(json.dumps({'total_abs_diff': results,
                  'note': '0 = 该路径在该精度下与 OpenCV 逐像素一致'}, ensure_ascii=False, indent=2))
