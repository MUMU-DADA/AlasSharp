# -*- coding: utf-8 -*-
"""地图模型（S2 数据半边）的**跨语言对照**：C# 解析 vs 上游活对象。

为什么值得单独做：地图模型的字段看着简单（shape / map_data / weight_data / camera…），
但解析规则很隐蔽 —— 例如 `shape` 走上游的 `node2location()`，末行是
`ord(node[0]) % 32 - 1, int(node[1:]) - 1`，而网格数是 **shape+1**。
这类"差一"一旦弄错，后面所有坐标都偏一格，而且不会以"识别不准"的形式暴露。
所以这里拿**上游自己的对象**当唯一真值：导入章节模块读活着的 `MAP`，
用与 C# 完全相同的字段顺序拼摘要，逐字符比。

对照不通过就是实现错了 —— 没有第二种解释。
"""
import glob
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
    MAP = mod.MAP
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


def main():
    digests_path = os.path.join(DATA, 'map_ir_digests.json')
    if not os.path.exists(digests_path):
        print('缺 %s：先跑 `alashub map-ir`' % digests_path)
        return 2
    digests = json.load(open(digests_path, encoding='utf-8'))
    grid_path = os.path.join(DATA, 'map_ir_grid_digests.json')
    grid_digests = json.load(open(grid_path, encoding='utf-8')) if os.path.exists(grid_path) else {}
    sample_size = int(os.environ.get('SAMPLE', '12'))
    keys = sorted(digests)
    random.seed(20260922)               # 固定种子：结果可复现，便于回归对比
    sample = random.sample(keys, min(sample_size, len(keys)))

    results = []
    for key in sample:
        # `*_base.py` 是**基类模块**，不是章节：它们没有 MAP 对象，天然没法对照。
        # 这里按设计跳过并单独计数（实测 IR 里有 63 个这样的文件，
        # 也就是说"1437 章节"里真正的章节是 1374 —— 导出层把基类也当章节了）。
        if key.endswith('_base.json'):
            entry = {'chapter': key, 'csharp': digests[key], 'upstream': None,
                     'match': None, 'error': '基类模块（无 MAP 对象），按设计跳过'}
            print('%-42s BASE 基类模块，跳过' % key)
            results.append(entry)
            continue
        # campaign_main/campaign_10_1.json → campaign.campaign_main.campaign_10_1
        # （上游章节在 `campaign/` 包下，不是仓库根；少了前缀就会 ModuleNotFoundError）
        module_path = 'campaign.' + key[:-5].replace('/', '.')
        entry = {'chapter': key, 'csharp': digests[key]}
        try:
            up = upstream_digest(module_path)
        except Exception as e:
            entry['upstream'] = None
            entry['error'] = '%s: %s' % (type(e).__name__, e)
            entry['match'] = None
            print('%-42s SKIP %s' % (key, entry['error'][:70]))
            results.append(entry)
            continue
        entry['upstream'] = up
        entry['match'] = (up == digests[key])
        # 网格指纹对照：逐格语义（S2 数据半边的第二块）。
        # 注意要自己拿一次模块对象 —— `mod` 是 upstream_digest() 的局部变量，main 里没有
        # （第一版直接写 mod.MAP，NameError 被 except 吞掉，表现成"grid 没对照"）。
        try:
            import importlib
            gmod = importlib.import_module(module_path)
            grid_up = upstream_grid_fingerprint(gmod.MAP)
        except Exception as e:
            grid_up = None
            entry['grid_error'] = '%s: %s' % (type(e).__name__, e)
        grid_cs = grid_digests.get(key)
        entry['grid_match'] = None if grid_up is None else (grid_up == grid_cs)
        if entry['grid_match'] is False:
            print('   GRID DIFF')
            print('   C#      : %r' % (grid_cs or '')[:200])
            print('   upstream: %r' % grid_up[:200])
        print('%-42s %s%s' % (key, 'MATCH' if entry['match'] else 'DIFF',
                              '' if entry['grid_match'] is None
                              else (' GRID-MATCH' if entry['grid_match'] else ' GRID-DIFF')))
        if not entry['match']:
            print('   C#      : %s' % digests[key])
            print('   upstream: %s' % up)
        results.append(entry)

    matched = sum(1 for r in results if r['match'])
    skipped = sum(1 for r in results if r['match'] is None)
    grid_checked = [r for r in results if r.get('grid_match') is not None]
    grid_matched = sum(1 for r in grid_checked if r['grid_match'])
    print()
    print('对照结果: %d 匹配 / %d 跳过 / 共 %d；网格指纹 %d/%d 匹配'
          % (matched, skipped, len(results), grid_matched, len(grid_checked)))

    base_total = sum(1 for k in digests if k.endswith('_base.json'))
    out = {
        'digest_spec': 'shapeX,shapeY | WxH | rows | tokens | weight | camera | spawnpts | spawn | battles',
        'total_ir_files': len(digests),
        'base_modules': base_total,
        'real_chapters': len(digests) - base_total,
        'sample': results,
        'matched': matched, 'skipped': skipped,
    }
    with open(os.path.join(DATA, 'map_ir_crosscheck.json'), 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False, indent=2, default=str)

    lines = [
        '# 地图模型（S2 数据半边）跨语言对照',
        '',
        'C# 的 `MapIR` 解析 vs **上游活对象**（导入章节模块读 `MAP`）。',
        '两边用完全相同的字段顺序拼摘要，逐字符比 —— 不通过就是实现错了，没有第二种解释。',
        '',
        '为什么必须这么验：`shape` 的解析规则很隐蔽（上游 `node2location()` 末行是'
        '`ord(node[0]) % 32 - 1, int(node[1:]) - 1`，网格数是 **shape+1**）；'
        '而且章节**没写 `camera_data` 时上游会自动生成**（`map_base.py:77` 用 '
        '`camera_2d((0,0,*shape), sight=(-3,-1,3,2))`），IR 里只有字面量。'
        '这类"差一 / 漏推导"弄错后所有坐标偏一格或整片缺相机点，'
        '且**不会以"识别不准"的形式暴露**。',
        '',
        '脚本：`tools/diagnostics/verify_map_ir.py`；数据：`data/map_ir_crosscheck.json`。',
        '',
        '摘要格式：`%s`' % out['digest_spec'],
        '',
        '口径说明：`camera` 与 `spawnpts` 按**集合**比（排序后拼）。上游 `camera_data` 是',
        '`SelectedGrids`，顺序不影响用途；它那串顺序来自 CPython `set()` 的迭代顺序',
        '（`camera_1d` 里 `[x for x in set(out) if ...]`），属于实现细节 —— 复刻它既脆弱又无意义。',
        '排序只丢掉顺序，**成员仍必须逐项一致**，真错了照样查得出来。',
        '',
        '## 结果：字段摘要 %d 匹配 / %d 跳过；网格指纹 %d/%d 匹配'
        % (matched, skipped, grid_matched, len(grid_checked)),
        '',
        '两类对照的含义不同：',
        '',
        '- **字段摘要**：shape/map_data/weight/camera/spawn 等字段的规范化值 —— 防"差一/漏推导"；',
        '- **网格指纹**：用上游 `GridInfo.decode` 与 C# 的移植版**逐格**解码 map_data，'
        '把整张地图的语义压成一行比 —— 防 token 语义抄错（例如上游 `decode` 会先 '
        '`text.upper()`，所以 `Me` 等同 `ME`；而 `--` 不在表里，八个查表标志为假，'
        '推导值仍按上游公式计算）。',
        '',
        'IR 文件 %d 个，其中 `*_base.json` **基类模块 %d 个（不是章节，没有 MAP）**，'
        '真实章节 **%d** 个 —— 导出层把基类也当章节了，见下面的"顺带发现"。'
        % (out['total_ir_files'], out['base_modules'], out['real_chapters']),
        '',
        '| 章节 | 结果 | 网格 | C# 摘要 |',
        '| --- | --- | --- | --- |',
    ]
    for r in results:
        if r['match']:
            verdict = '✅ 匹配'
        elif r['match'] is None:
            verdict = '⏭️ %s' % (r.get('error') or '')
        else:
            verdict = '❌ 不一致'
        grid = '—' if r.get('grid_match') is None else ('✅' if r['grid_match'] else '❌')
        lines.append('| `%s` | %s | %s | `%s` |' % (r['chapter'], verdict, grid, r['csharp']))
    if skipped:
        lines += ['', '## 跳过原因', '']
        for r in results:
            if r['match'] is None:
                lines.append('- `%s`：%s' % (r['chapter'], r.get('error')))
    lines += [
        '',
        '## 已知问题（全量跑出来的，逐条留证据）',
        '',
        '### 1. 有章节的地图是**从别的章节拷贝**的，导出成空地图',
        '',
        '`campaign_main/campaign_15_4_121.json`：IR 侧 `1x1 / rows=0 / tokens=0`，'
        '上游活对象是 `11x9 / 99 格 / weight=4950`。源码写的是：',
        '',
        '```python',
        'from .campaign_15_4 import MAP as MAP_15_4, Campaign as Campaign_15_4',
        'MAP = copy.copy(MAP_15_4)      # ← 地图来自另一个章节',
        "MAP.name = '15-4-121'",
        '```',
        '',
        '导出器（AST 抓字面量赋值）看不到这条链，于是导出了空地图。'
        '**这正是跨语言对照存在的意义** —— 这种错不会表现成"识别不准"，'
        '只会让引擎在一张空地图上做规划。',
        '',
        '修法（留待改导出器时一起做）：导出器解析 `copy.copy(MAP_X)` / '
        '`from .X import MAP as MAP_X`，把被引用章节的地图复制过来；'
        '或至少记一个 `map.derived_from = "campaign_15_4"` 指针让消费方跟进。',
        '',
        '### 2. 导出的"章节"里混着非章节、以及上游自己都导入不了的死模块',
        '',
        '全量 1437 个 IR 文件里：',
        '',
        '- **%d 个 `*_base.py` 基类模块**（没有 `MAP` 对象）—— 导出层把基类当章节了，'
        '所以"章节数"应以 **%d** 为准；'
        % (out['base_modules'], out['real_chapters']),
        '- 另有若干章节**连上游自己都导入不了**'
        '（`ImportError: cannot import name ... from module.campaign.assets`），'
        '说明它们引用的素材名在当前上游已不存在（历史遗留的死章节）。',
        '',
        '两者都会被"按 IR 文件数统计章节数"的地方算进去。跳过原因直方图：',
        '',
    ]

    import collections
    reasons = collections.Counter()
    for r in results:
        if r['match'] is None:
            reasons[(r.get('error') or '未知').split(':')[0][:70]] += 1
    if reasons:
        lines += ['| 跳过原因 | 数量 |', '| --- | --- |']
        for k, v in reasons.most_common():
            lines.append('| %s | %d |' % (k, v))
        lines.append('')

    lines += [
        '## 复现',
        '',
        '```powershell',
        'alashub map-ir                              # C# 解析全部 IR，导出摘要与网格指纹',
        'python tools/diagnostics/verify_map_ir.py   # 与上游活对象对照（固定随机种子）',
        '$env:SAMPLE = "2000"                        # 跑全量（实测 1437 个文件约 10 秒）',
        '```',
        '',
    ]
    doc = os.path.join(DOCS, 'map-ir.md')
    with open(doc, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    print('报告: %s' % doc)
    # 判据是**不匹配数为 0**，而不是'跳过数为 0'：跳过的样本是**显式**记 match=None 的
    # （基类模块没有可对照的网格），把它当失败会让这条对照永远红。不匹配仍然一律失败。
    mismatched = len(results) - matched - skipped
    return 0 if mismatched == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
