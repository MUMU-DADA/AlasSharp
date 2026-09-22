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
