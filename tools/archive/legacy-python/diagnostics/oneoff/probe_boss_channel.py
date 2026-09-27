# -*- coding: utf-8 -*-
"""在**引擎真正给上游的那张图**上，判定到底是"红色判据"还是"蓝色判据"认出 BOSS。

背景（含一个我自己踩过的坑）：保存的 PNG 是 `cv2.imwrite(path, E)` 写的，cv2 把数组当 BGR，
所以**文件里的颜色相对真实屏幕是 R/B 互换的**；而 `load_image()` 走 PIL、忠实读文件。
两者叠加，导致"直接拿文件做分析"会把红蓝看反。本脚本显式把文件色还原成引擎的 E
（`E = 文件色[:, :, ::-1]`），再在 E 上跑**上游原版** `predict_boss`，与垫片的蓝色判据对比。

    python tools/diagnostics/oneoff/probe_boss_channel.py data/_map_now3.png
"""
import argparse
import os
import sys

import cv2
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..')))
import alas_vision as av          # noqa: E402

from module.base.utils import load_image, color_similarity_2d, rgb2luma    # noqa: E402
from module.map_detection.view import View                                 # noqa: E402
import module.map_detection.grid_predictor as gp                           # noqa: E402


def main():
    p = argparse.ArgumentParser()
    p.add_argument('image')
    args = p.parse_args()
    repo = os.path.normpath(os.path.join(HERE, '..', '..', '..'))
    path = args.image if os.path.isabs(args.image) else os.path.join(repo, args.image)

    file_img = load_image(path)                  # 文件色（相对真实屏幕 R/B 互换）
    engine_img = np.ascontiguousarray(file_img[:, :, ::-1])   # 引擎真正给上游的图
    print(f'文件色按钮区均值(RGB) = {file_img[300:340, 585:700].reshape(-1, 3).mean(axis=0).round(1)}',
          flush=True)
    print(f'还原后 E 同区域均值(RGB) = '
          f'{engine_img[300:340, 585:700].reshape(-1, 3).mean(axis=0).round(1)}', flush=True)

    av.apply_numpy2_compat()
    av.apply_points_empty_compat()
    cfg = av._map_config()

    tpl = gp.TEMPLATE_ENEMY_BOSS
    tpl_img = tpl.image
    tpl_gray = cv2.cvtColor(tpl_img, cv2.COLOR_RGB2GRAY) if tpl_img.ndim == 3 else tpl_img
    area = (-0.55, -0.2, 0.45, 0.2)

    def score(sim):
        if sim.ndim == 3:
            sim = cv2.cvtColor(sim, cv2.COLOR_RGB2GRAY)
        res = cv2.matchTemplate(sim.astype(np.uint8), tpl_gray, cv2.TM_CCOEFF_NORMED)
        return float(cv2.minMaxLoc(res)[1])

    for tag, img in (('文件色', file_img), ('引擎 E', engine_img)):
        v = View(cfg)
        v.load(np.array(img))
        v.predict()                              # 上游原版 predict_boss（本进程未装垫片）
        bosses = [(tuple(int(x) for x in g.location), str(g.str)) for g in v.grids.values()
                  if g.is_boss]
        scored = []
        for loca, g in v.grids.items():
            crop = g.relative_crop(area, shape=(50, 20))
            scored.append((tuple(int(x) for x in loca),
                           round(score(color_similarity_2d(crop, (255, 77, 82))), 3),
                           round(score(color_similarity_2d(crop, (82, 77, 255))), 3)))
        scored.sort(key=lambda x: -max(x[1], x[2]))
        print(f'\n=== {tag} ===  上游原版 predict_boss 认出的 BOSS 格: {bosses}', flush=True)
        print('  前 3 名（格, 红判据, 蓝判据）:', flush=True)
        for row in scored[:3]:
            print('   ', row, flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
