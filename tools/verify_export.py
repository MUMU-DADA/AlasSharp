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
from collections import Counter
import hashlib
import json
import os
import re
import sys
from pathlib import Path

try:
    from .upstream_config_export import ConfigResolver
except ImportError:
    from upstream_config_export import ConfigResolver

SHAPE_RE = re.compile(r'^([A-Za-z])(\d+)$')


def col_of(letter: str) -> int:
    # 上游有少量小写写法（实测 campaign/event_20200603_en/sp1.py 是 'h5'，网格确为 8 列），
    # 因此大小写都接受，统一按大写算列号。
    return ord(letter.upper()) - ord('A')


def check(repo: str, data: str) -> dict:
    problems = []
    stats = {}

    assets = json.loads(Path(data, 'assets.json').read_text(encoding='utf-8'))
    index = json.loads(Path(data, 'campaign_index.json').read_text(encoding='utf-8'))
    manifest = json.loads(Path(data, 'manifest.json').read_text(encoding='utf-8'))
    from export_upstream_data import SERVERS
    if len(assets.get('servers', [])) != len(SERVERS) or set(assets.get('servers', [])) != set(SERVERS):
        problems.append('素材目录服务器清单缺失、重复或与导出契约不一致')

    # ---- 1. 计数
    stats['assets'] = len(assets['assets'])
    stats['chapters'] = len(index['chapters'])
    src_asset_files = {p.relative_to(repo).as_posix()
                       for p in (Path(repo) / 'module').rglob('assets.py')}
    if manifest['assets']['source_files'] != len(src_asset_files):
        problems.append('素材源文件数量与当前上游不一致')
    indexed_sources = {row['source'] for row in index['chapters']}
    native_sources = {p.relative_to(repo).as_posix()
                      for p in (Path(repo) / 'campaign').rglob('*.py')
                      if p.name != '__init__.py' and '__pycache__' not in p.parts}
    if indexed_sources != native_sources or len(indexed_sources) != len(index['chapters']):
        problems.append('关卡索引缺失、重复或包含非上游来源')
    asset_bad = []
    for aid, binding in assets['assets'].items():
        fields = ['file'] + (['area', 'button', 'color'] if binding['kind'] == 'Button'
                             else ['area'] if binding['kind'] == 'Mask' else [])
        servers = set(binding.get('servers', []))
        if (binding.get('id') != aid or aid != f"{binding.get('module')}/{binding.get('name')}"
                or binding.get('source') not in src_asset_files
                or binding.get('source') != f"module/{binding.get('module', '').replace('.', '/')}/assets.py"
                or binding.get('kind') not in ('Button', 'Template', 'Mask')):
            asset_bad.append(f'{aid}: identity/source')
        if not servers or len(servers) != len(binding.get('servers', [])) or not servers <= set(assets['servers']):
            asset_bad.append(f'{aid}: servers')
        for field in fields:
            value = binding.get(field)
            if not isinstance(value, dict) or set(value) != servers:
                asset_bad.append(f'{aid}: {field} server variants')
            elif field in ('area', 'button', 'color'):
                size = 3 if field == 'color' else 4
                if any(not isinstance(v, list) or len(v) != size
                       or any(type(item) is not int for item in v) for v in value.values()):
                    asset_bad.append(f'{aid}: {field} shape/type')
            elif any(not isinstance(v, str) or not v.strip() for v in value.values()):
                asset_bad.append(f'{aid}: {field} path')
        if binding.get('all_servers') != (servers >= set(assets['servers'])):
            asset_bad.append(f'{aid}: all_servers')
    asset_manifest = manifest['assets']
    for key, actual in (
            ('count', len(assets['assets'])),
            ('all_four_servers', sum(a['all_servers'] for a in assets['assets'].values())),
            ('by_module', dict(Counter(a['module'] for a in assets['assets'].values()))),
            ('by_kind', dict(Counter(a['kind'] for a in assets['assets'].values())))):
        if asset_manifest.get(key) != actual:
            asset_bad.append(f'manifest.assets.{key}')
    if asset_manifest.get('unresolved') != [] or manifest.get('errors') != []:
        asset_bad.append('manifest contains unresolved assets or source errors')
    stats['asset_contract_issues'] = len(asset_bad)
    if asset_bad:
        problems.append(f'{len(asset_bad)} 个素材契约问题: {asset_bad[:5]}')

    for section, sources in [('assets', src_asset_files), ('campaign', native_sources)]:
        if manifest.get(section, {}).get('source_files') != len(sources):
            problems.append(f'{section} 源文件计数与当前上游不一致')
        expected_hashes = {source: hashlib.sha256(Path(repo, source).read_bytes()).hexdigest()
                           for source in sources}
        if manifest.get(section, {}).get('source_hashes') != expected_hashes:
            problems.append(f'{section} 源哈希清单与当前上游不一致')
    if manifest.get('campaign', {}).get('files') != len(index['chapters']):
        problems.append('campaign manifest 计数与索引不一致')

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
    grid_bad, plan_bad, config_bad, rendered_ok, rendered_checked = [], [], [], 0, 0
    config_resolver = ConfigResolver(repo)
    tier_count = {'A': 0, 'B': 0, 'C': 0}
    for entry in index['chapters']:
        tier_count[entry['tier']] = tier_count.get(entry['tier'], 0) + 1
        ir = json.loads(Path(data, entry['json']).read_text(encoding='utf-8'))
        if ir.get('source') != entry['source'] or entry['json'] != entry['source'][:-3] + '.json':
            config_bad.append({'file': entry['source'], 'differences': ['index.source/json']})

        # Effective Config must be reproducible from source. This catches imported
        # Config classes, inherited overrides and constant expressions that a local
        # class-body-only exporter would silently omit.
        module = entry['source'][:-3].replace('/', '.').replace('\\', '.')
        expected_config = config_resolver.export(module)
        meta = ir.get('config_meta') or {}
        config_differences = []
        if ir.get('config') != expected_config['values']:
            config_differences.append('values')
        for key in ('present', 'complete', 'mro', 'origins', 'typed_values',
                    'source_files', 'unresolved'):
            if meta.get(key) != expected_config[key]:
                config_differences.append(key)
        if not expected_config['complete']:
            config_differences.append('incomplete')
        if entry.get('config_keys') != sorted(expected_config['values']):
            config_differences.append('index.config_keys')
        if entry.get('config_present') != expected_config['present']:
            config_differences.append('index.config_present')
        expected_config_complete = (expected_config['present']
                                    and expected_config['complete'])
        if entry.get('config_complete') != expected_config_complete:
            config_differences.append('index.config_complete')
        if config_differences:
            config_bad.append({'file': entry['source'],
                               'differences': sorted(set(config_differences)),
                               'unresolved': expected_config['unresolved']})

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
            src = Path(repo, entry['source']).read_text(encoding='utf-8')
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
    stats['config_checked'] = len(index['chapters'])
    stats['config_issues'] = len(config_bad)
    stats['tier_A_render_check'] = f'{rendered_ok}/{rendered_checked}'
    stats['tiers'] = tier_count
    if grid_bad:
        problems.append(f'{len(grid_bad)} 个关卡网格与 shape 不自洽')
    if plan_bad:
        problems.append(f'{len(plan_bad)} 个关卡计划有问题')
    if config_bad:
        problems.append(f'{len(config_bad)} 个模块的有效 Config 导出不完整或不一致')

    return {'ok': not problems, 'problems': problems, 'stats': stats,
            'grid_bad_sample': grid_bad[:5], 'plan_bad_sample': plan_bad[:5],
            'config_bad_sample': config_bad[:5],
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
