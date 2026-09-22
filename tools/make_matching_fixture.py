#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
S1b 对拍基准：灰度化 / 亮度 / OTSU / 模板匹配。真值全部由 OpenCV 现算。

为什么真值必须用 cv2 现算而不是"按公式推"：
    上游的调用形态本身有反直觉之处，必须原样复现：
      1) match_binary / match_luma 在 **RGB** 数据上调用 cv2.cvtColor(..., COLOR_BGR2GRAY)
         —— 通道权重错位，但这是上游既有行为；
      2) Button.match 写的是 cv2.matchTemplate(self.image, image) —— **模板在前、搜索区在后**，
         依赖 OpenCV 在 templ > image 时自动交换；
      3) 结果矩阵是 CV_32F，内部 double 计算后落回 float。
    这些都不是"按数学公式实现"能保证一致的，只能对拍。

用法：
    python make_matching_fixture.py [--match-cases N] [--gif-cases N]
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from collections import Counter

FORK_DEFAULT = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '..', '..',
    'my fork project', 'AzurLaneAutoScript'))

sys.path.insert(0, FORK_DEFAULT)
os.chdir(FORK_DEFAULT)
import module.device.pkg_resources  # noqa: F401

import cv2
import imageio
import numpy as np

from module.base.utils import crop, load_image

FIXTURE_VERSION = '1.0.0'
SAMPLES = 6


def sample_values(arr, count=SAMPLES):
    """取固定位置的样本值（行优先均匀取样），用于诊断而非全量比对。"""
    flat = arr.reshape(-1)
    if flat.size == 0:
        return []
    idx = [int(i * (flat.size - 1) / max(count - 1, 1)) for i in range(count)]
    return [int(flat[i]) for i in idx]


def gray_cases(assets, limit):
    """灰度化 / 亮度 / OTSU 三种原语的真值。"""
    cases = []
    for aid in sorted(assets):
        a = assets[aid]
        areas = a.get('area') or {}
        files = a.get('file') or {}
        for server in sorted(files):
            area = areas.get(server)
            if area is None:
                continue
            rel = files[server]
            if not rel.lower().endswith('.png'):
                continue
            path = os.path.join(FORK_DEFAULT, rel.replace('./', '').replace('/', os.sep))
            if not os.path.isfile(path):
                continue
            image = load_image(path)                     # RGB，上游约定
            region = crop(image, area, copy=True)
            if region.ndim != 3 or region.shape[2] != 3:
                continue                                  # 单通道图上游会在 cvtColor 抛错，跳过

            gray = cv2.cvtColor(region, cv2.COLOR_BGR2GRAY)     # 注意：上游就是这么写的
            gray_rgb = cv2.cvtColor(region, cv2.COLOR_RGB2GRAY)  # get_bbox 用的是这条
            yuv = cv2.cvtColor(region, cv2.COLOR_RGB2YUV)
            luma = yuv[:, :, 0]
            otsu_val, binary = cv2.threshold(gray, 0, 255,
                                             cv2.THRESH_BINARY | cv2.THRESH_OTSU)
            cases.append({
                'id': aid,
                'server': server,
                'file': rel,
                'rect': list(area),
                'rgb_shape': list(region.shape),
                'gray_sum': int(gray.sum()),
                'gray_samples': sample_values(gray),
                'gray_rgb_sum': int(gray_rgb.sum()),
                'gray_rgb_samples': sample_values(gray_rgb),
                'luma_sum': int(luma.sum()),
                'luma_samples': sample_values(luma),
                'otsu': int(otsu_val),
                'binary_sum': int(binary.sum()),
            })
            if len(cases) >= limit:
                return cases
    return cases


