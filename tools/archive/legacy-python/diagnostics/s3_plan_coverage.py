# -*- coding: utf-8 -*-
"""S3 JSON 导出覆盖统计；不把导出完整度当作原生 Campaign 的支持范围。

与 `s3_plan_inventory.py` 的分工：
  - inventory：调用**词表**层面（57 个名字、tier A 只用 9 个）；
  - 本脚本：**计划步骤**层面 —— 每个章节的 `battle_*` 方法里，有多少是"计划完整"的
    （`plan_complete=true` 表示导出器能把该方法体完整翻译成 JSON 规则），
    以及不完整的那些卡在什么调用上。

`s3_run_plan` 交由上游 execute_a_battle 调度实际 Campaign 方法。
JSON 未完整导出的方法仍由原生代码执行，本表只统计导出器能表达的范围。

输出：`data/s3_plan_coverage.json` + 屏幕摘要。
注意：**图内帧可识别性**是另一个前提（见 `s3_preflight.py`），本脚本只能离线统计计划和
已知夹具；没有夹具的图必须进图才知道，这一点在输出里明确标注为未知。
"""
import collections
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
CAMPAIGN = os.path.join(ROOT, 'data', 'campaign')
OUT = os.path.join(ROOT, 'data', 's3_plan_coverage.json')

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def stage_of(stem):
    parts = stem.split('_')
    if len(parts) >= 3 and parts[-2].isdigit() and parts[-1].isdigit():
        return '%s-%s' % (parts[-2], parts[-1])
    return None


def main():
    with open(os.path.join(HERE, 'map_fixtures.json'), encoding='utf-8') as f:
        fixtures = json.load(f)
    fixture_sources = {info['chapter'] for name, info in fixtures.items()
                       if not name.startswith('_')
                       and os.path.isfile(os.path.join(ROOT, 'data', 'fixtures', name))}
    rows = []
    by_tier = collections.Counter()
    tier_ready = collections.Counter()
    blocked_calls = collections.Counter()

    for path in glob.glob(os.path.join(CAMPAIGN, '**', '*.json'), recursive=True):
        stem = os.path.basename(path)[:-5]
        if stem.endswith('_base'):
            continue
        try:
            with open(path, encoding='utf-8') as f:
                ir = json.load(f)
        except Exception:
            continue
        camp = ir.get('campaign') or {}
        tier = camp.get('tier')
        stages = [b for b in (camp.get('battles') or [])
                  if str(b.get('method', '')).startswith('battle_')]
        complete = [b for b in stages if b.get('plan_complete')]
        incomplete = [b for b in stages if not b.get('plan_complete')]
        for b in incomplete:
            for c in (b.get('calls') or []):
                blocked_calls[c] += 1
        stage = stage_of(stem)
        by_tier[tier] += 1
        ready = bool(stages) and not incomplete
        if ready:
            tier_ready[tier] += 1
        rows.append({
            'chapter': 'campaign.' + os.path.relpath(path, CAMPAIGN)[:-5].replace(os.sep, '.'),
            'tier': tier, 'stage': stage,
            'methods': [b['method'] for b in stages],
            'methods_complete': [b['method'] for b in complete],
            'methods_incomplete': [b['method'] for b in incomplete],
            'ir_plan_complete': ready,
            'map_fixture': os.path.relpath(path, CAMPAIGN).replace(os.sep, '/') in fixture_sources,
            'runtime_verified': None,
        })

    with open(OUT, 'w', encoding='utf-8') as f:
        json.dump({'rows': rows,
                   'by_tier': dict(by_tier),
                   'ir_complete_by_tier': dict(tier_ready),
                   'calls_in_incomplete_methods': dict(blocked_calls.most_common(20))},
                  f, ensure_ascii=False, indent=2)

    total = len(rows)
    ready = [r for r in rows if r['ir_plan_complete']]
    with_fixture = [r for r in ready if r['map_fixture']]
    print('章节总数 %d' % total)
    print('按 tier：%s' % dict(by_tier))
    print('JSON 计划完整（不代表实战已验证）：%d（%.1f%%）%s'
          % (len(ready), 100.0 * len(ready) / max(total, 1), dict(tier_ready)))
    print('  其中已有图内帧夹具（本统计不执行识别）：%d -> %s'
          % (len(with_fixture), [r['chapter'] for r in with_fixture]))
    print('  其中图可识别性**未知**（需进图才能确认）：%d' % (len(ready) - len(with_fixture)))
    print('计划不完整的章节：%d' % (total - len(ready)))
    print('未完整导出方法中的调用频次（原生运行不受此限制）：')
    for call, n in collections.Counter(blocked_calls).most_common(8):
        print('   %-28s %d' % (call, n))
    print('输出：%s' % os.path.relpath(OUT, ROOT))
    return 0


if __name__ == '__main__':
    sys.exit(main())
