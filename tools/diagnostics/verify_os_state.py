# -*- coding: utf-8 -*-
"""R2 第三域验收：大世界/海域只读探针（`kind = "os_state"`）。

两件事：

1. **只读探针跑得通**：用存盘帧跑 `os_state` 任务，判据来自宿主已有的
   `map_detect(mode="os")` 产品路径，本脚本只核对证据字段与工件，不重复实现识别。
2. **"没跑"不等于"跑失败"**：`capture=true` 在 dry-run 下必须记 **skipped + 原因**，
   而不是 failed —— 这正是 R2 通用任务模型的核心口径，顺手在这里也验一遍。

"没检测到"（`detected=false`）是**有效状态**：可能这一帧不在海域里（夹具是球面/大世界帧）。
所以断言的是"字段齐全、结论是跑通"，不是"必须 detected=true"。

用法：
    python tools/diagnostics/verify_os_state.py
没有 `data/fixtures/os_map.png` 时显式跳过（不静默通过）。
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

EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'
FIXTURE = DATA / 'fixtures' / 'os_map.png'


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先运行 dotnet build）')
        return 1
    if not FIXTURE.is_file():
        print(f'[跳过] 没有 {FIXTURE.relative_to(ROOT)}（`alashub map` 的默认夹具）；'
              f'大世界探针的帧断言未跑。')
        return 0

    failures: list[str] = []
    with TemporaryDirectory(prefix='alas-os-state-') as tmp:
        tmpdir = Path(tmp)
        queue_file = tmpdir / 'queue.json'
        queue_file.write_text(json.dumps({'tasks': [
            {'id': 'os-map', 'kind': 'os_state',
             'input': {'screenshot': str(FIXTURE.resolve())}},
            # dry-run 下 capture=true 必须被前置条件拦下（记 skipped，不是 failed）
            {'id': 'os-capture-dry', 'kind': 'os_state', 'input': {'capture': True}},
        ]}, ensure_ascii=False, indent=1), encoding='utf-8')
        artifacts = tmpdir / 'artifacts'
        proc = subprocess.run([str(EXE), 'queue', '--file', str(queue_file),
                               '--artifacts', str(artifacts)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=300)
        if proc.returncode != 0:
            failures.append(f'alashub queue 退出码 {proc.returncode}')
            print((proc.stdout or '')[-1500:])
        run_dirs = sorted(p for p in artifacts.glob('*') if p.is_dir())
        if not run_dirs:
            print('**失败**：没有产出运行目录')
            return 1
        run_dir = run_dirs[-1]
        queue_artifact = json.loads((run_dir / 'queue.json').read_text(encoding='utf-8'))

        print('=== 大世界/海域只读探针（存盘帧，无设备）===')
        checks: list[tuple[str, bool, str]] = []

        map_artifact_path = run_dir / 'task-os-map.json'
        if not map_artifact_path.is_file():
            failures.append('缺少 task-os-map.json')
            evidence = {}
            outcome = None
        else:
            artifact = json.loads(map_artifact_path.read_text(encoding='utf-8'))
            outcome = artifact['outcome']
            evidence = artifact.get('evidence') or {}
        checks += [
            ('探针任务成功', outcome == 'succeeded', f'outcome={outcome}'),
            ('证据带 mode=os', evidence.get('mode') == 'os', f"mode={evidence.get('mode')}"),
            ('证据带来源', str(evidence.get('source', '')).startswith('file:'),
             f"source={evidence.get('source')}"),
            ('证据带帧形状', (evidence.get('frame') or {}).get('shape') == [720, 1280, 3],
             f"frame={evidence.get('frame')}"),
            ('证据带 detected 字段', 'detected' in evidence, '缺 detected'),
            ('证据带 grid_count 字段', 'grid_count' in evidence, '缺 grid_count'),
            ('队列结论 partial（一成功一跳过）', queue_artifact['outcome'] == 'partial',
             f"outcome={queue_artifact['outcome']}"),
        ]

        capture_artifact_path = run_dir / 'task-os-capture-dry.json'
        if not capture_artifact_path.is_file():
            failures.append('缺少 task-os-capture-dry.json')
            capture = {}
        else:
            capture = json.loads(capture_artifact_path.read_text(encoding='utf-8'))
        checks += [
            ('capture 在 dry-run 记 skipped（不是 failed）', capture.get('outcome') == 'skipped',
             f"outcome={capture.get('outcome')}"),
            ('skipped 带原因', 'capture=true' in str(capture.get('error', '')),
             f"error={capture.get('error')}"),
            # dry-run 且没给 serial 时，两条前置条件同时不满足（dry-run 不碰设备 + 需要 serial）——
            # 断言"被记下来了且说清了 dry-run 这条"，不写死条数。
            ('前置条件被记录',
             bool(capture.get('unmet_preconditions'))
             and any('真跑会话' in text for text in capture.get('unmet_preconditions') or []),
             f"unmet={capture.get('unmet_preconditions')}"),
        ]

        for name, ok, detail in checks:
            print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
            if not ok:
                failures.append(f'{name}: {detail}')

        (DATA / 'os_state_probe.json').write_text(json.dumps({
            'note': '大世界只读探针的实测证据（detected=false 表示该帧不在海域里，是有效状态）',
            'evidence': evidence,
            'capture_precondition': {'outcome': capture.get('outcome'),
                                     'error': capture.get('error'),
                                     'unmet_preconditions': capture.get('unmet_preconditions')},
        }, ensure_ascii=False, indent=1), encoding='utf-8')
        print('  证据已写入: data/os_state_probe.json')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（只读探针跑通、判据来自宿主、capture 在 dry-run 记 skipped 而非失败）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
