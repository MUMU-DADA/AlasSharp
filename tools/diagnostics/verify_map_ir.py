# -*- coding: utf-8 -*-
"""地图模型（S2 数据半边）的**跨语言对照**：C# 解析 vs 上游活对象。

为什么值得单独做：地图模型的字段看着简单（shape / map_data / weight_data / camera…），
但解析规则很隐蔽 —— 例如 `shape` 走上游的 `node2location()`，末行是
`ord(node[0]) % 32 - 1, int(node[1:]) - 1`，而网格数是 **shape+1**。
这类"差一"一旦弄错，后面所有坐标都偏一格，而且不会以"识别不准"的形式暴露。
所以这里拿**上游自己的对象**当唯一真值：导入章节模块读活着的 `MAP`，
用与 C# 完全相同的字段顺序拼摘要，逐字符比。

导入错误、缺少摘要、字段及网格不一致均为失败；不把源错误伪装成跳过。
"""
from collections import Counter
from pathlib import Path
import json
import os
import random
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOT = os.path.normpath(os.path.join(HERE, '..'))
sys.path.insert(0, HERE)
DATA = os.path.join(ROOT, 'data')
DOCS = os.path.join(ROOT, 'docs')
FORK = os.environ.get('ALAS_FORK') or os.path.normpath(os.path.join(
    ROOT, '.runtime', 'engine'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def rows(text):
    return [line.split() for line in (text or '').strip().split('\n') if line.strip()]


# 兼容别名（上游指纹函数里用的名字）
rows_of = rows


def upstream_digest(module_path):
    """导入上游章节模块，读活着的 MAP，拼出与 C# DigestComparable 同序的摘要。"""
    import importlib
    if FORK not in sys.path:
        sys.path.insert(0, FORK)
    os.chdir(FORK)                      # 上游素材/配置是相对路径
    mod = importlib.import_module(module_path)
    MAP = mod.Campaign.MAP
    shape = tuple(int(v) for v in MAP.shape)
    sx, sy = shape
    map_rows = rows(MAP.map_data)
    weight_rows = rows(MAP.weight_data)
    weight_sum = sum(int(c) for r in weight_rows for c in r)
    tokens = sum(len(r) for r in map_rows)
    camera = list(MAP.camera_data or [])
    spawnpts = list(MAP.camera_data_spawn_point or [])
    spawn = list(MAP.spawn_data or [])
    battles = 0
    for s in spawn:
        try:
            battles = max(battles, int(s['battle']) + 1)
        except Exception:
            pass
    return '|'.join([
        '%d,%d' % (sx, sy), '%dx%d' % (sx + 1, sy + 1),
        'rows=%d' % len(map_rows), 'tokens=%d' % tokens, 'weight=%d' % weight_sum,
        # 相机点与出生点都按**集合**比（排序后拼）：上游是 SelectedGrids，顺序无用途，
        # 其顺序来自 CPython set() 迭代顺序 —— 复刻它是复刻实现细节。成员仍逐项校验。
        'camera=%s' % ','.join(sorted(str(c) for c in camera)),
        'spawnpts=%s' % ','.join(sorted(str(c) for c in spawnpts)),
        'spawn=%d' % len(spawn), 'battles=%d' % battles,
    ])


def upstream_grid_fingerprint(MAP):
    """用**上游自己的 GridInfo.decode** 逐格解码，拼出与 C# 同格式的指纹。

    注意两点（都在 C# 侧照抄了）：上游 `decode` 先 `text.upper()`（所以 `Me` 等同 `ME`）；
    `--` 不在表里 → 八个查表标志为假；指纹是 `.`，推导值仍按上游公式计算。
    """
    from module.map_detection.grid_info import GridInfo

    def fp(info):
        if info.is_land:
            return 'L'
        if info.is_spawn_point:
            return 'S'
        if info.is_submarine_spawn_point:
            return 'U'
        if info.may_enemy:
            return 'E'
        if info.may_boss:
            return 'B'
        if info.may_mystery:
            return 'M'
        if info.may_ammo:
            return 'A'
        if info.may_siren:
            return 'R'
        return '.'

    rows = []
    for row in rows_of(MAP.map_data):
        line = []
        for token in row:
            g = GridInfo()
            g.decode(token)
            line.append(fp(g))
        rows.append(''.join(line))
    return '\n'.join(rows)


def compare(key, digest, grid_digest):
    import importlib
    from module.map.map_base import CampaignMap
    entry = dict(chapter=key, csharp=digest, match=False, grid_match=False)
    module_path = 'campaign.' + key[:-5].replace('/', '.')
    try:
        module = importlib.import_module(module_path)
    except Exception as error:
        entry.update(status='upstream_error', error=f'{type(error).__name__}: {error}')
        return entry
    native_map = getattr(getattr(module, 'Campaign', None), 'MAP', None)
    if not isinstance(native_map, CampaignMap):
        entry.update(status='support_module', match=None, grid_match=None,
                     reason='Imported module has no native Campaign.MAP')
        return entry
    try:
        entry['upstream'] = upstream_digest(module_path)
        entry['match'] = entry['upstream'] == digest
        entry['grid_match'] = upstream_grid_fingerprint(native_map) == grid_digest
        entry['status'] = 'passed' if entry['match'] and entry['grid_match'] else 'failed'
    except Exception as error:
        entry.update(status='failed', error=f'{type(error).__name__}: {error}')
    return entry


def main():
    try:
        digests = json.loads(Path(DATA, 'map_ir_digests.json').read_text(encoding='utf-8'))
        grids = json.loads(Path(DATA, 'map_ir_grid_digests.json').read_text(encoding='utf-8'))
    except (OSError, ValueError) as error:
        print(f'Missing/invalid C# digests; run Alas.Server map-ir: {type(error).__name__}')
        return 2
    index = json.loads(Path(DATA, 'campaign_index.json').read_text(encoding='utf-8'))['chapters']
    keys = sorted(row['json'].removeprefix('campaign/') for row in index)
    integrity = []
    for label, values in [('digest', digests), ('grid', grids)]:
        if set(values) != set(keys):
            integrity.append(f'{label} inventory mismatch: missing={len(set(keys)-set(values))}, '
                             f'extra={len(set(values)-set(keys))}')
    count = min(int(os.environ.get('SAMPLE', str(len(keys)))), len(keys))
    if count < 1:
        raise ValueError('SAMPLE must be positive')
    sample = keys if count == len(keys) else sorted(random.Random(20260922).sample(keys, count))
    sys.path.insert(0, FORK)
    os.chdir(FORK)
    from module.logger import logger
    logger.setLevel(50)
    results = [compare(key, digests.get(key), grids.get(key)) for key in sample]
    for row in results:
        if 'error' in row:
            row['error'] = row['error'].replace(ROOT, '<project>').replace(
                Path(ROOT).as_posix(), '<project>')
    counts = Counter(row['status'] for row in results)
    failed = bool(integrity or counts['failed'] or counts['upstream_error'])
    output = dict(total_ir_files=len(keys), exhaustive=count == len(keys), counts=dict(counts),
                  integrity_errors=integrity, sample=results, ok=not failed)
    Path(DATA, 'map_ir_crosscheck.json').write_text(
        json.dumps(output, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    lines = [
        '# 地图模型跨语言对照', '',
        '由 `tools/diagnostics/verify_map_ir.py` 生成；C# 摘要来自 `Alas.Server map-ir`。',
        '对照上游实际 `Campaign.MAP` 的字段摘要和逐格 `GridInfo.decode`。相机集合排序后比较成员。',
        '辅助模块按原生对象分类；导入错误、缺少摘要和网格不一致均失败，不按文件名跳过。',
        '这是离线数据对照，不证明可进入关卡、战斗流程或真实通关。', '',
        f'索引 {len(keys)} 个模块，本次检查 {count} 个；全量：{output["exhaustive"]}。', '',
        '| 分类 | 数量 |', '| --- | --- |',
    ]
    lines.extend(f'| {name} | {value} |' for name, value in sorted(counts.items()))
    lines += ['', '## 差异及源错误', '']
    lines.extend(f'- {error}' for error in integrity)
    problems = [row for row in results if row['status'] in ('failed', 'upstream_error')]
    for row in problems:
        detail = row.get('error') or f'字段匹配={row["match"]}，网格匹配={row["grid_match"]}'
        lines.append(f'- `{row["chapter"]}`：{detail}')
    if not problems and not integrity:
        lines.append('无。')
    lines += ['', '脱敏范围：错误中的项目绝对路径替换为 `<project>`；未改变判据或错误类型。', '',
              '复现：`Alas.Server map-ir` 后运行 `python tools/diagnostics/verify_map_ir.py`。',
              '默认检查全部模块；`SAMPLE` 环境变量仅供显式抽样诊断。', '']
    Path(DOCS, 'archive/reports/map-ir.md').write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(dict(counts=counts, integrity_errors=integrity, ok=not failed)))
    return 1 if failed else 0


if __name__ == '__main__':
    raise SystemExit(main())
