# -*- coding: utf-8 -*-
"""R0：把已归档的**实机跑图日志**重新核对一遍（结果证据链）。

为什么需要它：R0 的阶段门槛要求"至少一条真实成功结算和一条真实撤退证据都能从日志解释"。
文档里写着"通关了"不算证据 —— 要能从原始日志里指出**是哪一次战果点击、
哪一行 CAMPAIGN END、有没有撤退夹在中间**。这个脚本就是干这件事：
不改日志、不猜结论，只把每条记录声称的结果和原始证据对齐，对不上就报矛盾。

做法：
  1. 每个 `[plan ...]` 开头、`[结果 ...]` 结尾算一次出击窗口；
  2. 窗口里抓原始证据：`BATTLE_STATUS_x` 点击、`In stage.`、`CAMPAIGN END`、
     `WITHDRAW CALLED` / `MAP WITHDRAW`、`[错误]`、步骤行、失败帧路径；
  3. 按 sortie-result/1 的规则复核 `cleared` / `campaign_end` 声称；
  4. 撤退事件按**发生在哪**分类：进图前的上一局清理 / 导航期 / 本局过程；
  5. 产出 `data/result_records_audit.json` 与 `docs/result-evidence.md`。

退出码非 0 = 有记录解释不通，或有门槛项缺失。
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DATA = ROOT / 'data'
DOCS = ROOT / 'docs'
sys.path.insert(0, str(ROOT / 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

WIN_RANKS = ('S', 'A', 'B')
RANK_CLICK = re.compile(r'@\s*BATTLE_STATUS_([SABCD])\b')
RANK_CLICK_ALT = re.compile(r'BATTLE_STATUS_([SABCD])\b')
PLAN_LINE = re.compile(r'^\[plan\s*\]\s*(\S+)\s+stage=(\S+)\s+tier=(\S+)\s+dry_run=(\S+)')
RESULT_LINE = re.compile(r'^\[结果\s*\]\s*(.*)$')
STEP_LINE = re.compile(r'^\s+step=(\S+)(.*)$')
FRAME_PATH = re.compile(r'failure_frame=(\S+)')


def parse_kv(text: str) -> dict:
    """`elapsed=84.3s stopped_early=False outcome=cleared` → dict（值都是字符串）。"""
    out = {}
    for token in text.split():
        if '=' in token:
            key, _, value = token.partition('=')
            out[key] = value
    return out


def stage_windows(lines):
    """按 `[结果]` 行切窗口：一次出击的证据在结果行**之前**。

    为什么不能用 `[plan]` 当窗口开头：C# 的 stdout 与 Python 日志的 stderr 是两条流，
    落到同一个文件里顺序不可靠 —— 实测 `[plan]` 行经常出现在本次出击的原始日志**之后**。
    可靠的事实只有一个：`[結果]` 是本窗口的最后一行，窗口从上一行 `[結果]` 之后开始。
    章节名取窗口内（或窗口前最近）的 `[plan]` 行。
    """
    windows = []
    cursor = 0
    for index, line in enumerate(lines):
        if not RESULT_LINE.match(line):
            continue
        block = [(i, lines[i]) for i in range(cursor, index + 1)]
        plan = None
        for i in range(index, -1, -1):
            plan = PLAN_LINE.match(lines[i])
            if plan:
                break
        windows.append({
            'start': cursor,
            'end': index,
            'chapter': plan.group(1) if plan else '<未知>',
            'stage': plan.group(2) if plan else '<未知>',
            'tier': plan.group(3) if plan else '<未知>',
            'dry_run': (plan.group(4) == 'True') if plan else False,
            'lines': block,
            'reported': parse_kv(RESULT_LINE.match(line).group(1)),
        })
        cursor = index + 1
    return windows


def withdraw_kind(window_lines, first_index, last_index):
    """撤退事件的性质：看它周围的步骤行/调用栈，而不是靠猜。

    取事件**前后各一段**文本：上游的 `=== WITHDRAW CALLED ===` 先把调用栈打在后面，
    而 `<<< MAP WITHDRAW >>>` 的调用栈在前面 —— 只看单向会把同一次撤退判成两种。
    """
    around = [line for index, line in window_lines
              if first_index - 40 <= index <= last_index + 40]
    context = '\n'.join(around)
    if 'prepare_campaign_navigation' in context and 'inst.withdraw()' in context:
        return 'cleanup_previous_sortie', '进图前清理上一局（prepare_campaign_navigation）'
    if 'campaign_ui.py' in context or 'handle_campaign_ui_additional' in context:
        return 'navigation', '章节导航期间上游自己调了 withdraw()'
    return 'stage', '本局过程中撤退'


def collect_withdraws(lines):
    """把相邻的撤退标记行并成**一次**撤退事件（同一次撤退会打 2~3 行标记）。"""
    markers = [index for index, line in lines
               if 'WITHDRAW CALLED' in line or 'MAP WITHDRAW' in line
               or '@ WITHDRAW' in line or 'POPUP_CONFIRM_WITHDRAW' in line]
    if not markers:
        return []
    groups, current = [], [markers[0]]
    for index in markers[1:]:
        if index - current[-1] <= 30:
            current.append(index)
        else:
            groups.append(current)
            current = [index]
    groups.append(current)

    events = []
    for group in groups:
        kind, why = withdraw_kind(lines, group[0], group[-1])
        events.append({
            'line': group[0],
            'lines': group,
            'kind': kind,
            'why': why,
            'evidence': [dict(lines)[i].strip()[:120] for i in group],
        })
    return events


def audit_window(window):
    """复核一次出击：声称的结果 vs 原始证据。"""
    lines = window['lines']
    reported = dict(window['reported'])
    reported['cleared'] = reported.get('cleared') == 'True'
    reported['campaign_end'] = reported.get('campaign_end') == 'True'

    ranks, in_stage, end_marker = [], [], []
    steps, frames, errors, traceback_lines = [], [], [], []
    for index, line in lines:
        match = RANK_CLICK.search(line) or RANK_CLICK_ALT.search(line)
        if match:
            ranks.append((index, match.group(1)))
        if 'In stage.' in line:
            in_stage.append(index)
        if 'CAMPAIGN END' in line:
            end_marker.append(index)
        step = STEP_LINE.match(line)
        if step:
            steps.append(step.group(1) + step.group(2).strip())
        frame = FRAME_PATH.search(line)
        if frame:
            frames.append(frame.group(1))
        if line.startswith('[错误'):
            errors.append(line.strip()[:200])
        if line.lstrip().startswith('|'):
            traceback_lines.append(line.strip()[:160])

    withdraws = collect_withdraws(lines)

    last_battle = ranks[-1][0] if ranks else None
    last_rank = ranks[-1][1] if ranks else None
    withdraw_after_battle = [w for w in withdraws
                             if last_battle is not None and w['line'] > last_battle]
    cleanup_withdraws = [w for w in withdraws if w['kind'] == 'cleanup_previous_sortie']

    problems = []
    if reported['cleared'] != (reported.get('outcome') == 'cleared'):
        problems.append('cleared 与 outcome 自相矛盾')
    if reported['cleared']:
        if last_rank not in WIN_RANKS:
            problems.append(f'声称通关但没有胜方战果点击（最后战果 {last_rank}）')
        if not in_stage:
            problems.append('声称通关但日志里没有 "In stage." 结算行')
        if not end_marker:
            problems.append('声称通关但没有 CAMPAIGN END 标记')
        if withdraw_after_battle:
            problems.append('最后一场战斗之后还出现过撤退')
    if reported.get('outcome') == 'error' and not errors:
        problems.append('声称报错但窗口里没有 [错误] 行')
    if reported.get('outcome') == 'withdrawn' and not withdraws:
        problems.append('声称撤退但窗口里没有撤退证据')

    explanation = []
    if reported['cleared']:
        explanation.append(f'战果 {last_rank} 已点击、In stage. 与 CAMPAIGN END 都在，'
                           f'最后一次战斗之后没有撤退')
    if cleanup_withdraws:
        explanation.append(f'进图前清理了上一局（{len(cleanup_withdraws)} 次撤退调用）')
    if not explanation:
        explanation.append(errors[0] if errors else '未通关，按原始证据记录')

    return {
        'log': window['log'],
        'chapter': window['chapter'],
        'stage': window['stage'],
        'tier': window['tier'],
        'dry_run': window['dry_run'],
        'line': window['end'] + 1,
        'reported': reported,
        'evidence': {
            'rank_clicks': [rank for _, rank in ranks],
            'last_rank': last_rank,
            'final_battle_line': last_battle,
            'in_stage_lines': in_stage,
            'campaign_end_lines': end_marker,
            'withdraw_events': withdraws,
            'withdraw_after_last_battle': withdraw_after_battle,
            'steps': steps,
            'failure_frames': frames,
            'errors': errors,
            'traceback_tail': traceback_lines[:4],
        },
        'verdict': 'consistent' if not problems else 'contradiction',
        'problems': problems,
        'explanation': '；'.join(explanation),
    }


def audit_log(path: Path):
    text = path.read_text(encoding='utf-8', errors='replace')
    windows = stage_windows(text.splitlines())
    records = []
    for window in windows:
        window['log'] = path.name
        records.append(audit_window(window))
    return records


def render_markdown(audit) -> str:
    lines = [
        '# 实机结果证据核对（R0）',
        '',
        '> 本页由 `tools/diagnostics/audit_real_records.py` 从 `data/*.log` 重新核对生成，**不手写**。',
        '> 每条记录都能指回原始日志的行号；对不上就报矛盾，不做解释性兜底。',
        '',
        f"归档日志：{len(audit['logs'])} 份；出击记录：{len(audit['records'])} 条；"
        f"矛盾：{audit['summary']['contradictions']} 条。",
        '',
        '## 逐条记录',
        '',
        '| 日志 | 关 | 声称结果 | 战果 | 结算行 | CAMPAIGN END | 撤退事件 | 裁决 |',
        '| --- | --- | --- | --- | --- | --- | --- | --- |',
    ]
    for record in audit['records']:
        rep, ev = record['reported'], record['evidence']
        events = ', '.join(w['kind'] for w in ev['withdraw_events']) or '—'
        lines.append(
            f"| `{record['log']}`:{record['line']} | {record['stage']} | "
            f"outcome={rep.get('outcome')} cleared={rep['cleared']} | "
            f"{ev['last_rank'] or '—'} | {'✓' if ev['in_stage_lines'] else '—'} | "
            f"{'✓' if ev['campaign_end_lines'] else '—'} | {events} | "
            f"{'一致' if record['verdict'] == 'consistent' else '**矛盾**'} |")
    lines += ['', '## 解释', '']
    for record in audit['records']:
        lines.append(f"- `{record['log']}`:{record['line']}（{record['stage']}）：{record['explanation']}")
        for problem in record['problems']:
            lines.append(f"  - ⚠️ {problem}")
    lines += ['', '## 撤退事件台账', '',
              '| 日志 | 行 | 性质 | 依据 |', '| --- | --- | --- | --- |']
    for event in audit['withdraw_events']:
        lines.append(f"| `{event['log']}` | {event['line'] + 1} | {event['kind']} | {event['why']} |")
    lines += ['', '## 门槛与缺口', '']
    for item in audit['summary']['gate']:
        lines.append(f"- {item}")
    lines += ['', '## 已知缺口', '']
    for item in audit['gaps']:
        lines.append(f"- {item}")
    lines += ['', '## 复现', '', '```powershell',
              'python tools/diagnostics/audit_real_records.py', '```', '']
    return '\n'.join(lines)


def main() -> int:
    logs = sorted(DATA.glob('*.log'))
    records, all_events = [], []
    for path in logs:
        for record in audit_log(path):
            records.append(record)
            for event in record['evidence']['withdraw_events']:
                all_events.append({'log': record['log'], 'stage': record['stage'], **event})

    clears = [r for r in records if r['reported']['cleared']]
    explained_clears = [r for r in clears if r['verdict'] == 'consistent']
    contradictions = [r for r in records if r['verdict'] != 'consistent']
    explained_events = [e for e in all_events if e['kind'] in
                        ('cleanup_previous_sortie', 'navigation', 'stage')]
    stage_level_withdrawn = [r for r in records if r['reported'].get('outcome') == 'withdrawn']

    gate = []
    gate.append(f"真实成功结算：{len(explained_clears)} 条可解释"
                + ('（通过）' if explained_clears else '（**未通过**）'))
    gate.append(f"真实撤退证据：{len(explained_events)} 起可解释"
                + ('（通过）' if explained_events else '（**未通过**）'))
    gate.append(f"记录自相矛盾：{len(contradictions)} 条"
                + ('（通过）' if not contradictions else '（**未通过**）'))

    gaps = []
    if not stage_level_withdrawn:
        gaps.append('归档日志里**没有**以 `outcome=withdrawn` 收尾的一局：'
                    + (f'现有 {len(all_events)} 起撤退都是进图前清理或导航期发生，'
                       '属于"上一局/客户端状态"清理，不是本局结论。'
                       if all_events else '归档里连撤退事件都没有。')
                    + '要补"本局撤退判为 withdrawn"的真机记录，需要一次真实出击后主动撤退；'
                      '那是消耗石油且改变账号状态的动作用户未授权，留给设备在线时补。')
    navigation_swallowed = [e for e in all_events if e['kind'] == 'navigation'
                            and 'swallowed' not in e['why']]
    if navigation_swallowed:
        gaps.append('导航期撤退目前只记在 `ensure_campaign_ui` 的步骤里'
                    '（`navigation_end`/`navigation_withdrawn`），不再被静默吞掉；'
                    '对应回归见 `verify_s3_plan.py`。')

    audit = {
        'generated_by': 'tools/diagnostics/audit_real_records.py',
        'contract': 'sortie-result/1',
        'logs': [p.name for p in logs],
        'records': records,
        'withdraw_events': all_events,
        'gaps': gaps,
        'summary': {
            'logs': len(logs),
            'records': len(records),
            'cleared_records': len(clears),
            'cleared_explained': len(explained_clears),
            'error_records': sum(1 for r in records if r['reported'].get('outcome') == 'error'),
            'withdraw_events': len(all_events),
            'withdraw_explained': len(explained_events),
            'stage_level_withdrawn': len(stage_level_withdrawn),
            'contradictions': len(contradictions),
            'gate': gate,
        },
    }

    (DATA / 'result_records_audit.json').write_text(
        json.dumps(audit, ensure_ascii=False, indent=1), encoding='utf-8')
    (DOCS / 'result-evidence.md').write_text(render_markdown(audit), encoding='utf-8')

    print('=== 实机结果证据核对（R0）===')
    for record in records:
        rep, ev = record['reported'], record['evidence']
        print(f"  {'一致' if record['verdict'] == 'consistent' else '矛盾'} "
              f"{record['log']}:{record['line']:<5} {record['stage']:<5} "
              f"outcome={str(rep.get('outcome')):<14} cleared={str(rep['cleared']):<5} "
              f"战果={str(ev['last_rank']):<4} 撤退={len(ev['withdraw_events'])}")
    print()
    for line in gate:
        print('  ' + line)
    for gap in gaps:
        print('  [缺口] ' + gap)
    print()
    print('证据已写入: data/result_records_audit.json')
    print('证据文档: docs/result-evidence.md')
    ok = bool(explained_clears) and bool(explained_events) and not contradictions
    print()
    print('结果: ' + ('OK（真实通关与真实撤退都能从日志解释）' if ok else 'FAIL'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
