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
    ROOT, '..', 'my fork project', 'AzurLaneAutoScript'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def rows(text):
    return [line.split() for line in (text or '').strip().split('\n') if line.strip()]


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
        'camera=%s' % ','.join(str(c) for c in camera),
        'spawnpts=%s' % ','.join(str(c) for c in spawnpts),
        'spawn=%d' % len(spawn), 'battles=%d' % battles,
    ])


def main():
    digests_path = os.path.join(DATA, 'map_ir_digests.json')
    if not os.path.exists(digests_path):
        print('缺 %s：先跑 `alashub map-ir`' % digests_path)
        return 2
    digests = json.load(open(digests_path, encoding='utf-8'))
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
        print('%-42s %s' % (key, 'MATCH' if entry['match'] else 'DIFF'))
        if not entry['match']:
            print('   C#      : %s' % digests[key])
            print('   upstream: %s' % up)
        results.append(entry)

    matched = sum(1 for r in results if r['match'])
    skipped = sum(1 for r in results if r['match'] is None)
    print()
    print('对照结果: %d 匹配 / %d 跳过 / 共 %d' % (matched, skipped, len(results)))

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
        '## 结果：%d 匹配 / %d 跳过 / 共 %d' % (matched, skipped, len(results)),
        '',
        'IR 文件 %d 个，其中 `*_base.json` **基类模块 %d 个（不是章节，没有 MAP）**，'
        '真实章节 **%d** 个 —— 导出层把基类也当章节了，见下面的"顺带发现"。'
        % (out['total_ir_files'], out['base_modules'], out['real_chapters']),
        '',
        '| 章节 | 结果 | C# 摘要 |',
        '| --- | --- | --- |',
    ]
    for r in results:
        if r['match']:
            verdict = '✅ 匹配'
        elif r['match'] is None:
            verdict = '⏭️ %s' % (r.get('error') or '')
        else:
            verdict = '❌ 不一致'
        lines.append('| `%s` | %s | `%s` |' % (r['chapter'], verdict, r['csharp']))
    if skipped:
        lines += ['', '## 跳过原因', '']
        for r in results:
            if r['match'] is None:
                lines.append('- `%s`：%s' % (r['chapter'], r.get('error')))
    lines += [
        '',
        '## 顺带发现：导出层把基类模块当成了章节',
        '',
        '`campaign/**/*.py` 里有 %d 个 `*_base.py`（`campaign_2_base`、`campaign_base` 之类），'
        '它们是**基类模块，没有 `MAP` 对象**，但导出层把它们也导成了"章节"。'
        % out['base_modules'],
        '',
        '后果：凡是以 IR 文件数统计"章节数"的地方都会虚高 —— 实际是 **%d 个真实章节**。'
        % out['real_chapters'],
        '本脚本按设计跳过它们（`AttributeError: module ... has no attribute MAP` 就是这么来的）。',
        '要不要在导出层过滤掉，等下一轮改导出器时一起处理（改动会牵动 IR 数量与既有校验口径，'
        '不适合顺手改）。',
        '',
        '## 复现',
        '',
        '```powershell',
        'alashub map-ir                              # C# 解析全部 IR 并导出摘要',
        'python tools/diagnostics/verify_map_ir.py   # 与上游活对象对照（固定随机种子）',
        '```',
        '',
    ]
    doc = os.path.join(DOCS, 'map-ir.md')
    with open(doc, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    print('报告: %s' % doc)
    return 0 if (skipped == 0 and matched == len(results)) else 1


if __name__ == '__main__':
    sys.exit(main())
