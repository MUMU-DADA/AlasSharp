# -*- coding: utf-8 -*-
"""验证：cv2 是否因「模板/搜索区尺寸比」切换相关算法，导致同一位置的分数不同。

做法：把搜索区用常量边加宽，使模板相对变小。数学上内部位置的 NCC 不变，
若 cv2 的分数因此改变，即证明它切换了算法路径。
"""
import json
import os
import sys

import cv2
import numpy as np

FORK = r"<developer-home>\source\ALAS fork project\csharp\.runtime\engine"
os.chdir(FORK)
sys.path.insert(0, FORK)
import module.device.pkg_resources  # noqa: F401

from module.base.utils import crop, load_image

fx = json.load(open(r"<developer-home>\source\ALAS fork project\csharp\data\fixtures\matching.json",
                    encoding='utf-8'))
case = next(c for c in fx['match'] if c['id'] == 'combat/READY_AIR_RAID')
path = os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep))
area = np.array(case['area'])
off = case['offset']

full = load_image(path)
template = load_image(path, case['area'])
search = crop(full, np.array((-3, -off, 3, off)) + area, copy=False)
th, tw = template.shape[:2]

variants = {}
for label, pad_b, pad_r in (('原始 70x26', 0, 0), ('+70/+80 边', 70, 80), ('+300/+300 边', 300, 300)):
    s = search if (pad_b == 0 and pad_r == 0) else cv2.copyMakeBorder(
        search, 0, pad_b, 0, pad_r, cv2.BORDER_CONSTANT, value=(0, 0, 0))
    r = cv2.matchTemplate(template, s, cv2.TM_CCOEFF_NORMED)
    variants[label] = {
        'search_shape': list(s.shape),
        'result_shape': list(r.shape),
        'value_at_3_30': round(float(r[30, 3]), 6),
        'max': round(float(r.max()), 6),
        'max_loc': [int(v) for v in cv2.minMaxLoc(r)[3]],
        'template_over_search_area_ratio': round(th * tw / (s.shape[0] * s.shape[1]), 4),
    }

# 结论性判据：同一块内容、同一位置，分数是否随搜索区尺寸变化
vals = [v['value_at_3_30'] for v in variants.values()]
print(json.dumps({
    'variants': variants,
    'value_changes_with_padding': len({round(v, 6) for v in vals}) > 1,
    'note': 'NCC 是逐位置局部量，加常量边不应改变内部位置的分数；若改变则说明 cv2 换了算法路径',
}, ensure_ascii=False, indent=2))
