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
EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'
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

        # 5) 任务失败 → 1（运行层面失败 ≠ 输入错误）
        fail_file = tmpdir / 'fail.json'
        fail_file.write_text(json.dumps({'tasks': [
            {'id': 'pf', 'kind': 'periodic_preflight', 'input': {'task': 'reward'}}]}),
            encoding='utf-8')
        failing = run(EXE, 'queue', '--file', str(fail_file), '--artifacts', str(artifacts))
        checks.append(('任务失败 → 1（与输入错误的 2 区分开）',
                       failing.returncode == 1 and 'outcome=failed' in (failing.stdout or ''),
                       f'rc={failing.returncode}'))

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
