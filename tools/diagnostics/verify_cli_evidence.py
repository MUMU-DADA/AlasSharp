# -*- coding: utf-8 -*-
"""CLI 的 `[任务证据]` 行验收：**每个域的摘要行都必须真的打印出来**。

为什么单独一条：这一夜补过三次同类缺口（调度状态第 124 轮、配置开关第 143 轮、周期任务清点第 172 轮），
每次都是"打印行只手工验过"。而它的失效方式恰恰是**静默**的 —— 证据字段改个键名，
这行就不打了，队列却照样成功。

覆盖本脚本能离线跑的三个域（其余域的 CLI 行已在各自脚本里断言）：

| 域 | 怎么离线跑 | 断言的行 |
| --- | --- | --- |
| 战役批量（dry-run） | `alashub campaign <章节> --artifacts`（dry-run 不碰设备） | `[任务证据] batch_outcome=…` |
| 账号状态（存盘帧） | 队列 + `account_state` 任务，输入 `screenshot` | `[任务证据] server=…` |
| 大世界探针（存盘帧） | 队列 + `os_state` 任务，输入 `screenshot` | `[任务证据] mode=… detected=…` |

缺存档帧时对应项**显式跳过**（`data/` 不入库）。

用法：
    python tools/diagnostics/verify_cli_evidence.py
"""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / 'data'
EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'
CHAPTER = 'campaign.campaign_main.campaign_2_1'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def run(*args, timeout=300):
    return subprocess.run([str(a) for a in args], capture_output=True, text=True,
                          encoding='utf-8', errors='replace', timeout=timeout)


def run_queue(task: dict, tmpdir: Path) -> str:
    queue_file = tmpdir / f"queue-{task['id']}.json"
    queue_file.write_text(json.dumps({'tasks': [task]}, ensure_ascii=False), encoding='utf-8')
    proc = run(EXE, 'queue', '--file', queue_file, '--artifacts', tmpdir / f"art-{task['id']}")
    return proc.stdout or ''


def frame(*names: str) -> Path | None:
    for name in names:
        candidate = DATA / name
        if candidate.is_file():
            return candidate
    return None


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先 dotnet build）')
        return 1

    failures: list[str] = []
    checks: list[tuple[str, bool, str]] = []

    with tempfile.TemporaryDirectory(prefix='alas-cli-evidence-') as tmp:
        tmpdir = Path(tmp)

        # 1) 战役批量（dry-run，不需要设备）—— **走队列路径**：`[任务证据]` 行是队列打印机出的，
        #    单独的 `campaign` 命令打的是 `[合同]`/`[工件]`（第一版就踩了这个，见提交信息）。
        campaign_out = run_queue({'id': 'camp', 'kind': 'campaign_batch',
                                  'input': {'chapters': [CHAPTER]}}, tmpdir)
        checks.append(('战役批量：打印 batch_outcome',
                       '[任务证据]' in campaign_out and 'batch_outcome=' in campaign_out,
                       f'stdout 尾部={campaign_out[-160:]!r}'))

        # 2) 账号状态（用存盘帧离线跑）
        shot = frame('s3_final_stage.png', '_shot_campaign_map.png')
        if shot is None:
            print('[跳过] 账号状态：没有可用存档帧')
        else:
            out = run_queue({'id': 'acct', 'kind': 'account_state',
                             'input': {'screenshot': str(shot)}}, tmpdir)
            checks.append(('账号状态：打印 server',
                           '[任务证据]' in out and 'server=' in out,
                           f'stdout 尾部={out[-160:]!r}'))

        # 3) 大世界探针（用存盘帧离线跑）
        shot = frame('_live_enter_map_stall.png', '_14_inmap.png')
        if shot is None:
            print('[跳过] 大世界探针：没有可用地图帧')
        else:
            out = run_queue({'id': 'os', 'kind': 'os_state',
                             'input': {'screenshot': str(shot)}}, tmpdir)
            checks.append(('大世界探针：打印 detected',
                           '[任务证据]' in out and 'detected=' in out,
                           f'stdout 尾部={out[-160:]!r}'))

    print('=== CLI 的 [任务证据] 行 ===')
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
    print(f'结果: OK（{len(checks)} 个域的摘要行都真的打印了；缺帧的域显式跳过）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
