# -*- coding: utf-8 -*-
"""R2 运行报告验收：工件 → 结构化事实 + 证据完整性检查。

`alashub report` 要回答两件事，本脚本分别在**真实产生的运行目录**上核对：

1. **这次运行到底发生了什么**：队列/批次结论、逐任务与逐关卡结论、日志计数、
   宿主与设备初始化次数 —— 全部来自工件，报告不重判通关。
2. **证据链全不全**：引用了却不在的工件、缺会话日志、结果里的合同违例，
   都要变成机器可读的 findings。这里**故意删工件**来验证它真的会报。

运行本身用真实 CLI 产生（`queue --file`，dry-run，无设备）：一个战役 dry-run 任务
（读上游规则 + 写批次工件）+ 一个账号状态只读任务（用真机存盘帧）。
"""
from __future__ import annotations

import json
import os
import shutil
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
CHAPTER = 'campaign.campaign_main.campaign_2_1'
FRAME = '_boss122.png'          # 真机地图帧（账号状态任务用）


def run(args, timeout=300, cwd=None):
    return subprocess.run(args, capture_output=True, text=True, encoding='utf-8',
                          errors='replace', timeout=timeout, cwd=cwd)


def verify_stub_request_visibility() -> int:
    """Exercise queue artifacts and report loading with the existing offline stub host."""
    import html
    import report_html
    from verify_runtime import dry_run_document

    with TemporaryDirectory(prefix='alas-report-stub-') as tmp:
        root = Path(tmp)
        request = {'id': 'dry-rules', 'kind': 'campaign_batch', 'required': True,
                   'input': {'chapters': [CHAPTER], 'max_rounds': 7,
                             'stop_on_failure': False}}
        fixture = root / 'fixture.json'
        fixture.write_text(json.dumps({'cases': [{
            'name': 'request-visibility', 'dry_run': True, 'artifacts': True,
            'tasks': [{**request, 'documents': {CHAPTER: dry_run_document(CHAPTER)}}],
        }]}, ensure_ascii=False), encoding='utf-8')
        produced = run([str(EXE), 'selftest-runtime', '--fixture', str(fixture),
                        '--workspace', str(root)])
        runs = sorted(root.glob('queue-*/*'))
        if produced.returncode != 0 or len(runs) != 1:
            print('FAIL 离线替身队列未产出单次运行目录')
            print(f'  runs={[path.name for path in runs]}')
            print((produced.stdout or produced.stderr)[-1500:])
            return 1
        run_dir = runs[0]
        queue = json.loads((run_dir / 'queue.json').read_text(encoding='utf-8'))
        index_request = {key: queue['tasks'][0][key]
                         for key in ('id', 'kind', 'required', 'input')}

        def report_of(directory: Path, name: str) -> dict:
            output = root / name
            result = run([str(EXE), 'report', '--run', str(directory), '--json', str(output)])
            if result.returncode != 0 or not output.is_file():
                raise RuntimeError((result.stdout or result.stderr)[-1500:])
            return json.loads(output.read_text(encoding='utf-8'))

        report = report_of(run_dir, 'report.json')
        task = next(item for item in report['items'] if item.get('level') == 'task')
        old_dir = root / 'old-run'
        shutil.copytree(run_dir, old_dir)
        old_queue = old_dir / 'queue.json'
        old_document = json.loads(old_queue.read_text(encoding='utf-8'))
        old_summary = old_document['tasks'][0]
        old_summary.pop('required')
        old_summary.pop('input')
        old_summary['artifact'] = str(old_dir / Path(old_summary['artifact']).name)
        old_queue.write_text(json.dumps(old_document), encoding='utf-8')
        old_task = next(item for item in report_of(old_dir, 'old-report.json')['items']
                        if item.get('level') == 'task')
        (old_dir / 'task-dry-rules.json').unlink()
        missing_report = report_of(old_dir, 'missing-report.json')
        missing_task = next(item for item in missing_report['items']
                            if item.get('level') == 'task')
        rendered_tasks = report_html.render(report).split('<h2 id="tasks">', 1)[1].split('</table>', 1)[0]
        missing_tasks = report_html.render(missing_report).split('<h2 id="tasks">', 1)[1].split('</table>', 1)[0]
        checks = [
            ('队列摘要保留非默认请求', index_request == request),
            ('报告保留完整输入', task.get('input') == request['input']
             and task.get('required') is True),
            ('旧队列从任务工件补读', old_task.get('input') == request['input']
             and old_task.get('required') is True),
            ('旧任务工件缺失时不补造输入',
             'input' not in missing_task and 'required' not in missing_task
             and any(f['code'] == 'missing_artifact' for f in missing_report['findings'])),
            ('HTML 任务表保留完整非默认输入',
             html.escape(json.dumps(request['input'], ensure_ascii=False, indent=2))
             in rendered_tasks),
            ('旧工件缺失时 HTML 显示未记录',
             '未记录' in missing_tasks and '查看输入' not in missing_tasks),
        ]
        print('=== 离线替身宿主：任务请求可见性 ===')
        for name, ok in checks:
            print(f"  {'ok  ' if ok else 'FAIL'} {name}")
        return 0 if all(ok for _, ok in checks) else 1


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先运行 dotnet build）')
        return 1
    if verify_stub_request_visibility() != 0:
        return 1
    if '--stub-only' in sys.argv:
        return 0

    failures: list[str] = []
    with TemporaryDirectory(prefix='alas-report-') as tmp:
        tmpdir = Path(tmp)
        tasks = [{'id': 'dry-rules', 'kind': 'campaign_batch', 'required': True,
                  'input': {'chapters': [CHAPTER], 'max_rounds': 7,
                            'stop_on_failure': False}}]
        frame = DATA / FRAME
        if frame.is_file():
            tasks.append({'id': 'state-now', 'kind': 'account_state',
                          'input': {'screenshot': str(frame.resolve())}})
        else:
            print(f'[提示] 没有 {FRAME}，账号状态任务不参与本次运行（只验战役 dry-run）')
        queue_file = tmpdir / 'queue.json'
        queue_file.write_text(json.dumps({'tasks': tasks}, ensure_ascii=False, indent=1),
                              encoding='utf-8')

        artifacts = tmpdir / 'artifacts'
        produced = run([str(EXE), 'queue', '--file', str(queue_file), '--artifacts', str(artifacts)])
        if produced.returncode != 0:
            failures.append(f'queue 退出码 {produced.returncode}')
            print(produced.stdout[-1500:])
        run_dirs = sorted(p for p in artifacts.glob('*') if p.is_dir())
        if not run_dirs:
            print('**失败**：没有产出运行目录')
            print((produced.stdout or produced.stderr)[-1500:])
            return 1
        run_dir = run_dirs[-1]
        # 反例用的副本要在**任何改动之前**复制：否则第二个反例会读到第一个反例改坏的状态，
        # 报出叠加的 findings（曾经如此），让"缺日志"这条断言看不出真正的原因。
        no_log = tmpdir / 'broken-no-log'
        shutil.copytree(run_dir, no_log)

        # ---- 正常路径：报告要能把这些事实读出来
        report_json = tmpdir / 'report.json'
        reported = run([str(EXE), 'report', '--run', str(run_dir), '--json', str(report_json)])
        if reported.returncode != 0:
            failures.append(f'report 退出码 {reported.returncode}')
        for line in (reported.stdout or '').strip().splitlines():
            print('  ' + line)
        if not report_json.is_file():
            print('**失败**：报告没有写出 --json')
            return 1
        report = json.loads(report_json.read_text(encoding='utf-8'))
        queue_document = json.loads((run_dir / 'queue.json').read_text(encoding='utf-8'))
        queue_requests = queue_document['tasks']
        report_tasks = [item for item in report['items'] if item['level'] == 'task']
        expected_requests = [
            {'id': task['id'], 'kind': task['kind'],
             'required': task.get('required', False), 'input': task.get('input')}
            for task in tasks]

        checks = [
            ('任务数', report['totals']['tasks'] == len(tasks),
             f"期望 {len(tasks)} 实为 {report['totals']['tasks']}"),
            ('宿主只起一次', report['host_start_count'] == 1,
             f"host_start_count={report['host_start_count']}"),
            ('dry_run 标记', report['dry_run'] is True, f"dry_run={report['dry_run']}"),
            ('证据完整', report['evidence_complete'] is True,
             f"findings={[f['code'] for f in report['findings']]}"),
            ('无 run_not_found', not any(f['code'] == 'run_not_found' for f in report['findings']), ''),
            ('日志条目 > 0', report['totals']['log_entries'] > 0,
             f"log_entries={report['totals']['log_entries']}"),
            ('有任务条目', any(i['level'] == 'task' for i in report['items']),
             f"items={[i['level'] for i in report['items']]}"),
            ('有工件计数', report['files'] >= 3, f"files={report['files']}"),
            ('队列结论', report['queue_outcome'] in ('dry_run', 'succeeded', 'partial'),
             f"queue_outcome={report['queue_outcome']}"),
            ('队列摘要逐任务保留完整请求',
             all({key: item[key] for key in ('id', 'kind', 'required', 'input')} == expected
                 for item, expected in zip(queue_requests, expected_requests))
             and len(queue_requests) == len(tasks),
             f"requests={queue_requests}"),
            ('报告逐任务显示原始输入',
             all(item.get('input') == task['input']
                 and item.get('required') == task.get('required', False)
                 for item, task in zip(report_tasks, tasks))
             and len(report_tasks) == len(tasks),
             f"items={report_tasks}"),
        ]
        if frame.is_file():
            checks.append(('账号状态任务成功',
                           report['totals']['tasks_succeeded'] >= 1,
                           f"tasks_succeeded={report['totals']['tasks_succeeded']}"))
        # 边界快照要落到数据面（否则前端看不到"任务从什么画面开始"，跨任务复位无从判断）
        ran_tasks = [i for i in report['items'] if i['level'] == 'task' and not i.get('skipped')]
        boundary_total = (report['totals'].get('boundaries_with_frame', 0)
                          + report['totals'].get('boundaries_without_frame', 0))
        checks += [
            ('每个跑过的任务都有边界快照',
             bool(ran_tasks) and all('boundary_state' in i for i in ran_tasks),
             f"ran={[i['id'] for i in ran_tasks]}"),
            ('边界快照统计与任务数一致',
             boundary_total == len(ran_tasks),
             f"统计={boundary_total} 任务={len(ran_tasks)}"),
            # 停止信息与日志来源都要上数据面：只给 outcome，前端看不出"为什么停、谁在说话"
            ('暴露提前停止与原因字段',
             'stopped_early' in report and 'stop_reason' in report,
             f"keys={[k for k in ('stopped_early', 'stop_reason') if k in report]}"),
            ('日志 scope 分布且与总条数一致',
             bool(report['totals'].get('log_scopes'))
             and sum(report['totals']['log_scopes'].values()) == report['totals']['log_entries'],
             f"scopes={report['totals'].get('log_scopes')} entries={report['totals']['log_entries']}"),
        ]
        print()
        print('=== 正常路径（报告读得出事实）===')
        for name, ok, detail in checks:
            print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
            if not ok:
                failures.append(f'{name}: {detail}')

        replay_root = tmpdir / 'replay-artifacts'
        replayed = run([str(EXE), 'queue', '--file', str(run_dir / 'queue.json'),
                        '--artifacts', str(replay_root)])
        replay_runs = sorted(p for p in replay_root.glob('*') if p.is_dir())
        replay_requests = (json.loads((replay_runs[-1] / 'queue.json').read_text(encoding='utf-8'))['tasks']
                           if replay_runs else [])
        replay_ok = (replayed.returncode == 0 and len(replay_runs) == 1
                     and all({key: item[key] for key in ('id', 'kind', 'required', 'input')} == expected
                             for item, expected in zip(replay_requests, expected_requests))
                     and len(replay_requests) == len(tasks))
        print(f"  {'ok  ' if replay_ok else 'FAIL'} queue.json 回喂仍保留非默认输入")
        if not replay_ok:
            failures.append(f'queue.json 回喂丢失请求: rc={replayed.returncode} tasks={replay_requests}')

        # 合法 JSON 也可能含错误类型；报告应留 finding，不能直接抛异常。
        print()
        print('=== 反例：工件和日志的 JSON 类型错误 ===')
        for case_number, (label, artifact_name, content) in enumerate((
            ('queue.json 顶层数组', 'queue.json', '[]'),
            ('queue.json 任务条目为数值', 'queue.json', '{"outcome":"succeeded","tasks":[42]}'),
            ('index.json 关卡条目为数值', 'index.json', '{"outcome":"dry_run","stages":[42]}'),
            ('任务工件 id 为数值', 'task-dry-rules.json', '{"id":42}'),
            ('单关结果 result 为数组', 'sortie-2-1.json', '{"result":[]}'),
            ('日志行顶层数组', 'session-log.jsonl', '[]\n'),
            ('日志 level 为数组', 'session-log.jsonl', '{"level":[]}\n'),
        ), start=1):
            wrong_shape = tmpdir / f'wrong-shape-{case_number}'
            shutil.copytree(run_dir, wrong_shape)
            (wrong_shape / artifact_name).write_text(content, encoding='utf-8')
            wrong_json = tmpdir / (wrong_shape.name + '.json')
            wrong_result = run([str(EXE), 'report', '--run', str(wrong_shape),
                                '--json', str(wrong_json)])
            wrong_report = (json.loads(wrong_json.read_text(encoding='utf-8'))
                            if wrong_json.is_file() else {})
            codes = [finding['code'] for finding in wrong_report.get('findings', [])]
            ok = (wrong_result.returncode == 0 and 'unreadable_artifact' in codes
                  and wrong_report.get('evidence_complete') is False)
            print(f"  {'ok  ' if ok else 'FAIL'} {label}：rc={wrong_result.returncode} "
                  f'findings={codes}')
            if not ok:
                failures.append(f'{label} 未报告 unreadable_artifact')

        # ---- 反例一：删掉被引用的任务工件 → 必须报 missing_artifact
        # 工件里存的是**绝对路径**，所以要连原件一起删，否则绝对路径仍然命中。
        broken = tmpdir / 'broken-missing-artifact'
        shutil.copytree(run_dir, broken)
        victim = next(broken.glob('task-dry-rules.json'), None)
        original = run_dir / 'task-dry-rules.json'
        if victim is None:
            failures.append('找不到 task-dry-rules.json，无法做缺工件反例')
        else:
            victim.unlink()
            original.unlink()
            out = tmpdir / 'broken1.json'
            run([str(EXE), 'report', '--run', str(broken), '--json', str(out)])
            broken_report = json.loads(out.read_text(encoding='utf-8'))
            codes = [f['code'] for f in broken_report['findings']]
            ok = 'missing_artifact' in codes and broken_report['evidence_complete'] is False
            print()
            print('=== 反例一：删掉被引用的任务工件 ===')
            print(f"  {'ok  ' if ok else 'FAIL'} findings={codes} evidence_complete="
                  f"{broken_report['evidence_complete']}")
            if not ok:
                failures.append(f'缺工件没被报出来：findings={codes}')

        # ---- 反例二：删掉会话日志 → 必须报 log_missing（副本在任何改动前就已复制）
        (no_log / 'session-log.jsonl').unlink()
        out2 = tmpdir / 'broken2.json'
        run([str(EXE), 'report', '--run', str(no_log), '--json', str(out2)])
        broken2 = json.loads(out2.read_text(encoding='utf-8'))
        codes2 = [f['code'] for f in broken2['findings']]
        ok2 = 'log_missing' in codes2
        print()
        print('=== 反例二：删掉会话日志 ===')
        print(f"  {'ok  ' if ok2 else 'FAIL'} findings={codes2}")
        if not ok2:
            failures.append(f'缺日志没被报出来：findings={codes2}')

        # ---- 反例三：目录不存在 → run_not_found（且退出码非 0）
        missing = run([str(EXE), 'report', '--run', str(tmpdir / 'not-a-run')])
        ok3 = missing.returncode != 0 and 'run_not_found' in missing.stdout
        print()
        print('=== 反例三：运行目录不存在 ===')
        print(f"  {'ok  ' if ok3 else 'FAIL'} 退出码={missing.returncode}")
        if not ok3:
            failures.append(f'不存在的运行目录应报 run_not_found 且非 0，实为 {missing.returncode}')

        # ---- runs 列表（R4 数据面）：条数要等于实际运行数，且**同一秒内连跑两次不能互相覆盖**
        print()
        print('=== runs 列表（含同秒两次运行）===')
        list_root = tmpdir / 'list-artifacts'
        for _ in range(2):      # 连跑两次：时间戳同秒时要能各自落盘
            run([str(EXE), 'queue', '--file', str(queue_file), '--artifacts', str(list_root)])
        run_dirs = sorted(p.name for p in list_root.glob('*') if p.is_dir())
        listing = tmpdir / 'runs.json'
        listed = run([str(EXE), 'runs', '--artifacts', str(list_root), '--json', str(listing)])
        list_checks: list[tuple[str, bool, str]] = [
            ('runs 退出码 0', listed.returncode == 0, f'退出码={listed.returncode}'),
            ('两次运行各自落盘（同秒不覆盖）', len(run_dirs) == 2, f'实际目录={run_dirs}'),
        ]
        if listing.is_file():
            document = json.loads(listing.read_text(encoding='utf-8'))
            stamps = [r['stamp'] for r in document['runs']]
            list_checks += [
                ('列表条数 = 运行目录数',
                 document['returned'] == len(run_dirs) == len(stamps),
                 f"returned={document['returned']} 目录={len(run_dirs)}"),
                ('每条都有结论与证据完整性',
                 all('queue_outcome' in r and 'evidence_complete' in r for r in document['runs']),
                 f'runs={document["runs"][:1]}'),
                ('列出的目录都真实存在',
                 all(Path(r['directory']).is_dir() for r in document['runs']),
                 f"dirs={[r['directory'] for r in document['runs']]}"),
                # 停止信息在**列表**这一层也要有（只做进单次报告是不够的：列表是前端的第一屏）
                ('列表每行都带停止信息字段',
                 all('stopped_early' in r and 'stop_reason' in r for r in document['runs']),
                 f"runs={document['runs'][:1]}"),
            ]
        else:
            list_checks.append(('runs --json 落盘', False, '没有产出列表文件'))
        for name, ok, detail in list_checks:
            print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
            if not ok:
                failures.append(f'{name}: {detail}')

        # 同一队列可连续执行相同章节；每个批次及单关证据都必须保留。
        print()
        print('=== 多战役任务与相对工件目录 ===')
        multi_queue = tmpdir / 'multi-queue.json'
        multi_queue.write_text(json.dumps({'tasks': [
            {'id': 'first-batch', 'kind': 'campaign_batch', 'input': {'chapters': [CHAPTER]}},
            {'id': 'second-batch', 'kind': 'campaign_batch', 'input': {'chapters': [CHAPTER]}},
        ]}), encoding='utf-8')
        multi_root = tmpdir / 'multi-artifacts'
        relative_root = os.path.relpath(multi_root, ROOT)
        multi_result = run([str(EXE), 'queue', '--file', str(multi_queue),
                            '--artifacts', relative_root], cwd=ROOT)
        multi_runs = sorted(p for p in multi_root.glob('*') if p.is_dir())
        if multi_result.returncode != 0 or len(multi_runs) != 1:
            failures.append('相对路径多战役队列未在预期目录产出运行目录')
            print(f'  FAIL queue rc={multi_result.returncode} runs={multi_runs}')
        else:
            multi_dir = multi_runs[0]
            indexes = [multi_dir / name for name in ('index.json', 'index-2.json')]
            tasks_docs = [multi_dir / name for name in
                          ('task-first-batch.json', 'task-second-batch.json')]
            multi_report_path = tmpdir / 'multi-report.json'
            run([str(EXE), 'report', '--run', str(multi_dir), '--json', str(multi_report_path)])
            multi_report = json.loads(multi_report_path.read_text(encoding='utf-8'))
            index_docs = [json.loads(p.read_text(encoding='utf-8')) for p in indexes if p.is_file()]
            task_docs = [json.loads(p.read_text(encoding='utf-8')) for p in tasks_docs if p.is_file()]
            sortie_refs = [stage['artifact'] for doc in index_docs for stage in doc['stages']]
            index_refs = [doc['evidence']['index_artifact'] for doc in task_docs]
            multi_checks = [
                ('两份批次索引', len(index_docs) == 2, f'indexes={[p.name for p in indexes if p.is_file()]}'),
                ('两个任务各自引用索引', len(index_refs) == 2
                 and len(set(index_refs)) == 2
                 and {Path(p).name for p in index_refs} == {'index.json', 'index-2.json'},
                 f'refs={index_refs}'),
                ('两份单关工件没有覆盖', len(sortie_refs) == 2
                 and len(set(sortie_refs)) == 2
                 and all(Path(p).is_file() for p in sortie_refs),
                 f'sorties={sortie_refs}'),
                ('报告汇总两关', multi_report['totals']['stages'] == 2,
                 f"stages={multi_report['totals']['stages']}"),
                ('多批次证据完整', multi_report['evidence_complete'] is True,
                 f"findings={[f['code'] for f in multi_report['findings']]}"),
            ]
            for name, ok, detail in multi_checks:
                print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  <- {detail}'))
                if not ok:
                    failures.append(f'{name}: {detail}')

            # 任务引用丢失的索引必须让报告标明证据缺口。
            missing_index = tmpdir / 'multi-missing-index'
            shutil.copytree(multi_dir, missing_index)
            task_path = missing_index / 'task-second-batch.json'
            task_doc = json.loads(task_path.read_text(encoding='utf-8'))
            task_doc['evidence']['index_artifact'] = str(missing_index / 'index-2.json')
            task_path.write_text(json.dumps(task_doc), encoding='utf-8')
            (missing_index / 'index-2.json').unlink()
            missing_report_path = tmpdir / 'multi-missing-report.json'
            run([str(EXE), 'report', '--run', str(missing_index),
                 '--json', str(missing_report_path)])
            missing_report = json.loads(missing_report_path.read_text(encoding='utf-8'))
            missing_codes = [f['code'] for f in missing_report['findings']]
            ok = 'missing_artifact' in missing_codes and not missing_report['evidence_complete']
            print(f"  {'ok  ' if ok else 'FAIL'} 缺失索引引用")
            if not ok:
                failures.append(f'缺失索引引用未被识别: {missing_codes}')

            damaged_index = tmpdir / 'multi-damaged-index'
            shutil.copytree(multi_dir, damaged_index)
            (damaged_index / 'index-2.json').write_text('{"stages":[42]}', encoding='utf-8')
            damaged_report_path = tmpdir / 'multi-damaged-report.json'
            run([str(EXE), 'report', '--run', str(damaged_index),
                 '--json', str(damaged_report_path)])
            damaged_report = json.loads(damaged_report_path.read_text(encoding='utf-8'))
            damaged_codes = [f['code'] for f in damaged_report['findings']]
            ok = ('unreadable_artifact' in damaged_codes
                  and not damaged_report['evidence_complete'])
            print(f"  {'ok  ' if ok else 'FAIL'} 损坏第二份索引")
            if not ok:
                failures.append(f'损坏第二份索引未被识别: {damaged_codes}')

            duplicate_index = tmpdir / 'multi-duplicate-index'
            shutil.copytree(multi_dir, duplicate_index)
            duplicate_task = duplicate_index / 'task-second-batch.json'
            duplicate_doc = json.loads(duplicate_task.read_text(encoding='utf-8'))
            duplicate_doc['evidence']['index_artifact'] = index_refs[0]
            duplicate_task.write_text(json.dumps(duplicate_doc), encoding='utf-8')
            duplicate_report_path = tmpdir / 'multi-duplicate-report.json'
            run([str(EXE), 'report', '--run', str(duplicate_index),
                 '--json', str(duplicate_report_path)])
            duplicate_report = json.loads(duplicate_report_path.read_text(encoding='utf-8'))
            duplicate_codes = [f['code'] for f in duplicate_report['findings']]
            ok = ('duplicate_batch_index' in duplicate_codes
                  and not duplicate_report['evidence_complete'])
            print(f"  {'ok  ' if ok else 'FAIL'} 重复索引引用")
            if not ok:
                failures.append(f'重复索引引用未被识别: {duplicate_codes}')

            mixed_batch = tmpdir / 'multi-mixed-batch'
            shutil.copytree(multi_dir, mixed_batch)
            changed_index = mixed_batch / 'index-2.json'
            changed_doc = json.loads(changed_index.read_text(encoding='utf-8'))
            changed_doc['outcome'] = 'error'
            changed_index.write_text(json.dumps(changed_doc), encoding='utf-8')
            mixed_report_path = tmpdir / 'multi-mixed-report.json'
            run([str(EXE), 'report', '--run', str(mixed_batch),
                 '--json', str(mixed_report_path)])
            mixed_report = json.loads(mixed_report_path.read_text(encoding='utf-8'))
            ok = mixed_report['batch_outcome'] == 'mixed'
            print(f"  {'ok  ' if ok else 'FAIL'} 不同批次结论摘要")
            if not ok:
                failures.append(f'不同批次结论被覆盖: {mixed_report["batch_outcome"]}')

        # ---- 单批命令的工件形态（没有 queue.json/state.json）：报告同样要读得全
        print()
        print('=== 单批命令（campaign --artifacts）===')
        campaign_root = tmpdir / 'campaign-artifacts'
        campaign = run([str(EXE), 'campaign', CHAPTER, '--artifacts', str(campaign_root)])
        campaign_run = sorted(p for p in campaign_root.glob('*') if p.is_dir())
        if campaign.returncode != 0 or not campaign_run:
            failures.append(f'campaign 没产出运行目录：退出码={campaign.returncode}')
        else:
            campaign_json = tmpdir / 'campaign-report.json'
            run([str(EXE), 'report', '--run', str(campaign_run[-1]), '--json', str(campaign_json)])
            campaign_report = json.loads(campaign_json.read_text(encoding='utf-8'))
            files = {p.name for p in campaign_run[-1].iterdir()}
            campaign_checks = [
                ('没有 queue.json / state.json（单批形态）',
                 files == {'index.json', 'session-log.jsonl', 'sortie-2-1.json'},
                 f'files={sorted(files)}'),
                ('批次结论读得到', campaign_report['batch_outcome'] == 'dry_run',
                 f"batch_outcome={campaign_report['batch_outcome']}"),
                ('关卡条目读得到', campaign_report['totals']['stages'] == 1,
                 f"stages={campaign_report['totals']['stages']}"),
                # 这两项以前只在 queue.json 里有，单批形态下必须是 `?` —— 现在要从 index.json 取
                ('宿主初始化次数不再是未知', campaign_report['host_start_count'] == 1,
                 f"host_start_count={campaign_report['host_start_count']}"),
                ('证据完整', campaign_report['evidence_complete'] is True,
                 f"findings={[f['code'] for f in campaign_report['findings']]}"),
            ]
            for name, ok, detail in campaign_checks:
                print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
                if not ok:
                    failures.append(f'{name}: {detail}')

    print()
    # ---- 反例：混进来的目录不算"一次运行"（否则 report --artifacts 会去读它并报误导性发现）
    print()
    print('=== 混进来的目录不算运行 ===')
    import tempfile as _tf
    _stray = Path(_tf.mkdtemp(prefix='alas-stray-'))   # 自己的临时目录：脚本里前面的 tmpdir 到这里已经清理了
    stray_root = _stray / 'stray-artifacts'
    stray_queue = _stray / 'stray-queue.json'
    stray_queue.write_text(json.dumps({'tasks': [
        {'id': 'only', 'kind': 'task_schedule', 'input': {'limit': 2}}]},
        ensure_ascii=False), encoding='utf-8')
    run([str(EXE), 'queue', '--file', str(stray_queue), '--artifacts', str(stray_root)])
    # 造一个排序**在时间戳之后**的目录：修复前会被"取最近一次"选中
    (stray_root / 'zzz-stray').mkdir(parents=True, exist_ok=True)
    stray_json = _stray / 'stray-report.json'
    run([str(EXE), 'report', '--artifacts', str(stray_root), '--json', str(stray_json)])
    stray_report = json.loads(stray_json.read_text(encoding='utf-8'))
    stray_runs = _stray / 'stray-runs.json'
    run([str(EXE), 'runs', '--artifacts', str(stray_root), '--json', str(stray_runs)])
    listed = json.loads(stray_runs.read_text(encoding='utf-8'))
    stray_checks = [
        ('report 取的是真运行（不是 zzz-stray）',
         'zzz-stray' not in str(stray_report.get('run') or ''),
         f"run={stray_report.get('run')}"),
        ('结论仍是有证据的成功（没被误导成 log_missing）',
         stray_report.get('queue_outcome') == 'succeeded'
         and not [f for f in stray_report.get('findings') or []
                  if f.get('code') == 'log_missing'],
         f"outcome={stray_report.get('queue_outcome')} findings={stray_report.get('findings')}"),
        ('runs 不把杂目录算成一次运行', listed.get('returned') == 1,
         f"returned={listed.get('returned')} runs={[r.get('directory') for r in listed.get('runs') or []]}"),
    ]
    for name, ok, detail in stray_checks:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
        if not ok:
            failures.append(f'{name}: {detail}')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（报告读得出事实；缺工件/缺日志/目录不存在都会被指出；'
          'runs 列表含同秒两次运行的落盘与条数）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
