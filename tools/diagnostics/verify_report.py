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

        # ---- 反例二：删掉会话日志 → 必须报 log_missing
        no_log = tmpdir / 'broken-no-log'
        shutil.copytree(run_dir, no_log)
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

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（报告读得出事实，缺工件/缺日志/目录不存在都能被指出来）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
