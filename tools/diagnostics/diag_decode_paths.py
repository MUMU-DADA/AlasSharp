# -*- coding: utf-8 -*-
"""定位：文件路径(PIL) 与 字节路径(cv2.imdecode) 在同一 PNG 上的像素差异。"""
import json
import os
import sys

import cv2
import numpy as np

FORK = r"<developer-home>\source\ALAS fork project\csharp\.runtime\engine"
os.chdir(FORK)
sys.path.insert(0, FORK)
import module.device.pkg_resources  # noqa: F401

from module.base.utils import color_similarity, get_color, load_image

DATA = r"<developer-home>\source\ALAS fork project\csharp\data"
fx = json.load(open(os.path.join(DATA, 'fixtures', 'imaging.json'), encoding='utf-8'))

out = []
seen = set()
for case in fx['cases']:
    if len(out) >= 12:
        break
    rel = case['file'].replace('./', '').replace('/', os.sep)
    path = os.path.join(FORK, rel)
    if not os.path.isfile(path) or rel in seen:
        continue
    seen.add(rel)

    file_img = load_image(path)                                  # 上游素材路径（PIL）
    raw = open(path, 'rb').read()
    buf = np.frombuffer(raw, np.uint8)
    byte_img = cv2.imdecode(buf, cv2.IMREAD_COLOR)               # 上游截图路径
    if byte_img is None:
        out.append({'file': rel, 'error': 'imdecode 失败'})
        continue
    cv2.cvtColor(byte_img, cv2.COLOR_BGR2RGB, dst=byte_img)

    same = (file_img.shape == byte_img.shape
            and np.array_equal(file_img, byte_img))
    entry = {
        'file': rel,
        'mode': case['mode'],
        'file_shape': list(file_img.shape),
        'byte_shape': list(byte_img.shape),
        'identical': bool(same),
    }
    if not same and file_img.shape == byte_img.shape:
        d = np.abs(file_img.astype(int) - byte_img.astype(int))
        entry['max_pixel_diff'] = int(d.max())
        entry['diff_pixels'] = int((d.sum(axis=2) > 0).sum())
    if file_img.shape == byte_img.shape:
        area = case['area']
        c1 = [float(v) for v in get_color(file_img, area)]
        c2 = [float(v) for v in get_color(byte_img, area)]
        entry['color_file'] = [round(v, 3) for v in c1]
        entry['color_byte'] = [round(v, 3) for v in c2]
        entry['stored'] = case.get('stored_color')
        entry['sim_file'] = round(float(color_similarity(c1, case['stored_color'])), 3)
        entry['sim_byte'] = round(float(color_similarity(c2, case['stored_color'])), 3)
        entry['appear_file'] = bool(color_similarity(c1, case['stored_color']) <= 10)
        entry['appear_byte'] = bool(color_similarity(c2, case['stored_color']) <= 10)
    out.append(entry)

print(json.dumps(out, ensure_ascii=False, indent=1))
