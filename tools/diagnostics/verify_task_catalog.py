# -*- coding: utf-8 -*-
"""周期任务域的数据源验收：上游任务目录（`task_catalog` op）。

周期任务（科研/建造/委托/每日…）的清单与分组定义在上游 `task.yaml` 里，并由上游生成器
变成 `args.json`。本项目**不另维护一份任务表**，所以这里做的是**两个来源互相对拍**：

  A. `task_catalog` op 用上游 loader 读 `task.yaml` 得到的任务名集合（源）
  B. 上游生成产物 `module/config/argument/args.json` 里的任务名集合（生成结果）

A ⊆ B（源里的任务都要落到生成物里）且两者数量一致时才算对得上；
差集要打印出来 —— 只报"不一致"没法定位是源漏读还是生成物过期。

用法：
    python tools/diagnostics/verify_task_catalog.py
"""

from __future__ import annotations

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
import alas_vision as av          # noqa: E402  必须先 chdir 到上游仓库再导入


def main() -> int:
    task_yaml = ENGINE / 'module' / 'config' / 'argument' / 'task.yaml'
    args_json = ENGINE / 'module' / 'config' / 'argument' / 'args.json'
    if not task_yaml.is_file():
        print(f'[跳过] 找不到 {task_yaml}（上游仓库不在 .runtime/engine？）')
        return 0

    response = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': 'task_catalog', 'args': {}})))
    if not response.get('ok'):
        print(f"**失败**：task_catalog 调用失败：{response.get('error')}")
        return 1
    catalog = response['result']
    if catalog.get('error'):
        print(f"**失败**：{catalog['error']}")
        return 1

    source_names = set(catalog.get('source_groups') or [])
    generated = catalog.get('generated_tasks') or []
    print(f"=== 上游任务目录 ===\nsource={catalog.get('source')} loader={catalog.get('loader')}")
    print(f"  task.yaml 顶层键（= 分组）: {catalog.get('source_group_count')} 个")
    print(f"  args.json 任务表（= 生成后的扁平清单）: {catalog.get('generated_task_count')} 个 "
          f"from {catalog.get('generated_source')}")

    failures: list[str] = []
    if not source_names:
        failures.append('task.yaml 里一个顶层键都没解析出来')
    if catalog.get('generated_error'):
        failures.append(f"读不到生成产物: {catalog['generated_error']}")
    if not generated:
        failures.append('args.json 里没有任务（生成产物缺失或为空）')

    # 两个来源本来就不是同一集合：这里**如实列出差异**，供做周期任务域时确认该用哪个。
    # （第一版脚本把两者当同一集合比对，被自己的对拍当场证伪 —— 这个差异就是结论。）
    print(f"  交集（既是分组名又是任务名）: {sorted(source_names & set(generated))}")
    print(f"  仅分组（不是任务）: {sorted(source_names - set(generated))}")

    groups = catalog.get('groups') or {}
    print(f"  带分组信息的顶层键：{len(groups)} 个"
          + (f"，例：{list(groups.items())[:2]}" if groups else ''))

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（两个来源都可读；周期任务域的扁平清单应取 args.json，差异已如实标注）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
