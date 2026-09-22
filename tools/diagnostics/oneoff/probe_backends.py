# -*- coding: utf-8 -*-
"""离线对比两个检测后端（homography / perspective）在**已存盘的现场帧**上的结果。

动机：≤3 行的图（1-1 / 1-2 / 1-4 / 7-1 / 8-1）一直报
`No vertical line detected` / `Vanish point and distant point too close`，
而 ≥4 行的图都能识别。降阈值、降峰参数、放大都试过无效（见 docs/map-detection.md）——
但**上游有两个检测后端**，此前只用 homography 试过。这个脚本把两套后端在同一批帧上并排跑，
用"能识别的图（2-2/3-1/3-2）"当对照组，避免把"后端换对了"误判成"参数调对了"。

    python tools/diagnostics/oneoff/probe_backends.py
    python tools/diagnostics/oneoff/probe_backends.py --frames data/fixtures/inmap_7-1.png
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..')))
import alas_vision as av          # noqa: E402

REPO = os.path.normpath(os.path.join(HERE, '..', '..', '..'))
# 前四个是**已知识别不了**的（≤3 行），后三个是对照组（≥4 行，能识别）
DEFAULT_FRAMES = [
    ('1-1(单行)', 'data/fixtures/subchapter_1_1.png'),
    ('1-4困难(3行)', 'data/fixtures/map_hard_1_4.png'),
    ('7-1(3行)', 'data/fixtures/inmap_7-1.png'),
    ('|对照|2-2(4行)', 'data/fixtures/inmap_2-2.png'),
    ('|对照|3-1(4行)', 'data/fixtures/inmap_3-1.png'),
    ('|对照|3-2(4行)', 'data/fixtures/inmap_3-2.png'),
]


def op(_n, **a):
    r = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _n, 'args': a})))
    if not r.get('ok'):
        raise RuntimeError(f'{_n}: {r.get("error")}')
    return r['result']


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--frames', nargs='*', default=None)
    p.add_argument('--backends', nargs='*', default=['homography', 'perspective'])
    p.add_argument('--upscale', type=int, default=1)
    args = p.parse_args()
    frames = ([('自定义', f) for f in args.frames] if args.frames else DEFAULT_FRAMES)

    results = {}
    for label, rel in frames:
        path = os.path.join(REPO, rel)
        if not os.path.exists(path):
            print(f'{label:16s} 缺帧 {rel}', flush=True)
            continue
        op('screenshot_load', path=path)
        row = {}
        for backend in args.backends:
            try:
                r = op('map_detect', backend=backend, upscale=args.upscale)
            except Exception as e:
                row[backend] = f'异常 {type(e).__name__}: {e}'
                continue
            if r.get('error'):
                row[backend] = f'FAIL {r["error"]}'
            elif r.get('shape'):
                row[backend] = (f'OK shape={r["shape"]} 格数={r.get("grid_count")} '
                                f'标志={r.get("flag_count")} '
                                f'边L/R/U/D={r.get("left_edge")}/{r.get("right_edge")}/'
                                f'{r.get("upper_edge")}/{r.get("lower_edge")}')
            else:
                row[backend] = 'FAIL 无结果 ' + json.dumps(
                    {k: v for k, v in r.items() if k in ('construct_error', 'load_error',
                                                         'inner_v', 'inner_h')},
                    ensure_ascii=False)[:160]
        results[label] = row

    print(f'\n{"帧":16s} ' + ' '.join(f'{b:52s}' for b in args.backends), flush=True)
    for label, row in results.items():
        print(f'{label:16s} ' + ' '.join(f'{row.get(b, "-"):52s}' for b in args.backends),
              flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())

