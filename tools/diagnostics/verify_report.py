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

EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'
CHAPTER = 'campaign.campaign_main.campaign_2_1'
FRAME = '_boss122.png'          # 真机地图帧（账号状态任务用）


def run(args, timeout=300):
    return subprocess.run(args, capture_output=True, text=True, encoding='utf-8',
                          errors='replace', timeout=timeout)


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先运行 dotnet build）')
        return 1

    failures: list[str] = []
    with TemporaryDirectory(prefix='alas-report-') as tmp:
        tmpdir = Path(tmp)
        tasks = [{'id': 'dry-rules', 'kind': 'campaign_batch',
                  'input': {'chapters': [CHAPTER]}}]
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
        ]
        if frame.is_file():
            checks.append(('账号状态任务成功',
                           report['totals']['tasks_succeeded'] >= 1,
                           f"tasks_succeeded={report['totals']['tasks_succeeded']}"))
        print()
        print('=== 正常路径（报告读得出事实）===')
        for name, ok, detail in checks:
            print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
            if not ok:
                failures.append(f'{name}: {detail}')

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
            ]
        else:
            list_checks.append(('runs --json 落盘', False, '没有产出列表文件'))
        for name, ok, detail in list_checks:
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
