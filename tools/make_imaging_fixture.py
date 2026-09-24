#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
S1 图像原语对拍基准（真值由 ALAS 自己的函数产出）。

为什么真值必须用上游函数算：
    这是「移植是否等价」的判据，不是「谁算得快」。基准里直接调用
    module.base.utils 的 load_image / crop / get_color / color_similar，
    保证真值就是 ALAS 运行时真正会得到的值。

覆盖：
  1. 全部素材 × 全部服务器（PNG）：解码 → 取 area 均值 → 与存储色比 → appear 判定
  2. 合成裁剪用例：越界补黑边、完全越界、以及**银行家舍入**的小数坐标
     （上游 crop() 用 Python round()，C# 的 Math.Round 默认是 AwayFromZero，这里专门验它）

GIF 不在本次覆盖内（968 个，多帧语义需单独验证），会统计并标注。

用法：
    python make_imaging_fixture.py [--limit N] [--out path]
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from collections import Counter

from fixture_runtime import initialize, invocation_path
FORK_DEFAULT = initialize()
import module.device.pkg_resources  # noqa: F401  桩需先导入（见 AGENTS.local.md）

import numpy as np
from PIL import Image

from module.base.utils import color_similar, color_similarity, crop, get_color, load_image

FIXTURE_VERSION = '1.0.0'


def build_asset_cases(fork: str, data_dir: str, limit: int | None):
    assets = json.load(open(os.path.join(data_dir, 'assets.json'), encoding='utf-8'))['assets']
    cases, stats = [], Counter()

    for aid in sorted(assets):
        a = assets[aid]
        files = a.get('file') or {}
        areas = a.get('area') or {}
        colors = a.get('color') or {}
        for server in sorted(files):
            rel = files[server]
            if not rel.lower().endswith('.png'):
                stats['skipped_non_png'] += 1
                continue
            path = os.path.join(fork, rel.replace('./', '').replace('/', os.sep))
            if not os.path.isfile(path):
                stats['missing_file'] += 1
                continue

            with Image.open(path) as im:
                mode, size = im.mode, im.size

            image = load_image(path)                     # 上游函数，PIL → ndarray
            area = areas.get(server)
            if area is None:
                # Template 没有 area：上游 Template.match 是整图对整屏匹配
                area = [0, 0, size[0], size[1]]
                stats['whole_image_area'] += 1

            mean = get_color(image, area)                # cv2.mean(...)[:3]
            entry = {
                'id': aid,
                'server': server,
                'file': rel,
                'kind': a['kind'],
                'mode': mode,
                'image_shape': list(image.shape),
                'area': list(area),
                'color': [float(v) for v in mean],
            }
            stored = colors.get(server)
            if stored is not None:
                entry['stored_color'] = [float(v) for v in stored]
                entry['similarity'] = float(color_similarity(mean, stored))
                entry['appear_default'] = bool(color_similar(mean, stored, threshold=10))
                entry['appear_30'] = bool(color_similar(mean, stored, threshold=30))
                stats['with_stored_color'] += 1
            cases.append(entry)
            stats['cases'] += 1
            if limit and len(cases) >= limit:
                return cases, stats
    return cases, stats


