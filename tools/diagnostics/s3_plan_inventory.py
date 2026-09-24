# -*- coding: utf-8 -*-
"""S3 计划词表清点：从关卡 IR 里统计"要实现哪些引擎调用"。

为什么先做这个：S3 = 执行上游的声明式关卡计划（`campaign.battles[].calls`）。
在写引擎之前，必须先知道**调用词表有多大、各自被多少章节用到、按 tier 怎么分布** ——
否则会凭感觉排期（"120 个方法"这类数字来自源码方法数，不等于实际被计划调用的集合）。

输出：
  docs/archive/reports/s3-plan-vocabulary.md      离线调用清单（不作为运行计划）
  data/s3_plan_inventory.json     机读结果

判据说明：tier 来自导出器（A=JSON 规则表即可 / B=计划完整但用了词表外算子 / C=需原生实现）。
`*_base.json` 是基类模块不是章节，统计时排除。
"""
import collections
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
CAMPAIGN = os.path.join(ROOT, 'data', 'campaign')
DATA_OUT = os.path.join(ROOT, 'data', 's3_plan_inventory.json')
DOC_OUT = os.path.join(ROOT, 'docs', 'archive/reports/s3-plan-vocabulary.md')

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def main():
    calls = collections.Counter()
    tier_calls = collections.defaultdict(collections.Counter)
    chapters_by_tier = collections.Counter()
    chapters_using = collections.defaultdict(set)      # 调用名 → 用到它的章节数
    methods = complete = hooks = 0
    files = 0

    for path in glob.glob(os.path.join(CAMPAIGN, '**', '*.json'), recursive=True):
        if os.path.basename(path).endswith('_base.json'):
            continue                                  # 基类模块，不是章节
        try:
            with open(path, encoding='utf-8') as f:
                ir = json.load(f)
        except Exception:
            continue
        files += 1
        camp = ir.get('campaign') or {}
        tier = camp.get('tier')
        chapters_by_tier[tier] += 1
        rel = os.path.relpath(path, CAMPAIGN).replace('\\', '/')
        for battle in camp.get('battles') or []:
            methods += 1
            if battle.get('plan_complete'):
                complete += 1
            for name in battle.get('calls') or []:
                calls[name] += 1
                tier_calls[tier][name] += 1
                chapters_using[name].add(rel)
        hooks += len(camp.get('native_overrides') or [])

    # 按 tier 分层：tier A 用到的 = 最小实现目标
    a_names = set(tier_calls['A'])
    shared_ab = {n for n in a_names if tier_calls['B'][n] or tier_calls['C'][n]}
    a_only = a_names - shared_ab
    c_only = {n for n in calls if not tier_calls['A'][n] and not tier_calls['B'][n]}

    rows = []
    for name, n in calls.most_common():
        rows.append({
            'call': name, 'total': n,
            'chapters': len(chapters_using[name]),
            'A': tier_calls['A'][name], 'B': tier_calls['B'][name], 'C': tier_calls['C'][name],
        })

    result = {
        'chapters': files,
        'chapters_by_tier': dict(chapters_by_tier),
        'battle_methods': methods,
        'plan_complete_methods': complete,
        'native_hooks': hooks,
        'vocabulary_size': len(calls),
        'total_call_occurrences': sum(calls.values()),
        'tier_a_core': sorted(a_names, key=lambda n: -calls[n]),
        'tier_a_only': sorted(a_only, key=lambda n: -calls[n]),
        'shared_ab': sorted(shared_ab, key=lambda n: -calls[n]),
        'tier_c_only': sorted(c_only, key=lambda n: -calls[n]),
        'calls': rows,
    }
    with open(DATA_OUT, 'w', encoding='utf-8') as f:
        json.dump(result, f, ensure_ascii=False, indent=2)

    lines = [
        '# S3 导出调用词表（离线统计）',
        '',
        '统计 `campaign.battles[].calls` 中可导出的调用，仅用于覆盖调查。',
        '生产流程执行原生 `Campaign.run()`；IR 分级与调用频次不证明可运行、通关或迁移优先级。',
        '',
        '脚本：`tools/diagnostics/s3_plan_inventory.py`；数据：`data/s3_plan_inventory.json`。',
        '',
        '## 总量',
        '',
        '| 指标 | 数值 |',
        '| --- | --- |',
        '| 章节数（不含 `*_base` 基类模块） | **%d** |' % files,
        '| tier 分布 | %s |' % ' / '.join('%s %d' % (k, v) for k, v in sorted(chapters_by_tier.items())),
        '| battle_* 方法 | %d（计划完整 %d = %.1f%%） |'
        % (methods, complete, 100.0 * complete / max(methods, 1)),
        '| 引擎钩子（native_overrides） | %d |' % hooks,
        '| **调用词表** | **%d 个不同名字 / %d 次出现** |' % (len(calls), sum(calls.values())),
        '',
        '## tier A 用到的调用（%d 个）' % len(a_names),
        '',
        '同一方法可能多次调用；出现次数与章节数量分别统计。',
        '',
        '| 调用 | 总次数 | 用到的章节数 | A | B | C |',
        '| --- | --- | --- | --- | --- | --- |',
    ]
    for r in rows:
        if r['call'] in a_names:
            lines.append('| `%s` | %d | %d | %d | %d | %d |'
                         % (r['call'], r['total'], r['chapters'], r['A'], r['B'], r['C']))
    lines += [
        '',
        '## 完整词表（%d 个）' % len(rows),
        '',
        '| 调用 | 总次数 | 章节数 | A | B | C | 备注 |',
        '| --- | --- | --- | --- | --- | --- | --- |',
    ]
    for r in rows:
        note = ''
        if r['call'] in c_only:
            note = '仅 tier C'
        elif r['A'] and not r['B'] and not r['C']:
            note = '仅 tier A'
        lines.append('| `%s` | %d | %d | %d | %d | %d | %s |'
                     % (r['call'], r['total'], r['chapters'], r['A'], r['B'], r['C'], note))
    lines += [
        '',
        '## tier C 独有调用（%d 个）' % len(c_only),
        '',
        # 次数相同的按名字再排一次：只按 -calls 排，同分项的顺序取决于 set 的迭代顺序，
        # 而 Python 的字符串哈希每个进程都不同（PYTHONHASHSEED）→ 生成产物每次都在抖，
        # `git status` 里反复出现"只换了几个词的位置"的假改动（实测踩过）。
        ' '.join('`%s`' % n for n in sorted(c_only, key=lambda n: (-calls[n], n))),
        '',
        '迁移边界见[当前路线](../../architecture-roadmap.md)，不能按此词表重放战役或添加地图特例。',
        '',
    ]
    with open(DOC_OUT, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))

    print('章节 %d | tier %s | 方法 %d（完整 %.1f%%）| 钩子 %d'
          % (files, dict(chapters_by_tier), methods, 100.0 * complete / max(methods, 1), hooks))
    print('词表 %d 个 / %d 次 | tier A 用到 %d 个（独有 %d）| 仅 C %d 个'
          % (len(calls), sum(calls.values()), len(a_names), len(a_only), len(c_only)))
    print('报告: %s' % DOC_OUT)
    return 0


if __name__ == '__main__':
    sys.exit(main())
