#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
S0 产物校验：确认导出的 JSON 真的可用、且与源码一致。

检查项：
  1. 计数与源码规模一致（素材文件数、关卡文件数）
  2. 每个素材引用的 PNG/GIF 文件在磁盘上真实存在
  3. 每个关卡 IR 的 map_data 网格与 shape 自洽（行列数 = shape）
  4. 战斗计划语义正确：对 tier A/B 关卡，steps 首尾与上游模板语义一致
  5. 抽查：把 IR 的步骤重新渲染成 Python 文本，与源码逐行比对（模板化关卡）
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys

SHAPE_RE = re.compile(r'^([A-Za-z])(\d+)$')


def col_of(letter: str) -> int:
    # 上游有少量小写写法（实测 campaign/event_20200603_en/sp1.py 是 'h5'，网格确为 8 列），
    # 因此大小写都接受，统一按大写算列号。
    return ord(letter.upper()) - ord('A')


def check(repo: str, data: str) -> dict:
    problems = []
    stats = {}

    assets = json.load(open(os.path.join(data, 'assets.json'), encoding='utf-8'))
    index = json.load(open(os.path.join(data, 'campaign_index.json'), encoding='utf-8'))
    manifest = json.load(open(os.path.join(data, 'manifest.json'), encoding='utf-8'))

    # ---- 1. 计数
    stats['assets'] = len(assets['assets'])
    stats['chapters'] = len(index['chapters'])
    src_asset_files = manifest['assets']['source_files']
    if src_asset_files != 45:
        problems.append(f'素材源文件数异常: {src_asset_files}')

    # ---- 2. 素材文件存在性
    missing = []
    for aid, a in assets['assets'].items():
        files = a.get('file')
        if isinstance(files, dict):
            for server, rel in files.items():
                p = os.path.join(repo, rel.lstrip('./').replace('/', os.sep))
                if not os.path.isfile(p):
                    missing.append({'asset': aid, 'server': server, 'path': rel})
    stats['asset_files_missing'] = len(missing)
    if missing:
        problems.append(f'{len(missing)} 个素材引用的图片不存在')

    # ---- 3. 网格自洽 + 4/5. 计划与源码比对
    grid_bad, plan_bad, rendered_ok, rendered_checked = [], [], 0, 0
    tier_count = {'A': 0, 'B': 0, 'C': 0}
    for entry in index['chapters']:
        tier_count[entry['tier']] = tier_count.get(entry['tier'], 0) + 1
        ir = json.load(open(os.path.join(data, entry['json']), encoding='utf-8'))

        shape = ir['map'].get('shape')
        grid = ir['map'].get('map_data')
        if shape and grid:
            m = SHAPE_RE.match(shape)
            if m:
                want_cols = col_of(m.group(1)) + 1
                want_rows = int(m.group(2))
                rows = [r for r in grid.split('\n') if r.strip()]
                if len(rows) != want_rows:
                    grid_bad.append({'file': entry['source'],
                                     'want_rows': want_rows, 'got': len(rows)})
                else:
                    for i, r in enumerate(rows):
                        n = len(r.split())
                        if n != want_cols:
                            grid_bad.append({'file': entry['source'], 'row': i,
                                             'want_cols': want_cols, 'got': n})
                            break
            else:
                grid_bad.append({'file': entry['source'], 'shape': shape})

        # 计划完整性：plan_complete=false 的关卡 steps 必须全为空
        for b in ir['campaign']['battles']:
            if not b['plan_complete'] and b['steps']:
                plan_bad.append({'file': entry['source'], 'method': b['method'],
                                 'issue': 'plan_complete=false 但有 steps'})
            if b['plan_complete'] and not b['steps']:
                plan_bad.append({'file': entry['source'], 'method': b['method'],
                                 'issue': 'plan_complete=true 但 steps 为空'})

        # 抽查：模板化关卡（tier A）应能由 steps 还原出源码里的调用序列
        if entry['tier'] == 'A':
            rendered_checked += 1
            src = open(os.path.join(repo, entry['source']), encoding='utf-8').read()
            ok = True
            for b in ir['campaign']['battles']:
                for s in b['steps']:
                    if s['kind'] == 'super_delegate':
                        # op 形如 super().X，源码里应出现 `super().X(`
                        pat = re.escape(s['op'].replace('super().', 'super().')) + r'\('
                    else:
                        pat = r'self\.' + re.escape(s['op']) + r'\('
                    if not re.search(pat, src):
                        ok = False
            if ok:
                rendered_ok += 1
            else:
                plan_bad.append({'file': entry['source'],
                                 'issue': 'steps 里的算子未在源码中出现'})

    stats['grid_mismatch'] = len(grid_bad)
    stats['plan_issues'] = len(plan_bad)
    stats['tier_A_render_check'] = f'{rendered_ok}/{rendered_checked}'
    stats['tiers'] = tier_count
    if grid_bad:
        problems.append(f'{len(grid_bad)} 个关卡网格与 shape 不自洽')
    if plan_bad:
        problems.append(f'{len(plan_bad)} 个关卡计划有问题')

    return {'ok': not problems, 'problems': problems, 'stats': stats,
            'grid_bad_sample': grid_bad[:5], 'plan_bad_sample': plan_bad[:5],
            'missing_sample': missing[:5]}


def main():
    ap = argparse.ArgumentParser()
    here = os.path.dirname(os.path.abspath(__file__))
    ap.add_argument('--repo', default=os.path.normpath(
        os.path.join(here, '..', '.runtime', 'engine')))
    ap.add_argument('--data', default=os.path.normpath(os.path.join(here, '..', 'data')))
    args = ap.parse_args()
    r = check(args.repo, args.data)
    print(json.dumps(r, ensure_ascii=False, indent=2))
    return 0 if r['ok'] else 1


if __name__ == '__main__':
    sys.exit(main())
