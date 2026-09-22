# -*- coding: utf-8 -*-
"""探查素材图片的解码特征：PIL 模式、通道数、尺寸。

对拍前必须先确认这点：ALAS 的 load_image 走 PIL，`np.array(Image.open(f))` 对
调色板图（mode='P'）给出的是**调色板索引**而不是 RGB 值，通道数也会变。
若素材里存在非 RGB/RGBA 模式，C# 侧的解码就必须复现同样的语义。
"""
import json
import os
import sys
from collections import Counter

from PIL import Image

FORK = r"<developer-home>\source\ALAS fork project\csharp\.runtime\engine"
assets = json.load(open(r"<developer-home>\source\ALAS fork project\csharp\data\assets.json",
                        encoding='utf-8'))['assets']

modes = Counter()
dims = Counter()
by_ext = Counter()
samples = {}
problems = []

for aid, a in assets.items():
    for server, rel in (a.get('file') or {}).items():
        p = os.path.join(FORK, rel.replace('./', '').replace('/', os.sep))
        if not os.path.isfile(p):
            problems.append({'asset': aid, 'server': server, 'issue': 'missing'})
            continue
        ext = os.path.splitext(p)[1].lower()
        by_ext[ext] += 1
        if ext == '.gif':
            continue
        try:
            with Image.open(p) as im:
                modes[im.mode] += 1
                dims[im.size] += 1
                if im.mode not in samples:
                    samples[im.mode] = os.path.relpath(p, FORK)
        except Exception as e:
            problems.append({'asset': aid, 'server': server, 'issue': f'{type(e).__name__}: {e}'})

print(json.dumps({
    'png_modes': dict(modes.most_common()),
    'by_extension': dict(by_ext),
    'top_dimensions': [{'size': list(k), 'count': v} for k, v in dims.most_common(6)],
    'mode_samples': samples,
    'problems': problems[:10],
    'problem_count': len(problems),
}, ensure_ascii=False, indent=2))
