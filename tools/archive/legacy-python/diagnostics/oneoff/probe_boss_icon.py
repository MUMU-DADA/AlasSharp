# -*- coding: utf-8 -*-
"""对一张**已存盘的画面**跑逐格预测 + BOSS 判据打分（离线，不碰游戏）。

    python tools/diagnostics/oneoff/probe_boss_icon.py data/_map_now3.png

为什么要离线跑：BOSS 在画面上的时机很短暂（清完小怪才刷），而 `View.load()` 只依赖
截图，不依赖游戏状态，所以**存一帧就能反复试判据**。
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..')))
import alas_vision as av          # noqa: E402


def op(name, **args):
    resp = json.loads(av.handle_line(json.dumps({'id': 1, 'op': name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(f'{name}: {resp.get("error")}')
    return resp['result']


def main():
    p = argparse.ArgumentParser()
    p.add_argument('image')
    p.add_argument('--icons', action='store_true')
    p.add_argument('--out-dir', default=None)
    args = p.parse_args()

    # 引擎在**导入期**就会 chdir 到引擎根目录，所以相对路径不能用 cwd 解析
    # （实测踩过：`data/x.png` 被解析成 `.runtime/engine/data/x.png`）。
    repo = os.path.normpath(os.path.join(HERE, '..', '..', '..'))
    img = args.image if os.path.isabs(args.image) else os.path.join(repo, args.image)
    img = os.path.abspath(img)
    print('load:', json.dumps(op('screenshot_load', path=img), ensure_ascii=False), flush=True)
    r = op('map_grids', icons=args.icons, out_dir=args.out_dir)
    print('shape:', r.get('shape'), 'grids:', r.get('grid_count'),
          'tpl:', r.get('tpl_shape'), 'area:', r.get('area'), flush=True)
    print('top upstream_red:', json.dumps(r.get('top_score_upstream_red'), ensure_ascii=False),
          flush=True)
    print('top blue_rbswap  :', json.dumps(r.get('top_score_blue_rbswap'), ensure_ascii=False),
          flush=True)
    print('top luma        :', json.dumps(r.get('top_score_luma'), ensure_ascii=False),
          flush=True)
    print('BOSS 格（垫片生效后 is_boss=True）:', json.dumps(r.get('boss_grids'),
                                                        ensure_ascii=False), flush=True)
    print('icons:', r.get('icons_path'), r.get('icons_ok'), flush=True)
    print('---- 逐格 ----', flush=True)
    for g in r.get('grids', []):
        print(json.dumps(g, ensure_ascii=False), flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