def match_cases(assets, limit):
    """
    复现 Button.match(image, offset) 的真实调用形态：
        template = load_image(file, area)              # PIL 裁剪
        search   = crop(load_image(file), offset + area)  # numpy 裁剪（含补边）
        res      = cv2.matchTemplate(template, search, TM_CCOEFF_NORMED)   # 模板在前！
    """
    cases = []
    for aid in sorted(assets):
        a = assets[aid]
        if a['kind'] != 'Button':
            continue
        areas = a.get('area') or {}
        files = a.get('file') or {}
        for server in ('cn',):
            area = areas.get(server)
            rel = files.get(server)
            if area is None or not rel or not rel.lower().endswith('.png'):
                continue
            path = os.path.join(FORK_DEFAULT, rel.replace('./', '').replace('/', os.sep))
            if not os.path.isfile(path):
                continue
            image = load_image(path)
            area_arr = np.array(area)
            for offset in (30,):
                offset_arr = np.array((-3, -offset, 3, offset))
                template = load_image(path, area)                       # PIL
                search = crop(image, offset_arr + area_arr, copy=False)  # numpy
                if template.ndim != 3 or search.ndim != 3:
                    continue
                res = cv2.matchTemplate(template, search, cv2.TM_CCOEFF_NORMED)
                min_val, max_val, min_loc, max_loc = cv2.minMaxLoc(res)
                entry = {
                    'id': aid,
                    'server': server,
                    'file': rel,
                    'area': list(area),
                    'offset': offset,
                    'arg_order': 'template_first',
                    'template_shape': list(template.shape),
                    'search_shape': list(search.shape),
                    'result_shape': list(res.shape),
                    'min': float(min_val),
                    'max': float(max_val),
                    'min_loc': list(min_loc),
                    'max_loc': list(max_loc),
                    'sum': float(res.astype(np.float64).sum()),
                }
                if res.size <= 2000:
                    entry['matrix'] = [float(v) for v in res.reshape(-1)]
                    entry['matrix_dtype'] = 'float32'
                cases.append(entry)
                break
        if len(cases) >= limit:
            break
    return cases


def gif_cases(assets, limit):
    """
    GIF 模板语义：imageio 多帧 + 上游的「跟随首帧」通道规则 + 每帧追加水平镜像。
    """
    cases, seen = [], set()
    for aid in sorted(assets):
        a = assets[aid]
        for server in ('cn',):
            rel = (a.get('file') or {}).get(server)
            if not rel or not rel.lower().endswith('.gif'):
                continue
            if rel in seen:
                continue
            path = os.path.join(FORK_DEFAULT, rel.replace('./', '').replace('/', os.sep))
            if not os.path.isfile(path):
                continue
            seen.add(rel)
            frames = imageio.mimread(path)
            channel = 0
            processed = []
            for image in frames:
                if not channel:
                    channel = len(image.shape)
                if channel == 3:
                    image = image[:, :, :3].copy()
                elif len(image.shape) == 3:
                    image = image[:, :, 0].copy()
                processed.append(image)
            cases.append({
                'id': aid,
                'file': rel,
                'frame_count': len(frames),
                'first_frame_ndim': len(frames[0].shape),
                'channel_mode': channel,
                'frames': [{'shape': list(f.shape), 'sum': int(f.sum()),
                            'samples': sample_values(f)} for f in processed],
                'template_count': len(processed) * 2,   # 每帧再追加一份水平镜像
            })
            if len(cases) >= limit:
                return cases
    return cases


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--data', default=os.path.normpath(
        os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'data')))
    ap.add_argument('--out', default=None)
    ap.add_argument('--gray-cases', type=int, default=400)
    ap.add_argument('--match-cases', type=int, default=400)
    ap.add_argument('--gif-cases', type=int, default=40)
    args = ap.parse_args()

    out = args.out or os.path.join(args.data, 'fixtures', 'matching.json')
    os.makedirs(os.path.dirname(out), exist_ok=True)
    assets = json.load(open(os.path.join(args.data, 'assets.json'), encoding='utf-8'))['assets']

    fixture = {
        'version': FIXTURE_VERSION,
        'note': '真值由 OpenCV 现算，逐条复现上游的调用形态（含通道权重错位与参数顺序）',
        'gray': gray_cases(assets, args.gray_cases),
        'match': match_cases(assets, args.match_cases),
        'gif': gif_cases(assets, args.gif_cases),
    }
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(fixture, f, ensure_ascii=False, indent=1, sort_keys=True)
        f.write('\n')

    print(json.dumps({
        'out': out,
        'gray_cases': len(fixture['gray']),
        'match_cases': len(fixture['match']),
        'match_with_matrix': sum(1 for c in fixture['match'] if 'matrix' in c),
        'match_result_shapes': {str(k): v for k, v in
                                Counter(tuple(c['result_shape'])
                                        for c in fixture['match']).most_common(5)},
        'gif_cases': len(fixture['gif']),
        'gif_frame_ndim': dict(Counter(c['first_frame_ndim'] for c in fixture['gif'])),
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == '__main__':
    sys.exit(main())