def build_synthetic_cases(fork: str, out_dir: str):
    """
    裁剪边界用例。合成图写成 PNG 供 C# 读取，真值由上游 crop()/get_color() 产出。

    重点覆盖 crop() 的三个分支：
      - 完全在图内（含负坐标被夹到 0）
      - 部分越界 → 零填充黑边
      - 完全越界 → 返回全零图，尺寸 = round 后的 (x2-x1, y2-y1)
    以及银行家舍入：Python round(0.5)=0、round(1.5)=2、round(2.5)=2。
    """
    syn_dir = os.path.join(out_dir, 'synthetic')
    os.makedirs(syn_dir, exist_ok=True)

    # 用固定梯度造两张图，避免随机导致不可复现
    h, w = 37, 53
    rgb = np.zeros((h, w, 3), dtype=np.uint8)
    for y in range(h):
        for x in range(w):
            rgb[y, x] = ((x * 5) % 256, (y * 7) % 256, ((x + y) * 3) % 256)
    gray = np.zeros((h, w), dtype=np.uint8)
    for y in range(h):
        for x in range(w):
            gray[y, x] = (x * 11 + y * 13) % 256

    files = {}
    for name, arr in (('rgb', rgb), ('gray', gray)):
        p = os.path.join(syn_dir, f'{name}.png')
        Image.fromarray(arr).save(p)
        files[name] = p

    cases = []
    areas = [
        ('inside', (0, 0, 10, 10)),
        ('inside_full', (0, 0, w, h)),
        ('negative_clip', (-5, -5, 10, 10)),
        ('partial_right_bottom', (w - 5, h - 5, w + 7, h + 9)),
        ('partial_all_sides', (-4, -6, w + 3, h + 2)),
        ('fully_outside_right', (w + 3, 0, w + 10, 5)),
        ('fully_outside_left', (-20, 0, -5, 5)),
        ('banker_half_to_even', (0.5, 0.5, 10.5, 10.5)),
        ('banker_odd', (1.5, 2.5, 11.5, 12.5)),
        ('banker_234', (2.5, 3.5, 4.5, 5.5)),
        ('single_pixel', (3, 4, 4, 5)),
        ('reversed', (10, 10, 5, 5)),
    ]

    for shape_name, arr in (('rgb', rgb), ('gray', gray)):
        for case_name, area in areas:
            # 记录上游**抛错**的用例同样有价值：它划出了「移植可以更稳健」的边界。
            # 实测 reversed（x2<x1）会让上游 crop() 的 copy_image → cv2.copyTo 抛 cv2.error。
            try:
                region = crop(arr, area, copy=True)
                mean = get_color(arr, area)
                cases.append({
                    'image': f'synthetic/{shape_name}.png',
                    'case': case_name,
                    'area': list(area),
                    'region_shape': list(region.shape),
                    'region_sum': int(region.sum()),
                    'color': [float(v) for v in mean],
                })
            except Exception as e:
                cases.append({
                    'image': f'synthetic/{shape_name}.png',
                    'case': case_name,
                    'area': list(area),
                    'upstream_error': f'{type(e).__name__}: {e}'.splitlines()[0],
                })
    return cases


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--repo', default=FORK_DEFAULT)
    ap.add_argument('--data', default=os.path.normpath(
        os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'data')))
    ap.add_argument('--out', default=None)
    ap.add_argument('--limit', type=int, default=None)
    args = ap.parse_args()
    args.data = invocation_path(args.data)
    args.out = invocation_path(args.out) if args.out else None
    # Imports and asset paths must use the same normalized source.
    args.repo = FORK_DEFAULT

    out = args.out or os.path.join(args.data, 'fixtures', 'imaging.json')
    os.makedirs(os.path.dirname(out), exist_ok=True)

    cases, stats = build_asset_cases(args.repo, args.data, args.limit)
    synthetic = build_synthetic_cases(args.repo, os.path.dirname(out))

    fixture = {
        'version': FIXTURE_VERSION,
        'note': '真值由 ALAS 自身的 load_image/crop/get_color/color_similar 产出',
        'cases': cases,
        'synthetic': synthetic,
        'stats': dict(stats),
    }
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(fixture, f, ensure_ascii=False, indent=1, sort_keys=True)
        f.write('\n')

    modes = Counter(c['mode'] for c in cases)
    shapes = Counter(tuple(c['image_shape']) for c in cases)
    print(json.dumps({
        'out': out,
        'cases': len(cases),
        'synthetic_cases': len(synthetic),
        'with_stored_color': stats['with_stored_color'],
        'skipped_non_png': stats['skipped_non_png'],
        'modes': dict(modes),
        'image_ndim': dict(Counter(len(s) for s in shapes)),
        'appear_true': sum(1 for c in cases if c.get('appear_default')),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == '__main__':
    sys.exit(main())
