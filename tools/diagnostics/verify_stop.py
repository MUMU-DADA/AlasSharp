# -*- coding: utf-8 -*-
"""R4「停止任务」验收：`--stop-file` 触发取消，在任务边界生效。

R4 门槛要求"前端可查看任务状态和证据、**停止任务**"。运行时的取消语义是
"在任务边界生效、不打断进行中的上游调用"，所以这里验的是**触发与落账**：

  1. 停止文件在启动前就存在 → 队列在第一个边界就停：结论 `cancelled`、`stopped_early`、
     剩余任务如实记 `skipped`（"没跑"不能算失败，所以退出码仍是 0）；
  2. 没有停止文件 → 任务照常跑完（对照，证明没把正常运行弄坏）。

**已知边界**（如实写在这里，不假装是缺陷）：停止在**任务边界**生效，所以任务极快时
（dry-run 每关约 9ms）整个队列可能在第一个计时周期之前就跑完了 —— 这是"边界语义"的
固有性质。需要"必定停住"时用启动前就存在的停止文件（本脚本第 1 条就是这么测的）。

用法：
    python tools/diagnostics/verify_stop.py
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


def build_queue(root: Path, count: int) -> Path:
    path = root / 'queue.json'
    path.write_text(json.dumps({'tasks': [
        {'id': f't{i:03d}', 'kind': 'campaign_batch', 'input': {'chapters': [CHAPTER]}}
        for i in range(count)]}, ensure_ascii=False), encoding='utf-8')
    return path


def run_queue(queue_file: Path, artifacts: Path, *extra):
    return subprocess.run([str(EXE), 'queue', '--file', str(queue_file),
                           '--artifacts', str(artifacts), *extra],
                          capture_output=True, text=True, encoding='utf-8',
                          errors='replace', timeout=600)


def queue_artifact(artifacts: Path):
    newest = sorted(artifacts.glob('*/queue.json'))[-1]
    return json.loads(newest.read_text(encoding='utf-8'))


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先运行 dotnet build）')
        return 1

    failures: list[str] = []
    with tempfile.TemporaryDirectory(prefix='alas-stop-verify-') as tmp:
        tmpdir = Path(tmp)

        # ---- 1) 启动前就有停止文件 → 第一个边界即停
        stopped_root = tmpdir / 'stopped'
        stopped_root.mkdir()
        queue_file = build_queue(stopped_root, 20)
        stop_file = stopped_root / 'STOP'
        stop_file.write_text('stop', encoding='utf-8')
        proc = run_queue(queue_file, stopped_root / 'artifacts', '--stop-file', str(stop_file))
        document = queue_artifact(stopped_root / 'artifacts')
        skipped = sum(1 for t in document['tasks'] if t['outcome'] == 'skipped')
        checks = [
            ('打印了停止请求', '停止' in (proc.stdout or ''), (proc.stdout or '')[-160:]),
            ('结论 cancelled', document['outcome'] == 'cancelled', document['outcome']),
            ('stopped_early=True', document['stopped_early'] is True, str(document['stopped_early'])),
            ('剩余任务记 skipped（不是失败）', skipped == len(document['tasks']),
             f'skipped={skipped}/{len(document["tasks"])}'),
            ('skipped 不算失败 → 退出码 0', proc.returncode == 0, f'退出码={proc.returncode}'),
        ]

        # ---- 2) 对照：没有停止文件 → 全部跑完
        normal_root = tmpdir / 'normal'
        normal_root.mkdir()
        normal_queue = build_queue(normal_root, 5)
        normal = run_queue(normal_queue, normal_root / 'artifacts')
        normal_document = queue_artifact(normal_root / 'artifacts')
        checks += [
            ('无停止文件时全部跑完', normal_document['outcome'] == 'dry_run'
             and normal_document['stopped_early'] is False,
             f"outcome={normal_document['outcome']} stopped_early={normal_document['stopped_early']}"),
            ('对照退出码 0', normal.returncode == 0, f'退出码={normal.returncode}'),
        ]

        print('=== 停止任务（--stop-file）===')
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
    print('结果: OK（停止在任务边界生效、剩余任务如实记跳过；不影响正常运行）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
