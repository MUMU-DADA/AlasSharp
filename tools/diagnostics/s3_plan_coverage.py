# -*- coding: utf-8 -*-
"""S3 计划可用性统计：哪些章节**现在就能按计划跑**，哪些还需要补工。

与 `s3_plan_inventory.py` 的分工：
  - inventory：调用**词表**层面（57 个名字、tier A 只用 9 个）；
  - 本脚本：**计划步骤**层面 —— 每个章节的 `battle_*` 方法里，有多少是"计划完整"的
    （`plan_complete=true` 表示导出器能把该方法体完整翻译成 JSON 规则），
    以及不完整的那些卡在什么调用上。

为什么要它：`s3_run_plan` 执行的是 `battle_*` 方法序列，所以"能跑"的前提是
**这些方法的计划是完整的**；只统计调用词表会高估可跑范围。

输出：`data/s3_plan_coverage.json` + 屏幕摘要（含"现在就能跑"的章节清单）。
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

# 已验证可检出的图内帧（与 s3_preflight.py 保持一致）
KNOWN_FIXTURES = {'2-1', '10-4', '困难1-4', '活动图'}
KNOWN_UNSUPPORTED = {'1-1'}

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
        # "现在就能跑"：所有 battle_* 方法的计划都完整，且图不是已知不支持
        ready = bool(stages) and not incomplete and (stage not in KNOWN_UNSUPPORTED)
        if ready:
            tier_ready[tier] += 1
        rows.append({
            'chapter': stem, 'tier': tier, 'stage': stage,
            'methods': [b['method'] for b in stages],
            'methods_complete': [b['method'] for b in complete],
            'methods_incomplete': [b['method'] for b in incomplete],
            'ready': ready,
            'map_fixture': stage in KNOWN_FIXTURES,
            'map_known_unsupported': stage in KNOWN_UNSUPPORTED,
        })

    with open(OUT, 'w', encoding='utf-8') as f:
        json.dump({'rows': rows,
                   'by_tier': dict(by_tier),
                   'ready_by_tier': dict(tier_ready),
                   'blocked_calls': dict(blocked_calls.most_common(20))},
                  f, ensure_ascii=False, indent=2)

    total = len(rows)
    ready = [r for r in rows if r['ready']]
    with_fixture = [r for r in ready if r['map_fixture']]
    print('章节总数 %d' % total)
    print('按 tier：%s' % dict(by_tier))
    print('**计划全完整**（现在就能按计划跑）：%d（%.1f%%）%s'
          % (len(ready), 100.0 * len(ready) / max(total, 1), dict(tier_ready)))
    print('  其中**图内帧已验证可识别**：%d -> %s'
          % (len(with_fixture), [r['chapter'] for r in with_fixture]))
    print('  其中图可识别性**未知**（需进图才能确认）：%d' % (len(ready) - len(with_fixture)))
    print('计划不完整的章节：%d' % (total - len(ready)))
    print('卡住最多的调用（计划不完整的方法里出现次数）：')
    for call, n in collections.Counter(blocked_calls).most_common(8):
        print('   %-28s %d' % (call, n))
    print('输出：%s' % os.path.relpath(OUT, ROOT))
    return 0


if __name__ == '__main__':
    sys.exit(main())
