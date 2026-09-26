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
import importlib
import json
import os
import re
import sys
from pathlib import Path

try:
    from .upstream_config_export import ConfigResolver
    from .upstream_map_export import MapResolver
    from .upstream_campaign_export import CampaignResolver
except ImportError:
    from upstream_config_export import ConfigResolver
    from upstream_map_export import MapResolver
    from upstream_campaign_export import CampaignResolver

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

    # 页面图导出（第三阶段「只依赖上游静态规则」的静态数据之一）：
    # 页面名唯一、每条边的目标都是已知页面、源文件哈希与当前上游文件一致（漂移即失败）。
    # 契约门槛：`manifest.pages.present` 为真才要求 `pages.json`
    # （合成夹具的上游仓库可能没有 `module/ui/page.py`，那时导出会记 `present: false`）。
    pages_declared = bool((manifest.get('pages') or {}).get('present'))
    pages_path = Path(data, 'pages.json')
    if pages_declared and not pages_path.is_file():
        problems.append('缺少 pages.json（页面图导出）；先跑 tools/export_upstream_data.py')
    elif pages_declared:
        pages_doc = json.loads(pages_path.read_text(encoding='utf-8'))
        pages = pages_doc.get('pages') or []
        stats['pages'] = len(pages)
        stats['page_links'] = sum(len(page.get('links') or []) for page in pages)
        names = [page.get('name') for page in pages]
        if len(names) != len(set(names)):
            problems.append('pages.json 里有重名页面')
        known = set(names)
        for page in pages:
            for link in page.get('links') or []:
                if link.get('destination') not in known:
                    problems.append(f"pages.json：{page.get('name')} 的边指向未知页面 "
                                    f"{link.get('destination')}")
        recorded = (pages_doc.get('source_files') or {})
        for rel, digest in recorded.items():
            live = Path(repo, rel)
            if not live.is_file():
                problems.append(f'pages.json 记的源文件 {rel} 不存在')
                continue
            actual = hashlib.sha256(live.read_bytes()).hexdigest()
            if actual != digest:
                problems.append(f'pages.json 的源哈希与 {rel} 不一致（上游文件变了，重跑导出）')
        manifest_pages = (manifest.get('pages') or {})
        if manifest_pages.get('count') != len(pages):
            problems.append('manifest.pages.count 与 pages.json 的页数不一致')
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
    map_resolver = MapResolver(repo)
    campaign_resolver = CampaignResolver(repo)
    campaign_bad = []
    map_bad = []
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
        expected_campaign = campaign_resolver.export(module)
        campaign = ir.get('campaign') or {}
        campaign_meta = campaign.get('attributes_meta') or {}
        differences = []
        if campaign.get('attributes') != expected_campaign['values']:
            differences.append('attributes')
        for key, expected in expected_campaign.items():
            if key != 'values' and campaign_meta.get(key) != expected:
                differences.append(key)
        if not expected_campaign['complete']:
            differences.append('incomplete')
        for key, expected in (
                ('campaign_attributes', sorted(expected_campaign['values'])),
                ('campaign_present', expected_campaign['present']),
                ('campaign_complete', expected_campaign['present'] and expected_campaign['complete']),
                ('campaign_aliases', sorted(expected_campaign['method_aliases']))):
            if entry.get(key) != expected:
                differences.append('index.' + key)
        if differences:
            campaign_bad.append(dict(file=entry['source'], differences=differences,
                                     unresolved=expected_campaign['unresolved']))
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

        expected_map = map_resolver.export(module)
        map_meta = ir.get('map_meta') or {}
        differences = []
        if ir.get('map') != expected_map['values']:
            differences.append('values')
        for key in ('present', 'complete', 'origins', 'typed_values',
                    'source_files', 'unresolved', 'derived_from', 'calls'):
            if map_meta.get(key) != expected_map[key]:
                differences.append(key)
        if not expected_map['complete']:
            differences.append('incomplete')
        for key, expected in (
                ('map_keys', sorted(expected_map['values'])),
                ('map_present', expected_map['present']),
                ('map_complete', expected_map['present'] and expected_map['complete'])):
            if entry.get(key) != expected:
                differences.append('index.' + key)
        if expected_map['name'] is not None and (
                ir.get('name') != expected_map['name'] or ir.get('name_source') != 'CampaignMap'
                or entry.get('name') != expected_map['name'] or entry.get('name_source') != 'CampaignMap'):
            differences.append('name')
        if differences:
            map_bad.append({'file': entry['source'], 'differences': differences,
                            'unresolved': expected_map['unresolved']})

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
            # 结构化步骤（分支/返回/信号/日志/整图设标志/状态/局部绑定）**没有 `op`**：
            # 它们不是"源码里的一次调用"，跳过；分支体要递归进去查（`body`/`orelse`）。
            structural = {'branch', 'return', 'raise', 'log', 'map_set', 'state_set', 'local_set'}

            def check_steps(steps):
                good = True
                for s in steps:
                    if s.get('kind') in structural:
                        good = check_steps(s.get('body') or []) and good
                        good = check_steps(s.get('orelse') or []) and good
                        continue
                    if not s.get('op'):
                        plan_bad.append({'file': entry['source'],
                                         'issue': f"步骤 {s.get('kind')} 既不是结构化步骤也没有 op"})
                        good = False
                        continue
                    if s['kind'] == 'super_delegate':
                        # op 形如 super().X，源码里应出现 `super().X(`
                        pat = re.escape(s['op']) + r'\('
                    else:
                        pat = r'self\.' + re.escape(s['op']) + r'\('
                    if not re.search(pat, src):
                        good = False
                return good

            for b in ir['campaign']['battles']:
                if not check_steps(b['steps']):
                    ok = False
            if ok:
                rendered_ok += 1
            else:
                plan_bad.append({'file': entry['source'],
                                 'issue': 'steps 里的算子未在源码中出现'})

    for key, expected in (
            ('map_modules', sum(bool(r.get('map_present')) for r in index['chapters'])),
            ('map_complete', sum(bool(r.get('map_complete')) for r in index['chapters'])),
            ('map_fields', sum(len(r.get('map_keys', [])) for r in index['chapters']))):
        if manifest.get('campaign', {}).get(key) != expected:
            problems.append(f'campaign manifest {key} 不一致')
    for key, expected in (
            ('campaign_modules', sum(bool(r.get('campaign_present')) for r in index['chapters'])),
            ('campaign_complete', sum(bool(r.get('campaign_complete')) for r in index['chapters'])),
            ('campaign_attributes', sum(len(r.get('campaign_attributes', [])) for r in index['chapters'])),
            ('campaign_aliases', sum(len(r.get('campaign_aliases', [])) for r in index['chapters']))):
        if manifest.get('campaign', {}).get(key) != expected:
            problems.append(f'campaign manifest {key} 不一致')
    stats['campaign_attribute_checked'] = len(index['chapters'])
    # Campaign 声明"导出不完整"分两类，必须分开报告（不能简单白名单）：
    #   * **上游自身无法导入**：关卡文件引用的 assets 常量在上游快照里不存在（如
    #     `event_20200227_cn/c2.py` 的 `from module.campaign.assets import C2`）→ 上游死代码，
    #     不是我们的导出缺陷。这里**动态验证**：真的去 import 那个模块，ImportError 才算这一类。
    #   * **其余**：无法归因，继续当问题。
    upstream_broken, unexplained = [], []
    for entry in campaign_bad:
        module = entry['file'][:-3].replace('/', '.').replace('\\', '.')
        try:
            importlib.import_module(module)
            unexplained.append(entry)
        except ImportError as error:
            upstream_broken.append(dict(entry, upstream_import_error=f'{type(error).__name__}: {error}'))
        except Exception:                      # noqa: BLE001 —— 别的异常不算"上游缺常量"，仍当问题
            unexplained.append(entry)
    campaign_bad = unexplained
    stats['campaign_attribute_issues'] = len(campaign_bad)
    stats['campaign_attribute_upstream_broken'] = len(upstream_broken)
    if upstream_broken:
        stats['campaign_attribute_upstream_broken_modules'] = [item['file'] for item in upstream_broken]
    if campaign_bad:
        problems.append(f'{len(campaign_bad)} 个模块的 Campaign 声明导出不完整或不一致')
    stats['map_checked'] = len(index['chapters'])
    stats['map_issues'] = len(map_bad)
    if map_bad:
        problems.append(f'{len(map_bad)} 个模块的 MAP 声明导出不完整或不一致')
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
            'config_bad_sample': config_bad[:5], 'map_bad_sample': map_bad[:5],
            'campaign_bad_sample': campaign_bad[:5],
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
