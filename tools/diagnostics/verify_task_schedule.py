# -*- coding: utf-8 -*-
"""第六域验收：周期任务的调度状态（`kind = "task_schedule"`，只读）。

三段：

1. **与独立读数对拍**（真配置）：C# 任务报出来的任务数/启用数/无 Scheduler 数，
   与脚本直接从 `args.json` + `config/alas.json` 数一遍的结果逐项一致 ——
   单侧读数错了不会两边一起错。
2. **边界情形**（构造的假配置走同一条代码路径）：全禁用 / 全启用 / 缺 `Scheduler` 段 /
   配置不存在（应明确报错而不是崩）。
3. **只读保证**：跑完之后**配置文件字节不变**（`sha256` 前后比对）—— 这条比"我们没写配置"的声明更硬。

用法：
    python tools/diagnostics/verify_task_schedule.py
"""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net10.0' / 'alashub.exe'
ARGS_JSON = ENGINE / 'module' / 'config' / 'argument' / 'args.json'
CONFIG = ENGINE / 'config' / 'alas.json'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def run_task(config_path: str, only_enabled: bool = False) -> dict:
    """跑一次队列任务，返回它的 evidence（或错误信息）。"""
    with tempfile.TemporaryDirectory(prefix='alas-sched-') as tmp:
        queue_file = Path(tmp) / 'queue.json'
        queue_file.write_text(json.dumps({'tasks': [{
            'id': 'sched', 'kind': 'task_schedule',
            'input': {'only_enabled': only_enabled, 'limit': 200,
                      'config_path': config_path}}]}, ensure_ascii=False), encoding='utf-8')
        artifacts = Path(tmp) / 'artifacts'
        proc = subprocess.run([str(EXE), 'queue', '--file', str(queue_file),
                               '--artifacts', str(artifacts)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=300)
        artifact = next(iter(sorted(artifacts.glob('*/task-sched.json'))), None)
        if artifact is None:
            return {'_run_error': f'退出码={proc.returncode} {(proc.stdout or "")[-200:]}'}
        document = json.loads(artifact.read_text(encoding='utf-8'))
        evidence = document.get('evidence') or {}
        evidence['_outcome'] = document.get('outcome')
        evidence['_stdout'] = proc.stdout or ''   # CLI 输出也带回来: 好断言 [任务证据] 行确实打出来了
        evidence['_error'] = document.get('error')
        return evidence


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先 dotnet build）')
        return 1
    if not ARGS_JSON.is_file() or not CONFIG.is_file():
        print('[跳过] 缺少上游 args.json 或账号配置 config/alas.json；本验收未跑。')
        return 0

    failures: list[str] = []
    tasks = sorted(json.loads(ARGS_JSON.read_text(encoding='utf-8')).keys())
    config = json.loads(CONFIG.read_text(encoding='utf-8'))
    expected_enabled = sorted(t for t in tasks
                              if isinstance(config.get(t), dict)
                              and isinstance(config[t].get('Scheduler'), dict)
                              and config[t]['Scheduler'].get('Enable'))
    expected_no_scheduler = sorted(t for t in tasks
                                   if not (isinstance(config.get(t), dict)
                                           and isinstance(config[t].get('Scheduler'), dict)))

    before = sha256(CONFIG)
    real = run_task(str(CONFIG), only_enabled=True)
    after = sha256(CONFIG)

    print('=== 与独立读数对拍（真配置）===')
    checks = [
        ('任务结论 succeeded', real.get('_outcome') == 'succeeded',
         f"outcome={real.get('_outcome')} error={real.get('_error')}"),
        ('任务总数一致', real.get('task_count') == len(tasks),
         f"任务={real.get('task_count')} 独立={len(tasks)}"),
        ('启用数一致', real.get('enabled_count') == len(expected_enabled),
         f"任务={real.get('enabled_count')} 独立={len(expected_enabled)}"),
        ('无 Scheduler 数一致', real.get('no_scheduler_count') == len(expected_no_scheduler),
         f"任务={real.get('no_scheduler_count')} 独立={len(expected_no_scheduler)}"),
        ('列出的都是启用项', all(e.get('enable') is True for e in real.get('listed') or []),
         f"listed={real.get('listed')[:3]}"),
        ('只读：配置字节不变', before == after, f'{before[:12]} → {after[:12]}'),
        ('CLI 打出 [任务证据] 行（键名改掉会静默消失）', '[任务证据]' in real.get('_stdout', '') and '启用=' in real.get('_stdout', ''), 'stdout 里没有 [任务证据] 或 启用='),
    ]
    for name, ok, detail in checks:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
        if not ok:
            failures.append(f'{name}: {detail}')

    # ---- 边界：用构造的假配置走同一条代码路径
    print()
    print('=== 边界情形（构造的假配置）===')
    with tempfile.TemporaryDirectory(prefix='alas-sched-fixtures-') as tmp:
        tmpdir = Path(tmp)
        fixtures = {
            'all-disabled': {t: {'Scheduler': {'Enable': False}} for t in tasks},
            'all-enabled': {t: {'Scheduler': {'Enable': True, 'NextRun': '2026-01-01 00:00:00'}}
                            for t in tasks},
            'no-scheduler': {t: {'Other': {}} for t in tasks},
        }
        for name, payload in fixtures.items():
            path = tmpdir / f'{name}.json'
            path.write_text(json.dumps(payload), encoding='utf-8')
            evidence = run_task(str(path), only_enabled=True)
            if name == 'all-disabled':
                # 断言用**任务证据里真实存在的字段**（listed），不要用宿主 op 的 listed_count ——
                # 任务类没有把它抄进证据，那是它自己的取舍；测试不该假设它存在。
                ok = evidence.get('_outcome') == 'succeeded' and evidence.get('enabled_count') == 0 \
                     and evidence.get('listed') == []
            elif name == 'all-enabled':
                ok = evidence.get('_outcome') == 'succeeded' \
                     and evidence.get('enabled_count') == len(tasks)
            else:
                ok = evidence.get('_outcome') == 'succeeded' \
                     and evidence.get('no_scheduler_count') == len(tasks) \
                     and evidence.get('enabled_count') == 0
            print(f"  {'ok  ' if ok else 'FAIL'} {name}: enabled={evidence.get('enabled_count')} "
                  f"no_scheduler={evidence.get('no_scheduler_count')} "
                  f"outcome={evidence.get('_outcome')}")
            if not ok:
                failures.append(f'{name}: {evidence}')

        missing = run_task(str(tmpdir / 'not-exists.json'), only_enabled=True)
        ok = missing.get('_outcome') == 'failed' and '读不到' in str(missing.get('_error') or '')
        print(f"  {'ok  ' if ok else 'FAIL'} 配置不存在: outcome={missing.get('_outcome')} "
              f"error={str(missing.get('_error'))[:60]}")
        if not ok:
            failures.append(f"配置不存在时应 Failed 且给出原因，实为 {missing}")

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（读数与独立计数一致、四种边界明确、跑完配置字节不变）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
