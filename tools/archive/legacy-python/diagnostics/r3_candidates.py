# -*- coding: utf-8 -*-
"""R3 准备：把"该先迁移哪些原生钩子"从印象变成**可核对的排序**。

路线 R3 的纪律是"先做高频、低耦合、可对拍的操作，不由某张地图是否失败决定"。
所以这里只做一件事：从 S0 冻结的关卡 IR（`data/campaign/**`）里统计
`campaign.native_overrides` 的覆盖情况，按覆盖章节数排序，产出 `docs/archive/reports/r3-candidates.md`。

同时**核对既有文档里的数字**：README 里写着"非 battle_* 的引擎钩子涉及 49 个关卡、
去重后仅 17 个方法"。这个脚本会独立数一遍并报差异 —— 数字对不上就是其中之一过期了
（要么文档旧了，要么我读 IR 的方式不对），两者都必须当场暴露，不能靠记忆。

用法：
    python tools/diagnostics/r3_candidates.py
"""

from __future__ import annotations

import json
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / 'data' / 'campaign'
DOCS = ROOT / 'docs'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# README 里记着的数字（用来自我核对，不是断言目标：对不上要报差异，不是改数字）
DOCUMENTED = {'chapters': 49, 'methods': 17}


def load_chapters():
    """返回 [(source, [hook...])]，按 source 排序。"""
    chapters = []
    for path in sorted(DATA.rglob('*.json')):
        try:
            document = json.loads(path.read_text(encoding='utf-8'))
        except Exception:
            continue
        campaign = document.get('campaign') or {}
        hooks = campaign.get('native_overrides') or []
        if not isinstance(hooks, list):
            continue
        chapters.append((str(document.get('source') or path.name), [str(h) for h in hooks]))
    return chapters


def main() -> int:
    if not DATA.is_dir():
        print(f'[跳过] 没有 {DATA.relative_to(ROOT)}（先跑 tools/export_upstream_data.py）')
        return 0
    chapters = load_chapters()
    if not chapters:
        print(f'**失败**：{DATA.relative_to(ROOT)} 下没读到任何关卡 IR')
        return 1

    by_hook = defaultdict(list)
    with_hooks = []
    for source, hooks in chapters:
        if hooks:
            with_hooks.append(source)
        for hook in hooks:
            by_hook[hook].append(source)

    ranked = sorted(by_hook.items(), key=lambda kv: (-len(kv[1]), kv[0]))
    print(f'=== R3 候选（覆盖 {len(chapters)} 章 IR）===')
    print(f'  有钩子的关卡: {len(with_hooks)}（文档记 {DOCUMENTED["chapters"]}）')
    print(f'  去重方法数  : {len(by_hook)}（文档记 {DOCUMENTED["methods"]}）')

    lines = [
        '# R3 迁移候选：原生钩子覆盖排序',
        '',
        '> 本页由 `tools/diagnostics/r3_candidates.py` 从 S0 关卡 IR（`data/campaign/**`）生成，**不手写**。',
        '> 排序依据是**覆盖章节数**（路线 R3：先做高频、低耦合、可对拍的，不由某张地图是否失败决定）。',
        '',
        f'覆盖 {len(chapters)} 章 IR；有钩子的关卡 {len(with_hooks)} 个；去重方法 {len(by_hook)} 个。',
        '',
        '## 排序（覆盖章节数 → 方法名）',
        '',
        '| # | 方法 | 覆盖章节 | 说明 |',
        '| --- | --- | --- | --- |',
    ]
    for index, (hook, sources) in enumerate(ranked, 1):
        sample = '、'.join(s.split('/')[-1].replace('.py', '') for s in sources[:3])
        more = f' 等 {len(sources)} 章' if len(sources) > 3 else ''
        lines.append(f'| {index} | `{hook}` | **{len(sources)}** | {sample}{more} |')

    lines += [
        '',
        '## 与文档既有数字的核对',
        '',
        f'| 项 | 本次独立数出 | README 记录 | 差异 |',
        '| --- | --- | --- | --- |',
        f'| 有钩子的关卡 | {len(with_hooks)} | {DOCUMENTED["chapters"]} | '
        f'{len(with_hooks) - DOCUMENTED["chapters"]:+d} |',
        f'| 去重方法数 | {len(by_hook)} | {DOCUMENTED["methods"]} | '
        f'{len(by_hook) - DOCUMENTED["methods"]:+d} |',
        '',
        '> 对不上的处置：先查是"文档旧了"还是"IR 读法不对"（例如 `native_overrides` 的定义变了），',
        '> 再决定改文档还是改脚本 —— 不要直接改数字把两边凑成一样。',
        '',
        '## 迁移前的门槛（路线 R3）',
        '',
        '每个候选迁移时都要带：上游调用轨迹、C# 结果轨迹、失败语义对拍；对拍不完整就继续走上游宿主。',
        '**不存在只对单张地图有效的阈值、坐标或路线** —— 单张地图只能作为现场样本。',
        '',
        '复现：`python tools/diagnostics/r3_candidates.py`。',
        '',
    ]
    DOCS.mkdir(parents=True, exist_ok=True)
    (DOCS / 'archive/reports/r3-candidates.md').write_text('\n'.join(lines), encoding='utf-8')

    print()
    for index, (hook, sources) in enumerate(ranked[:8], 1):
        print(f'  {index:2d}. {hook:34s} {len(sources):3d} 章')
    print(f'  … 共 {len(ranked)} 个方法')
    print()
    print('报告: docs/archive/reports/r3-candidates.md')
    mismatch = (len(with_hooks) != DOCUMENTED['chapters']
                or len(by_hook) != DOCUMENTED['methods'])
    print('结果: ' + ('OK（与文档数字一致）' if not mismatch
                    else 'ATTENTION（与文档数字不一致，差异已写进报告，先查原因）'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
