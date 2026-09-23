# -*- coding: utf-8 -*-
"""R2 活动域验收：活动章节清点（`kind = "event_state"`，纯离线）。

活动域的价值全在"清点得**准**"上，所以这里做的是**两个独立推导互相对拍**：
  A. C# 任务从 S0 契约里筛出来的活动章节数与分层（生产路径）
  B. 本脚本直接从同一份 `data/campaign_index.json` 里数一遍（独立实现）
两者必须一致 —— 否则不是"清点风格不同"，而是有一侧读错了契约。

另外验两条口径：
  * **"没有活动"是有效状态**：前缀匹配不到任何章节时记 `Succeeded` + `matched=0`，不是失败；
  * `only_complete=true` 时列出的条目必须真的都是计划完整的。

以及 `plan-queue` 这一段数据面：生成的必须是**普通队列文件**（计数与独立数一致、
任务项就是 campaign_batch、id 是可执行模块名），而且**拿它直接跑 dry-run 要能跑通** ——
生成物"看起来对"不算数，能被执行才算。

用法：
    python tools/diagnostics/verify_event_state.py
契约文件不存在时显式跳过（不静默通过）。
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DATA = ROOT / 'data'
sys.path.insert(0, str(ROOT / 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net10.0' / 'alashub.exe'
INDEX = DATA / 'campaign_index.json'
PREFIX = 'event_'


def folder(source):
    parts = str(source).replace('\\', '/').split('/')
    return parts[-2] if len(parts) >= 2 else str(source)


def load_index_chapters():
    """从契约里取出章节列表（兼容两种结构：顶层 chapters，或 catalog.campaign.chapters）。"""
    document = json.loads(INDEX.read_text(encoding='utf-8'))
    for candidate in (document.get('chapters'),
                      (document.get('campaign') or {}).get('chapters'),
                      (document.get('catalog') or {}).get('campaign', {}).get('chapters')):
        if isinstance(candidate, list) and candidate:
            return candidate
    # 兜底：找第一个"元素是含 source 字段的对象"的列表
    for value in document.values():
        if isinstance(value, list) and value and isinstance(value[0], dict) \
                and 'source' in value[0]:
            return value
    raise SystemExit('契约结构不认识：找不到章节列表')


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先运行 dotnet build）')
        return 1
    if not INDEX.is_file():
        print(f'[跳过] 没有 {INDEX.relative_to(ROOT)}（先跑 tools/export_upstream_data.py）；'
              f'活动域清点未跑。')
        return 0

    chapters = load_index_chapters()
    expected = [c for c in chapters if folder(c.get('source', '')).lower().startswith(PREFIX)]
    expected_complete = [c for c in expected if c.get('plan_complete') is True]
    expected_tiers = {}
    for chapter in expected:
        tier = chapter.get('tier') or '?'
        expected_tiers[tier] = expected_tiers.get(tier, 0) + 1
    print(f'=== 活动域清点（契约 {len(chapters)} 章，其中 {PREFIX}* {len(expected)} 章）===')

    failures: list[str] = []
    with TemporaryDirectory(prefix='alas-event-state-') as tmp:
        tmpdir = Path(tmp)
        queue_file = tmpdir / 'queue.json'
        queue_file.write_text(json.dumps({'tasks': [
            {'id': 'events-all', 'kind': 'event_state',
             'input': {'folder_prefix': PREFIX, 'only_complete': False, 'limit': 5}},
            {'id': 'events-complete', 'kind': 'event_state',
             'input': {'folder_prefix': PREFIX, 'only_complete': True, 'limit': 5}},
            {'id': 'events-none', 'kind': 'event_state',
             'input': {'folder_prefix': 'no_such_prefix_zzz'}},
            {'id': 'events-suffix', 'kind': 'event_state',
             'input': {'folder_prefix': '20260908_cn'}},
            {'id': 'events-invalid-prefix', 'kind': 'event_state',
             'input': {'folder_prefix': ''}},
            {'id': 'events-invalid-limit', 'kind': 'event_state',
             'input': {'limit': 0}},
            {'id': 'events-invalid-bool', 'kind': 'event_state',
             'input': {'only_complete': 'true'}},
            {'id': 'events-unknown-field', 'kind': 'event_state',
             'input': {'max_rounds': 3}},
        ]}, ensure_ascii=False, indent=1), encoding='utf-8')
        artifacts = tmpdir / 'artifacts'
        proc = subprocess.run([str(EXE), 'queue', '--file', str(queue_file),
                               '--artifacts', str(artifacts)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=300)
        if proc.returncode != 0:
            failures.append(f'alashub queue 退出码 {proc.returncode}')
            print((proc.stdout or '')[-1200:])
        run_dirs = sorted(p for p in artifacts.glob('*') if p.is_dir())
        if not run_dirs:
            print('**失败**：没有产出运行目录')
            return 1
        run_dir = run_dirs[-1]

        def evidence(task_id):
            path = run_dir / f'task-{task_id}.json'
            if not path.is_file():
                failures.append(f'缺少工件 task-{task_id}.json')
                return {}
            document = json.loads(path.read_text(encoding='utf-8'))
            if document.get('outcome') != 'succeeded':
                failures.append(f'{task_id} 结论 {document.get("outcome")}'
                                f'（{document.get("error")}）')
            return document.get('evidence') or {}

        all_events = evidence('events-all')
        complete_events = evidence('events-complete')
        none_events = evidence('events-none')
        suffix_events = evidence('events-suffix')

        checks = [
            ('章节总数一致', all_events.get('chapters_total') == len(chapters),
             f"C#={all_events.get('chapters_total')} 独立数={len(chapters)}"),
            ('活动章节数一致', all_events.get('matched') == len(expected),
             f"C#={all_events.get('matched')} 独立数={len(expected)}"),
            ('分层一致', (all_events.get('by_tier') or {}) == expected_tiers,
             f"C#={all_events.get('by_tier')} 独立数={expected_tiers}"),
            ('only_complete 只列完整计划',
             complete_events.get('matched') == len(expected_complete)
             and all(item.get('plan_complete') is True
                     for item in complete_events.get('listed') or []),
             f"C#={complete_events.get('matched')} 独立数={len(expected_complete)}"),
            ('列出的条目可直接执行',
             all('.' in str(item.get('chapter', ''))
                 for item in all_events.get('listed') or []),
             f"listed={all_events.get('listed')}"),
            ('没匹配到也算成功（有效状态）', none_events.get('matched') == 0,
             f"matched={none_events.get('matched')}"),
            ('目录筛选遵守前缀语义', suffix_events.get('matched') == 0,
             f"matched={suffix_events.get('matched')}"),
        ]
        for task_id in ('events-invalid-prefix', 'events-invalid-limit',
                        'events-invalid-bool', 'events-unknown-field'):
            path = run_dir / f'task-{task_id}.json'
            row = json.loads(path.read_text(encoding='utf-8')) if path.is_file() else {}
            checks.append((f'{task_id} 由前置条件跳过',
                           row.get('outcome') == 'skipped'
                           and row.get('error_kind') == 'none'
                           and bool(row.get('unmet_preconditions')),
                           f"outcome={row.get('outcome')} error={row.get('error')}"))
        for name, ok, detail in checks:
            print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
            if not ok:
                failures.append(f'{name}: {detail}')

        # ---- plan-queue：清点 → 生成队列（生成物必须是**普通队列文件**，且能直接跑）
        print()
        print('=== 清点 → 生成队列 ===')
        plan_out = tmpdir / 'planned.json'
        planned = subprocess.run([str(EXE), 'plan-queue', '--out', str(plan_out),
                                  '--only-complete', '--limit', '5'],
                                 capture_output=True, text=True, encoding='utf-8',
                                 errors='replace', timeout=120)
        plan_checks: list[tuple[str, bool, str]] = []
        if planned.returncode != 0 or not plan_out.is_file():
            plan_checks.append(('生成队列文件', False,
                                f'退出码={planned.returncode} {(planned.stdout or "")[-200:]}'))
        else:
            document = json.loads(plan_out.read_text(encoding='utf-8'))
            tasks = document.get('tasks') or []
            plan_checks += [
                ('生成队列文件', True, ''),
                ('任务数等于 limit', len(tasks) == 5, f'len={len(tasks)}'),
                ('计数与独立数一致',
                 document.get('matched') == len(expected_complete)
                 and document.get('chapters_total') == len(chapters),
                 f"matched={document.get('matched')} 独立={len(expected_complete)} "
                 f"total={document.get('chapters_total')} 独立={len(chapters)}"),
                ('任务是普通队列项',
                 all(t.get('kind') == 'campaign_batch' and t.get('required') is False
                     and t.get('input', {}).get('chapters') == [t.get('id')] for t in tasks),
                 f'tasks={tasks[:1]}'),
                ('任务 id 是可执行模块名',
                 all(str(t.get('id', '')).startswith('campaign.') for t in tasks),
                 f"ids={[t.get('id') for t in tasks]}"),
            ]
            # 生成物必须**真的能跑**：拿它跑一次 dry-run（无设备），证明章节是真实可加载的模块
            artifacts2 = tmpdir / 'planned-artifacts'
            executed = subprocess.run([str(EXE), 'queue', '--file', str(plan_out),
                                       '--artifacts', str(artifacts2)],
                                      capture_output=True, text=True, encoding='utf-8',
                                      errors='replace', timeout=600)
            out = executed.stdout or ''
            plan_checks.append(('生成的队列可直接执行（dry-run）',
                                executed.returncode == 0 and f'任务={len(tasks)}' in out
                                and '失败=0' in out,
                                f'退出码={executed.returncode} {out[-200:]}'))
            captured_out = tmpdir / 'planned-capture.json'
            captured = subprocess.run([str(EXE), 'plan-queue', '--out', str(captured_out),
                                       '--only-complete', '--limit', '2', '--capture-after'],
                                      capture_output=True, text=True, encoding='utf-8',
                                      errors='replace', timeout=120)
            captured_tasks = (json.loads(captured_out.read_text(encoding='utf-8'))['tasks']
                              if captured.returncode == 0 and captured_out.is_file() else [])
            plan_checks.append(('每关后可生成同会话抓帧任务',
                                len(captured_tasks) == 4 and all(
                                    captured_tasks[i + 1]['kind'] == 'account_state'
                                    and captured_tasks[i + 1]['input'] == {'capture': True}
                                    and captured_tasks[i + 1]['required'] is True
                                    and captured_tasks[i + 1]['id'] == captured_tasks[i]['id'] + ':post'
                                    for i in (0, 2)), f'tasks={captured_tasks}'))
            captured_document = (json.loads(captured_out.read_text(encoding='utf-8'))
                                 if captured.returncode == 0 and captured_out.is_file() else {})
            plan_checks.append(('capture-after 默认 dry-run 保持 dry_run 且抓帧任务需授权',
                                captured_document.get('dry_run') is True
                                and captured_document.get('capture_after') is True
                                and all(t.get('required') is True for t in captured_tasks[1::2]),
                                f"dry_run={captured_document.get('dry_run')} "
                                f"capture_after={captured_document.get('capture_after')}"))

            # 规划参数必须在 CLI 边界拒绝无效值；否则 `Math.Max(1, limit)` 会把
            # "不要生成任务" 静默变成一关，0/负数的轮次和时间也会写入不可执行队列。
            invalid_cases = [
                ('limit=0', ['--limit', '0'], 'limit'),
                ('limit<0', ['--limit', '-2'], 'limit'),
                ('limit 非数字', ['--limit', 'nope'], 'limit'),
                ('max-rounds=0', ['--max-rounds', '0'], 'max-rounds'),
                ('max-seconds=0', ['--max-seconds', '0'], 'max-seconds'),
                ('max-seconds 非数字', ['--max-seconds', 'nope'], 'max-seconds'),
            ]
            for name, option_args, option in invalid_cases:
                invalid_out = tmpdir / f'invalid-{name.replace("=", "-").replace("<", "lt").replace(" ", "-")}.json'
                invalid = subprocess.run(
                    [str(EXE), 'plan-queue', '--data', str(DATA), '--out', str(invalid_out),
                     '--only-complete', *option_args],
                    capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=120)
                combined = (invalid.stdout or '') + (invalid.stderr or '')
                plan_checks.append((f'拒绝无效 {name}',
                                    invalid.returncode == 2 and not invalid_out.exists()
                                    and option in combined,
                                    f'退出码={invalid.returncode} 输出={combined[-240:]}'))
        for name, ok, detail in plan_checks:
            print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
            if not ok:
                failures.append(f'{name}: {detail}')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（清点与独立数一致、only_complete 语义正确、没活动算有效状态）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
