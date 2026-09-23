# -*- coding: utf-8 -*-
"""决定性检查：cv2 拿到的模板与 (3,30) 处的窗口是否逐像素相同。"""
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
case = next(c for c in fx['match'] if c['id'] == 'combat/READY_AIR_RAID')
path = os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep))
area = np.array(case['area'])
off = case['offset']

full = load_image(path)
template = load_image(path, case['area'])
search = crop(full, np.array((-3, -off, 3, off)) + area, copy=False)
th, tw = template.shape[:2]
window = search[30:30 + th, 3:3 + tw]

ref = cv2.matchTemplate(template, search, cv2.TM_CCOEFF_NORMED)
ref_at = float(ref[30, 3])

info = {
    'file': case['file'],
    'area': case['area'],
    'template_shape': list(template.shape),
    'search_shape': list(search.shape),
    'window_equals_template': bool(np.array_equal(window, template)),
    'template_equals_fullcrop': bool(np.array_equal(
        template, full[area[1]:area[3], area[0]:area[2]])),
    'template_var_per_channel': [round(float(v), 4) for v in
                                 template.reshape(-1, 3).var(0)],
    'window_var_per_channel': [round(float(v), 4) for v in
                               window.reshape(-1, 3).var(0)],
    'cv2_result_at_3_30': ref_at,
    'cv2_result_max': float(ref.max()),
    'cv2_max_loc': [int(v) for v in cv2.minMaxLoc(ref)[3]],
    'template_dtype': str(template.dtype),
    'search_dtype': str(search.dtype),
}
print(json.dumps(info, ensure_ascii=False, indent=2))

# 若窗口与模板相同，则 NCC 必为 1.0；用 cv2 直接对这两块做匹配来验证
try:
    self_match = cv2.matchTemplate(window, template, cv2.TM_CCOEFF_NORMED)
    info2 = {'cv2_self_match_value': float(self_match[0, 0]),
             'cv2_self_match_shape': list(self_match.shape)}
except Exception as e:
    info2 = {'cv2_self_match_error': f'{type(e).__name__}: {e}'}
print(json.dumps(info2, ensure_ascii=False, indent=2))
