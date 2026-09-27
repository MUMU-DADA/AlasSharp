# -*- coding: utf-8 -*-
"""把一帧画面里的 **BOSS 格**对齐到上游章节地图的全局坐标，验证"识别到了能不能落到 may_boss 格"。

为什么需要这一步：`GridPredictor.predict_boss()` 认出来只是**一半**。
真正落标志的是 `module/map_detection/grid_info.py:220-225`：

    if info.is_boss:
        if not self.is_land and self.may_boss:      # ← 只接受**声明为 MB 的格**
            self.is_boss = True
        else:
            return False

所以如果本客户端的 BOSS 位置在上游 `map_data` 里不是 `MB`，即使认出来了也会被丢掉，
`battle_6` 的 `if boss:` 依旧不成立。本脚本用**同一帧**离线穷举相机位置，
找出能让 `CampaignMap.update()` 自洽（上游自己会判 "Too many wrong prediction"）的那个，
再看 BOSS 落在哪一格、那格是不是 may_boss。

    python tools/diagnostics/oneoff/probe_boss_global.py data/_map_now3.png
"""
import argparse
import importlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..')))
import alas_vision as av          # noqa: E402  （装配 sys.path + chdir 到引擎根）

import numpy as np                # noqa: E402
from module.base.utils import load_image, location2node   # noqa: E402
from module.map_detection.view import View                # noqa: E402


def main():
    p = argparse.ArgumentParser()
    p.add_argument('image')
    p.add_argument('--chapter', default='campaign.campaign_main.campaign_11_1')
    args = p.parse_args()

    av.apply_numpy2_compat()
    av.apply_points_empty_compat()
    av.apply_boss_icon_color_compat()

    repo = os.path.normpath(os.path.join(HERE, '..', '..', '..'))
    path = args.image if os.path.isabs(args.image) else os.path.join(repo, args.image)
    image = load_image(path)
    cfg = av._map_config()

    v = View(cfg)
    v.load(image)
    v.predict()
    print(f'view shape={np.array(v.shape) + 1} center_loca={v.center_loca} '
          f'edges L/R/U/D={v.left_edge}/{v.right_edge}/{v.upper_edge}/{v.lower_edge}', flush=True)
    for loca, g in sorted(v.grids.items()):
        if g.is_boss or g.is_enemy:
            print(f'  view {location2node(loca)} = {g.str} '
                  f'(enemy={g.is_enemy} boss={g.is_boss} genre={g.enemy_genre})', flush=True)

    mod = importlib.import_module(args.chapter)
    MAP = mod.MAP
    MAP.load_map_data()
    MAP.load_spawn_data()
    print(f'map shape={np.array(MAP.shape) + 1} '
          f'may_boss={[location2node(g.location) for g in MAP.select(may_boss=True)]}', flush=True)

    results = []
    for cx in range(0, int(MAP.shape[0]) + 2):
        for cy in range(0, int(MAP.shape[1]) + 2):
            MAP.reset()
            try:
                ok = MAP.update(grids=v, camera=(cx, cy), mode='normal')
            except Exception as e:
                results.append(((cx, cy), False, f'{type(e).__name__}: {e}', []))
                continue
            boss = [location2node(g.location) for g in MAP.select(is_boss=True)]
            results.append(((cx, cy), bool(ok), '', boss))

    ok_list = [r for r in results if r[1]]
    print(f'\n自洽的相机位置（CampaignMap.update 返回 True）: '
          f'{[location2node(r[0]) for r in ok_list]}', flush=True)
    for cam, ok, err, boss in ok_list:
        print(f'  camera={location2node(cam)} -> is_boss 落在 {boss}', flush=True)
    if not ok_list:
        print('没有任何相机位置自洽 —— 说明这一帧与上游 map_data 对不上（不能用这一帧判断）',
              flush=True)
        for cam, ok, err, boss in results[:8]:
            print('   sample', location2node(cam), ok, err, boss, flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
