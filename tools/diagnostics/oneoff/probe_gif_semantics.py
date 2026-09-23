# -*- coding: utf-8 -*-
"""GIF 素材特征探查：为 S1b（模板匹配）准备多帧语义。

ALAS 对 GIF 模板的处理（module/base/template.py: Template.image）：
    for image in imageio.mimread(file):
        if channel == 3: image = image[:, :, :3]
        elif len(image.shape) == 3: image = image[:, :, 0]   # 跟随第一帧的通道语义
        image = self.pre_process(image)
        self._image += [image, cv2.flip(image, 1)]           # 每帧再追加一份水平镜像
即：帧数 × 2 个模板，且帧间可能尺寸不同。C# 侧必须复现这套语义。
"""
import json
import os
import sys
from collections import Counter

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                     '..', '..', '..'))
FORK = os.path.join(ROOT, '.runtime', 'engine')
os.chdir(FORK)
sys.path.insert(0, FORK)
import module.device.pkg_resources  # noqa: F401

import cv2
import imageio
import numpy as np

assets = json.load(open(os.path.join(ROOT, 'data', 'assets.json'),
                        encoding='utf-8'))['assets']

frames_hist = Counter()
shape_hist = Counter()
ndim_hist = Counter()
varying = []
errors = []
checked = 0
gif_paths = {}

for aid, a in assets.items():
    for server, rel in (a.get('file') or {}).items():
        if not rel.lower().endswith('.gif'):
            continue
        p = os.path.join(FORK, rel.replace('./', '').replace('/', os.sep))
        if not os.path.isfile(p):
            errors.append({'asset': aid, 'server': server, 'error': 'missing'})
            continue
        if p in gif_paths:
            continue
        try:
            frames = imageio.mimread(p)
        except Exception as e:
            errors.append({'asset': aid, 'server': server, 'error': f'{type(e).__name__}: {e}'})
            continue
        gif_paths[p] = True
        checked += 1
        frames_hist[len(frames)] += 1
        shapes = {f.shape for f in frames}
        ndim_hist[len(frames[0].shape)] += 1
        for s in shapes:
            shape_hist[s] += 1
        if len(shapes) > 1:
            varying.append({'file': os.path.relpath(p, FORK), 'shapes': sorted(map(list, shapes))})

print(json.dumps({
    'gif_files_checked': checked,
    'frame_count_histogram': dict(sorted(frames_hist.items())),
    'first_frame_ndim': dict(ndim_hist),
    'distinct_frame_shapes': [{'shape': list(k), 'count': v} for k, v in shape_hist.most_common(8)],
    'frames_varying_within_file': len(varying),
    'varying_sample': varying[:5],
    'errors': errors[:5],
    'error_count': len(errors),
}, ensure_ascii=False, indent=2))
