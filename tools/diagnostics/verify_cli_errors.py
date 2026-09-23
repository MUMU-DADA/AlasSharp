# -*- coding: utf-8 -*-
"""CLI **错误路径**验收 + 退出码契约（离线）。

写它的起因：这一夜我（作为驱动 CLI 的 Agent）从没验过"输入是坏的会怎样"。
坏输入是**最容易被驱动方踩到**的一类 —— 而它恰好没有任何断言。

契约（本脚本同时是它的文档）：

| 情形 | 退出码 | 说明 |
| --- | --- | --- |
| 正常 / 有跳过 | **0** | "没跑"不等于"跑失败"（本项目硬规矩）。**坏章节名这类输入问题 → 任务记 `skipped`、队列 `partial`、退出码 0** —— 想发现它要看 `queue.json` 的 `outcome`/`skipped`，不能只看退出码 |
| 有任务失败 | **1** | 运行层面的失败 |
| 输入/用法错误 | **2** | 队列文件不存在、不是合法 JSON、参数缺失 |

**注意测量方式**：`xxx | Select-Object -First N` 会在取够后**掐断管道**，
于是 `$LASTEXITCODE` 会变成被杀的退出码（我第一版就这么误读到"坏章节名退出码 2"）。
断言退出码时**别用 -First**。

用法：
    python tools/diagnostics/verify_cli_errors.py
"""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net10.0' / 'alashub.exe'
CHAPTER = 'campaign.campaign_main.campaign_2_1'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def run(*args, timeout=300):
    return subprocess.run([str(a) for a in args], capture_output=True, text=True,
                          encoding='utf-8', errors='replace', timeout=timeout)


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先 dotnet build）')
        return 1

    checks: list[tuple[str, bool, str]] = []
    failures: list[str] = []
    with tempfile.TemporaryDirectory(prefix='alas-cli-errors-') as tmp:
        tmpdir = Path(tmp)
        artifacts = tmpdir / 'artifacts'

        # 1) 队列文件不存在 → 退出码 2 + 明确文案
        missing = run(EXE, 'queue', '--file', str(tmpdir / 'no-such.json'),
                      '--artifacts', str(artifacts))
        checks.append(('不存在的队列文件 → 2 且点到路径',
                       missing.returncode == 2 and '找不到队列文件' in (missing.stdout + missing.stderr),
                       f'rc={missing.returncode} out={(missing.stdout or missing.stderr)[:120]!r}'))

        # 2) 坏 JSON → 退出码 2 + 明确文案（不是调用栈）
        broken = tmpdir / 'broken.json'
        broken.write_text('{ not json', encoding='utf-8')
        bad_json = run(EXE, 'queue', '--file', str(broken), '--artifacts', str(artifacts))
        combined = (bad_json.stdout or '') + (bad_json.stderr or '')
        checks.append(('坏 JSON → 2 且说清"不是合法 JSON"',
                       bad_json.returncode == 2 and '不是合法 JSON' in combined
                       and 'at Alas.' not in combined,
                       f'rc={bad_json.returncode} out={combined[:140]!r}'))

        # input 的容器形状是队列文件契约。event_state 对缺省输入有有效默认值，
        # 若数组被静默转成 null，这个坏请求会真的运行并落盘工件。
        invalid_input = tmpdir / 'invalid-input.json'
        invalid_input.write_text(json.dumps({'tasks': [
            {'id': 'inventory', 'kind': 'event_state', 'input': []}]}), encoding='utf-8')
        invalid_artifacts = tmpdir / 'invalid-input-artifacts'
        invalid = run(EXE, 'queue', '--file', str(invalid_input),
                      '--artifacts', str(invalid_artifacts))
        invalid_out = (invalid.stdout or '') + (invalid.stderr or '')
        checks.append(('非对象 input → 输入错误且不启动会话',
                       invalid.returncode == 2 and 'input 必须是 JSON 对象' in invalid_out
                       and not invalid_artifacts.exists(),
                       f'rc={invalid.returncode} artifacts={invalid_artifacts.exists()} '
                       f'out={invalid_out[:140]!r}'))

        # 3) 坏章节名 → **skipped + 队列 partial + 退出码 0**（按项目语义：没跑 ≠ 跑失败）
        chapter_file = tmpdir / 'bad-chapter.json'
        chapter_file.write_text(json.dumps({'tasks': [
            {'id': 'x', 'kind': 'campaign_batch', 'input': {'chapters': ['not.a.chapter']}}]}),
            encoding='utf-8')
        bad_chapter = run(EXE, 'queue', '--file', str(chapter_file), '--artifacts', str(artifacts))
        out = (bad_chapter.stdout or '') + (bad_chapter.stderr or '')
        checks.append(('坏章节名 → skipped 且队列 partial，退出码 0',
                       bad_chapter.returncode == 0 and 'outcome=skipped' in out
                       and 'outcome=partial' in out,
                       f'rc={bad_chapter.returncode} out={out[-160:]!r}'))
        checks.append(('坏章节名的原因是可读的前置条件文案',
                       '前置条件不满足' in out and 'not.a.chapter' in out,
                       f'out={out[-160:]!r}'))

        # 4) 正常路径仍然是 0（对照，防止"什么都返回 0"这种假绿）
        good_file = tmpdir / 'good.json'
        good_file.write_text(json.dumps({'tasks': [
            {'id': 'ok', 'kind': 'campaign_batch', 'input': {'chapters': [CHAPTER]}}]}),
            encoding='utf-8')
        good = run(EXE, 'queue', '--file', str(good_file), '--artifacts', str(artifacts))
        # 注意：dry-run 的队列结论是 **dry_run**（不是 succeeded）—— 第一版我按 succeeded 断言，红了。
        # 这条顺带把这个约定钉住：**没真跑的批次有自己的结论词**。
        checks.append(('正常 dry-run 队列 → 0（结论是 dry_run）',
                       good.returncode == 0 and 'outcome=dry_run' in (good.stdout or ''),
                       f'rc={good.returncode} out={(good.stdout or "")[-140:]!r}'))
        spaced_file = tmpdir / 'chapter-with-spaces.json'
        spaced_file.write_text(json.dumps({'tasks': [
            {'id': 'spaced', 'kind': 'campaign_batch',
             'input': {'chapters': [f' {CHAPTER} ']}}
        ]}), encoding='utf-8')
        spaced = run(EXE, 'queue', '--file', str(spaced_file),
                     '--artifacts', str(tmpdir / 'spaced-artifacts'))
        spaced_out = (spaced.stdout or '') + (spaced.stderr or '')
        checks.append(('章节名两端空格沿用原有去空格行为',
                       spaced.returncode == 0 and 'outcome=dry_run' in spaced_out,
                       f'rc={spaced.returncode} out={spaced_out[-160:]!r}'))

        invalid_campaign_fields = [
            ('chapter-type', {'chapters': [42]}),
            ('repeat-type', {'chapters': [CHAPTER], 'repeat_until_cleared': 'false'}),
            ('stop-type', {'chapters': [CHAPTER], 'stop_on_failure': 'false'}),
            ('rounds-fraction', {'chapters': [CHAPTER], 'max_rounds': 1.5}),
            ('seconds-zero', {'chapters': [CHAPTER], 'max_seconds': 0}),
            ('fleet-zero', {'chapters': [CHAPTER], 'fleet1': 0}),
            ('unknown-field', {'chapters': [CHAPTER], 'max_round': 1}),
        ]
        invalid_campaign_file = tmpdir / 'invalid-campaign-fields.json'
        invalid_campaign_file.write_text(json.dumps({'tasks': [
            {'id': task_id, 'kind': 'campaign_batch', 'input': task_input}
            for task_id, task_input in invalid_campaign_fields
        ]}), encoding='utf-8')
        invalid_campaign_artifacts = tmpdir / 'invalid-campaign-artifacts'
        invalid_campaign = run(EXE, 'queue', '--file', str(invalid_campaign_file),
                               '--artifacts', str(invalid_campaign_artifacts))
        campaign_indexes = sorted(invalid_campaign_artifacts.glob('*/queue.json'))
        campaign_dir = campaign_indexes[-1].parent if campaign_indexes else None
        campaign_tasks = (json.loads(campaign_indexes[-1].read_text(encoding='utf-8'))['tasks']
                          if campaign_indexes else [])
        checks.append(('战役输入错误在上游调用前逐项跳过',
                       invalid_campaign.returncode == 0
                       and len(campaign_tasks) == len(invalid_campaign_fields)
                       and all(task['outcome'] == 'skipped' and 'input.' in task['error']
                               for task in campaign_tasks)
                       and campaign_dir is not None
                       and not list(campaign_dir.glob('sortie-*.json')),
                       f'rc={invalid_campaign.returncode} tasks={campaign_tasks!r}'))

        # 同一 id 改了业务输入后，旧断点不能把新请求当成已完成。
        resume_file = tmpdir / 'resume-changed-input.json'
        resume_artifacts = tmpdir / 'resume-changed-input-artifacts'
        resume_file.write_text(json.dumps({'tasks': [
            {'id': 'inventory', 'kind': 'event_state', 'input': {'folder_prefix': 'event_'}}
        ]}), encoding='utf-8')
        first_resume = run(EXE, 'queue', '--file', str(resume_file),
                           '--artifacts', str(resume_artifacts))
        resume_file.write_text(json.dumps({'tasks': [
            {'id': 'inventory', 'kind': 'event_state', 'input': {'folder_prefix': 'main_'}}
        ]}), encoding='utf-8')
        changed_resume = run(EXE, 'queue', '--file', str(resume_file),
                             '--artifacts', str(resume_artifacts), '--resume')
        changed_out = (changed_resume.stdout or '') + (changed_resume.stderr or '')
        checks.append(('同 id 改 input 后必须执行新任务',
                       first_resume.returncode == 0 and changed_resume.returncode == 0
                       and 'outcome=succeeded' in changed_out
                       and 'outcome=skipped' not in changed_out,
                       f'first={first_resume.returncode} second={changed_resume.returncode} '
                       f'out={changed_out[-180:]!r}'))

        # 断点依赖有序前缀：前序任务变了，后序旧完成项也必须重跑；尾部追加仍可续跑。
        prefix_file = tmpdir / 'resume-prefix.json'
        prefix_artifacts = tmpdir / 'resume-prefix-original'
        first_task = {'id': 'first', 'kind': 'event_state',
                      'input': {'folder_prefix': 'event_'}}
        second_task = {'id': 'second', 'kind': 'event_state',
                       'input': {'folder_prefix': 'main_'}}
        third_task = {'id': 'third', 'kind': 'event_state',
                      'input': {'folder_prefix': 'campaign_'}}
        prefix_file.write_text(json.dumps({'tasks': [first_task, second_task]}), encoding='utf-8')
        prefix_first = run(EXE, 'queue', '--file', str(prefix_file),
                           '--artifacts', str(prefix_artifacts))
        prefix_states = sorted(prefix_artifacts.glob('*/state.json'))
        prefix_state = prefix_states[-1] if prefix_states else tmpdir / 'missing-prefix-state.json'

        modified_first = {**first_task, 'input': {'folder_prefix': 'main_'}}
        prefix_file.write_text(json.dumps({'tasks': [modified_first, second_task]}), encoding='utf-8')
        changed_prefix_artifacts = tmpdir / 'resume-prefix-changed'
        changed_prefix = run(EXE, 'queue', '--file', str(prefix_file),
                             '--artifacts', str(changed_prefix_artifacts),
                             '--resume', '--resume-state', str(prefix_state))
        changed_indexes = sorted(changed_prefix_artifacts.glob('*/queue.json'))
        changed_tasks = (json.loads(changed_indexes[-1].read_text(encoding='utf-8'))['tasks']
                         if changed_indexes else [])
        checks.append(('改前序任务后，后序旧完成项必须重跑',
                       prefix_first.returncode == 0 and changed_prefix.returncode == 0
                       and [task['outcome'] for task in changed_tasks] == ['succeeded', 'succeeded'],
                       f'first={prefix_first.returncode} changed={changed_prefix.returncode} '
                       f'tasks={changed_tasks!r}'))

        prefix_file.write_text(json.dumps({'tasks': [second_task, first_task]}),
                               encoding='utf-8')
        reordered_artifacts = tmpdir / 'resume-prefix-reordered'
        reordered = run(EXE, 'queue', '--file', str(prefix_file),
                        '--artifacts', str(reordered_artifacts),
                        '--resume', '--resume-state', str(prefix_state))
        reordered_indexes = sorted(reordered_artifacts.glob('*/queue.json'))
        reordered_tasks = (json.loads(reordered_indexes[-1].read_text(encoding='utf-8'))['tasks']
                           if reordered_indexes else [])
        checks.append(('队列重排后旧完成项不得直接跳过',
                       reordered.returncode == 0 and
                       [task['outcome'] for task in reordered_tasks] ==
                       ['succeeded', 'succeeded'],
                       f'rc={reordered.returncode} tasks={reordered_tasks!r}'))

        prefix_file.write_text(json.dumps({'tasks': [first_task, second_task, third_task]}),
                               encoding='utf-8')
        appended_artifacts = tmpdir / 'resume-prefix-appended'
        appended = run(EXE, 'queue', '--file', str(prefix_file),
                       '--artifacts', str(appended_artifacts),
                       '--resume', '--resume-state', str(prefix_state))
        appended_indexes = sorted(appended_artifacts.glob('*/queue.json'))
        appended_tasks = (json.loads(appended_indexes[-1].read_text(encoding='utf-8'))['tasks']
                          if appended_indexes else [])
        checks.append(('只在队尾追加任务时可续跑已有完成项',
                       appended.returncode == 0 and
                       [task['outcome'] for task in appended_tasks] ==
                       ['skipped', 'skipped', 'succeeded'],
                       f'rc={appended.returncode} tasks={appended_tasks!r}'))

        custom_state = tmpdir / 'custom-completed.json'
        latest_dry_state = sorted(resume_artifacts.glob('*/state.json'))[-1]
        custom_state.write_bytes(latest_dry_state.read_bytes())
        explicit_resume = run(EXE, 'queue', '--file', str(resume_file),
                              '--artifacts', str(tmpdir / 'explicit-state-artifacts'),
                              '--resume', '--resume-state', str(custom_state))
        explicit_out = (explicit_resume.stdout or '') + (explicit_resume.stderr or '')
        checks.append(('显式断点读取指定文件名',
                       explicit_resume.returncode == 0 and 'outcome=skipped' in explicit_out
                       and str(custom_state) in explicit_out,
                       f'rc={explicit_resume.returncode} out={explicit_out[-180:]!r}'))
        failed_state = tmpdir / 'failed-completed.json'
        failed_document = json.loads(custom_state.read_text(encoding='utf-8'))
        failed_document['completed']['inventory']['outcome'] = 'failed'
        failed_state.write_text(json.dumps(failed_document), encoding='utf-8')
        failed_state_artifacts = tmpdir / 'failed-state-artifacts'
        failed_resume = run(EXE, 'queue', '--file', str(resume_file),
                            '--artifacts', str(failed_state_artifacts),
                            '--resume', '--resume-state', str(failed_state))
        failed_state_out = (failed_resume.stdout or '') + (failed_resume.stderr or '')
        checks.append(('断点中的 failed 不能伪装为已完成',
                       failed_resume.returncode == 2 and '断点文件包含非完成结论' in failed_state_out
                       and not failed_state_artifacts.exists(),
                       f'rc={failed_resume.returncode} artifacts={failed_state_artifacts.exists()} '
                       f'out={failed_state_out[-180:]!r}'))
        missing_state_artifacts = tmpdir / 'missing-state-artifacts'
        missing_state = run(EXE, 'queue', '--file', str(resume_file),
                            '--artifacts', str(missing_state_artifacts),
                            '--resume', '--resume-state', str(tmpdir / 'absent-state.json'))
        missing_state_out = (missing_state.stdout or '') + (missing_state.stderr or '')
        checks.append(('显式断点不存在时拒绝执行',
                       missing_state.returncode == 2 and '找不到断点文件' in missing_state_out
                       and not missing_state_artifacts.exists(),
                       f'rc={missing_state.returncode} artifacts={missing_state_artifacts.exists()} '
                       f'out={missing_state_out[-180:]!r}'))
        corrupt_state = tmpdir / 'corrupt-state.json'
        corrupt_state.write_text('{ broken', encoding='utf-8')
        corrupt_artifacts = tmpdir / 'corrupt-state-artifacts'
        corrupt = run(EXE, 'queue', '--file', str(resume_file),
                      '--artifacts', str(corrupt_artifacts),
                      '--resume', '--resume-state', str(corrupt_state))
        corrupt_out = (corrupt.stdout or '') + (corrupt.stderr or '')
        checks.append(('损坏的断点不能静默触发重跑',
                       corrupt.returncode == 2 and '断点文件不可读取' in corrupt_out
                       and not corrupt_artifacts.exists(),
                       f'rc={corrupt.returncode} artifacts={corrupt_artifacts.exists()} '
                       f'out={corrupt_out[-180:]!r}'))
        live_resume = run(EXE, 'queue', '--file', str(resume_file),
                          '--artifacts', str(resume_artifacts), '--resume',
                          '--run', '--read-only-device')
        live_out = (live_resume.stdout or '') + (live_resume.stderr or '')
        checks.append(('dry-run 断点不能跳过真实运行',
                       live_resume.returncode == 0 and 'outcome=succeeded' in live_out
                       and 'outcome=skipped' not in live_out,
                       f'rc={live_resume.returncode} out={live_out[-180:]!r}'))

        stop_file = tmpdir / 'stop.request'
        stop_file.touch()
        stopped = run(EXE, 'queue', '--file', str(good_file), '--stop-file', str(stop_file),
                      '--artifacts', str(tmpdir / 'stopped-artifacts'))
        stopped_out = (stopped.stdout or '') + (stopped.stderr or '')
        checks.append(('预先存在的停止文件 → 任务边界取消并留工件',
                       stopped.returncode == 0 and 'outcome=cancelled' in stopped_out
                       and 'outcome=skipped' in stopped_out and '[任务工件]' in stopped_out,
                       f'rc={stopped.returncode} out={stopped_out[-180:]!r}'))

        unknown_artifacts = tmpdir / 'unknown-artifacts'
        unknown = run(EXE, 'queue', '--file', str(good_file), '--not-a-run-flag', 'value',
                      '--artifacts', str(unknown_artifacts))
        unknown_out = (unknown.stdout or '') + (unknown.stderr or '')
        checks.append(('未知公共参数 → 输入错误且不启动会话',
                       unknown.returncode == 2 and '未知参数' in unknown_out
                       and not unknown_artifacts.exists(),
                       f'rc={unknown.returncode} artifacts={unknown_artifacts.exists()}'))

        # 5) 任务失败 → 1（运行层面失败 ≠ 输入错误）
        fail_file = tmpdir / 'fail.json'
        fail_file.write_text(json.dumps({'tasks': [
            {'id': 'pf', 'kind': 'periodic_preflight', 'input': {'task': 'reward'}}]}),
            encoding='utf-8')
        failing = run(EXE, 'queue', '--file', str(fail_file), '--artifacts', str(artifacts))
        checks.append(('任务失败 → 1（与输入错误的 2 区分开）',
                       failing.returncode == 1 and 'outcome=failed' in (failing.stdout or ''),
                       f'rc={failing.returncode}'))

        # 任务专属兼容命令不再各自解释输入；observe/navigate 必须经 queue --file 进入任务模型。
        navigation_artifacts = tmpdir / 'navigation-artifacts'
        navigation = run(EXE, 'goto', 'page_no_such_fixture', '--serial', 'offline-do-not-connect',
                          '--artifacts', str(navigation_artifacts), timeout=30)
        navigation_out = (navigation.stdout or '') + (navigation.stderr or '')
        checks.append(('goto 已弃用且不启动会话',
                       navigation.returncode == 2 and 'goto 已弃用' in navigation_out
                       and 'queue --file' in navigation_out and not navigation_artifacts.exists(),
                       f'rc={navigation.returncode} artifacts={navigation_artifacts.exists()}'))

        run_artifacts = tmpdir / 'run-invalid-artifacts'
        observation = run(EXE, 'run', '--serial', 'offline-do-not-connect', '--seconds', 'not-a-number',
                          '--artifacts', str(run_artifacts), timeout=30)
        observation_out = (observation.stdout or '') + (observation.stderr or '')
        checks.append(('run 已弃用且不启动会话',
                       observation.returncode == 2 and 'run 已弃用' in observation_out
                       and 'queue --file' in observation_out and not run_artifacts.exists(),
                       f'rc={observation.returncode} artifacts={run_artifacts.exists()}'))

    print('=== CLI 错误路径与退出码契约 ===')
    for name, ok, detail in checks:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
        if not ok:
            failures.append(f'{name}: {detail}')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（0=正常/有跳过、1=任务失败、2=输入错误；文案都说清了是什么坏了）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
