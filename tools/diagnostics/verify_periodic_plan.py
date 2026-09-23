# -*- coding: utf-8 -*-
"""周期任务勘察验收：`periodic_plan`（只读，报"跑某任务会去跑哪个类"）。

三段：

1. **与独立读数对拍**：op 报的"导入 + 类名 + 行号"与脚本**自己再读一遍** `alas.py` 的
   结果逐项一致 —— 单侧读错不会两边一起错。
2. **边界**：不存在的任务名 → `found=false` + 明确原因；空任务名 → 明确报错（都不许兜底）。
3. **只读保证**：调用前后 `sys.modules` 里**不该多出目标模块** —— 这条比"我们没有 import"的
   声明硬：如果哪天有人在 op 里加了 import，这条会红。

用法：
    python tools/diagnostics/verify_periodic_plan.py
"""

from __future__ import annotations

import ast
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
sys.path.insert(0, str(ROOT / 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

os.chdir(ENGINE)
import alas_vision as av                                        # noqa: E402

TASKS = ['commission', 'research', 'dorm', 'reward']


def independent_read(task: str):
    """独立实现：从 alas.py 里读同名方法的行号与导入（不 import 任何目标模块）。"""
    with open(Path(ENGINE) / 'alas.py', encoding='utf-8') as stream:
        tree = ast.parse(stream.read())
    for node in ast.walk(tree):
        if isinstance(node, ast.FunctionDef) and node.name == task:
            imports = sorted(
                f'from {n.module or ""} import {a.name}'
                for n in ast.walk(node) if isinstance(n, ast.ImportFrom)
                for a in n.names)
            return {'lineno': node.lineno, 'imports': imports}
    return None


def call(args):
    response = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': 'periodic_plan', 'args': args})))
    assert response.get('ok'), response
    return response['result']


def main() -> int:
    failures: list[str] = []
    print('=== 与独立读数对拍 ===')
    for task in TASKS:
        mine = independent_read(task)
        got = call({'task': task})
        if mine is None:
            ok = got.get('found') is False
            detail = f"独立读也找不到；op={got}"
        else:
            ok = (got.get('found') is True and got.get('lineno') == mine['lineno']
                  and sorted(got.get('imports') or []) == mine['imports'])
            detail = (f"op(lineno={got.get('lineno')}, imports={got.get('imports')}) "
                      f"独立(lineno={mine['lineno']}, imports={mine['imports']})")
        print(f"  {'ok  ' if ok else 'FAIL'} {task:12s} {'' if ok else '← ' + detail}")
        if not ok:
            failures.append(f'{task}: {detail}')

    print()
    print('=== 边界 ===')
    unknown = call({'task': 'no_such_task_zzz'})
    empty = call({})
    boundary = [
        ('不存在的任务名 → found=false + 原因',
         unknown.get('found') is False and bool(unknown.get('error')),
         f'{unknown}'),
        ('空任务名 → 明确报错（不兜底）',
         bool(empty.get('error')) and empty.get('found') is None,
         f'{empty}'),
    ]
    for name, ok, detail in boundary:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
        if not ok:
            failures.append(f'{name}: {detail}')

    print()
    print('=== 只读保证（调用前后不该多出目标模块）===')
    watch = ['module.commission.commission', 'module.research.research']
    before = {name for name in watch if name in sys.modules}
    call({'task': 'commission'})
    call({'task': 'research'})
    after = {name for name in watch if name in sys.modules}
    ok = before == after
    print(f"  {'ok  ' if ok else 'FAIL'} 未新导入目标模块（{sorted(after) or '无'}）")
    if not ok:
        failures.append(f'op 引入了目标模块: {sorted(after - before)}')

    # ---- 任务侧：走队列（产品路径），断言结论与证据；输入有错必须 failed 而不是"部分成功"
    print()
    print('=== 任务侧（kind = periodic_plan，走队列）===')
    exe = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'
    if not exe.is_file():
        print(f'[跳过] 未构建 {exe.relative_to(ROOT)}（先 dotnet build）—— 任务侧未验。')
    else:
        import subprocess
        import tempfile
        with tempfile.TemporaryDirectory(prefix='alas-periodic-') as tmp:
            tmpdir = Path(tmp)
            good = tmpdir / 'good.json'
            good.write_text(json.dumps({'tasks': [
                {'id': 'plan', 'kind': 'periodic_plan', 'input': {'tasks': ['commission']}}]}),
                encoding='utf-8')
            bad = tmpdir / 'bad.json'
            bad.write_text(json.dumps({'tasks': [
                {'id': 'plan-bad', 'kind': 'periodic_plan',
                 'input': {'tasks': ['commission', 'no_such_task_zzz']}}]}), encoding='utf-8')

            def run_queue(queue_file: Path, artifacts: Path):
                proc = subprocess.run([str(exe), 'queue', '--file', str(queue_file),
                                '--artifacts', str(artifacts)],
                               capture_output=True, text=True, encoding='utf-8',
                               errors='replace', timeout=300)
                artifact = next(iter(sorted(artifacts.glob('*/task-*.json'))), None)
                return json.loads(artifact.read_text(encoding='utf-8')) if artifact else {}

            ok_doc = run_queue(good, tmpdir / 'art-good')
            bad_doc = run_queue(bad, tmpdir / 'art-bad')
            ok_evidence = ok_doc.get('evidence') or {}
            bad_evidence = bad_doc.get('evidence') or {}
            plan = (ok_evidence.get('plans') or [{}])[0]
            expected = independent_read('commission') or {}
            task_checks = [
                ('全查到 → succeeded', ok_doc.get('outcome') == 'succeeded',
                 f"outcome={ok_doc.get('outcome')}"),
                ('证据里的行号与独立读一致', plan.get('lineno') == expected.get('lineno'),
                 f"任务={plan.get('lineno')} 独立={expected.get('lineno')}"),
                ('证据带导入与 run 标记',
                 bool(plan.get('imports')) and plan.get('calls_run') is True, f'{plan}'),
                ('有查不到的名字 → failed（不是部分成功）',
                 bad_doc.get('outcome') == 'failed'
                 and bad_evidence.get('missing') == ['no_such_task_zzz'],
                 f"outcome={bad_doc.get('outcome')} missing={bad_evidence.get('missing')}"),
                ('失败原因点名了是哪个任务',
                 'no_such_task_zzz' in str(bad_doc.get('error') or ''),
                 f"error={bad_doc.get('error')}"),
            ]
            for name, ok, detail in task_checks:
                print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
                if not ok:
                    failures.append(f'{name}: {detail}')

    # ---- 放行判定（periodic_preflight）：四条路径 + "永不执行"这条不变量
    print()
    print('=== 放行判定（kind = periodic_preflight）===')
    if not exe.is_file():
        print('[跳过] 未构建 alashub —— 放行判定未验。')
    else:
        import subprocess
        import tempfile
        with tempfile.TemporaryDirectory(prefix='alas-preflight-') as tmp:
            tmpdir = Path(tmp)
            cases = {
                'no-auth': {'task': 'reward'},
                'bad-confirm': {'task': 'reward', 'allow_actions': True, 'confirm': 'research'},
                'unknown': {'task': 'no_such_task_zzz', 'allow_actions': True,
                            'confirm': 'no_such_task_zzz'},
                'allowed': {'task': 'reward', 'allow_actions': True, 'confirm': 'reward'},
            }
            queue_file = tmpdir / 'queue.json'
            queue_file.write_text(json.dumps({'tasks': [
                {'id': key, 'kind': 'periodic_preflight', 'input': value}
                for key, value in cases.items()] + [
                # 执行入口也放进来：**只放未授权的那条** —— 它会被闸门挡下、不会真的执行
                {'id': 'run-no-auth', 'kind': 'periodic_run', 'input': {'task': 'reward'}},
            ]}, ensure_ascii=False), encoding='utf-8')
            artifacts = tmpdir / 'artifacts'
            proc = subprocess.run([str(exe), 'queue', '--file', str(queue_file),
                            '--artifacts', str(artifacts), '--continue-on-error'],
                           capture_output=True, text=True, encoding='utf-8',
                           errors='replace', timeout=300)
            docs = {}
            for artifact in artifacts.glob('*/task-*.json'):
                document = json.loads(artifact.read_text(encoding='utf-8'))
                docs[artifact.name.replace('task-', '').replace('.json', '')] = document

            def decision(key):
                evidence = (docs.get(key) or {}).get('evidence') or {}
                return (docs.get(key) or {}).get('outcome'), evidence

            gate_checks = []
            # 执行入口的可见性与结论：拒绝时 CLI 也要打出 [任务证据] 行（第 194 轮加的那行）
            run_outcome = (docs.get('run-no-auth') or {}).get('outcome')
            gate_checks.append((
                'periodic_run 未授权 → failed 且 CLI 打出判定行',
                run_outcome == 'failed'
                and '[任务证据]' in (proc.stdout or '')
                and '判定=denied' in (proc.stdout or ''),
                f"outcome={run_outcome} stdout 含判定行={'判定=denied' in (proc.stdout or '')}"))
            for key in ('no-auth', 'bad-confirm', 'unknown'):
                outcome, evidence = decision(key)
                gate_checks.append((f'{key} → failed 且 decision=denied',
                                    outcome == 'failed' and evidence.get('decision') == 'denied',
                                    f"outcome={outcome} decision={evidence.get('decision')}"))
            _, allowed_evidence = decision('allowed')
            gate_checks += [
                ('两闸都过 → allowed',
                 allowed_evidence.get('decision') == 'allowed', f'{allowed_evidence}'),
                # 这条是**不变量**：这个 op/任务存在的前提就是它不驱动游戏
                ('四条路径的 executes 全为 False（永不执行）',
                 all((decision(k)[1].get('executes') is False) for k in cases),
                 f"{ {k: decision(k)[1].get('executes') for k in cases} }"),
                ('放行时随附勘察结果（会跑哪个类）',
                 bool((allowed_evidence.get('plan') or {}).get('imports')),
                 f"plan={allowed_evidence.get('plan')}"),
                ('未授权的原因提到 allow_actions',
                 'allow_actions' in str(decision('no-auth')[1].get('reason') or ''),
                 f"reason={decision('no-auth')[1].get('reason')}"),
            ]
            for name, ok, detail in gate_checks:
                print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
                if not ok:
                    failures.append(f'{name}: {detail}')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（读数与独立读一致、边界明确、且不 import 目标模块）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
