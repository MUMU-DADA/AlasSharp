# -*- coding: utf-8 -*-
"""周期任务域的数据源验收：上游任务目录（`task_catalog`）。

**两个来源不是一回事**（第一版脚本把两者当同一集合比对，被自己的对拍当场证伪）：

  A. `task.yaml` 的顶层键 —— 是**分组**（本机 9 个）
  B. 生成产物 `args.json` —— 是**扁平任务清单**（本机 68 个），"有哪些任务"看这个

所以本脚本不再假设两者相等，而是：把差异如实打印（供做周期任务域时确认用哪个），
再验 C# 任务路径报出来的数与**独立读出的**两个来源一致，并钉住一条关键不变量 ——
**分组集合与任务集合不同**（上游若改了目录结构，这条会红，提示重读 docs/tasks.md）。

用法：
    python tools/diagnostics/verify_task_catalog.py
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
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

    # ---- 第二段：C# 任务路径（`kind = "task_catalog"`）—— 与**独立读出的**两个来源对拍。
    # 为什么两段都要：第一段验宿主 op（Python 侧），这一段验产品路径（队列里的任务）；
    # 都对着同一份上游数据，才能把"任务报的数对不对"钉死。
    print()
    print('=== C# 任务路径（队列里的 task_catalog）===')
    exe = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'
    if not exe.is_file():
        print(f'[跳过] 未构建 {exe.relative_to(ROOT)}（先 dotnet build）—— 任务路径未验。')
    else:
        with tempfile.TemporaryDirectory(prefix='alas-task-catalog-') as tmp:
            tmpdir = Path(tmp)
            queue_file = tmpdir / 'queue.json'
            queue_file.write_text(json.dumps({'tasks': [
                {'id': 'catalog', 'kind': 'task_catalog', 'input': {'limit': 3}}]},
                ensure_ascii=False), encoding='utf-8')
            artifacts = tmpdir / 'artifacts'
            executed = subprocess.run([str(exe), 'queue', '--file', str(queue_file),
                                       '--artifacts', str(artifacts)],
                                      capture_output=True, text=True, encoding='utf-8',
                                      errors='replace', timeout=300)
            task_artifact = next(iter(sorted(artifacts.glob('*/task-catalog.json'))), None)
            if executed.returncode != 0 or task_artifact is None:
                failures.append(f'任务路径没跑通：退出码={executed.returncode}')
                print(f"  FAIL 任务路径  ← {(executed.stdout or '')[-200:]}")
            else:
                document = json.loads(task_artifact.read_text(encoding='utf-8'))
                evidence = document.get('evidence') or {}
                expected_tasks = set(json.loads(args_json.read_text(encoding='utf-8')).keys())
                # 打印机会静默失效（键名一改这行就没了），所以连它一起断言 —— 与第 143 轮给配置开关域做的一样
                task_checks = [
                    ('CLI 打出 [任务证据] 行', '[任务证据]' in (executed.stdout or '')
                     and '分组来源=' in (executed.stdout or ''), 'stdout 里没有 [任务证据] 或 分组来源='),
                    ('任务结论 succeeded', document.get('outcome') == 'succeeded',
                     f"outcome={document.get('outcome')} error={document.get('error')}"),
                    ('任务数与独立数一致', evidence.get('task_count') == len(expected_tasks),
                     f"任务={evidence.get('task_count')} 独立={len(expected_tasks)}"),
                    # 关键不变量：分组集合与任务集合**不是同一回事** —— 这条如果哪天成立，
                    # 说明上游改了目录结构，docs/tasks.md 里那段结论要跟着重写。
                    ('分组确实是分组（与任务集合不同）',
                     bool(evidence.get('groups')) and set(evidence.get('groups') or []) == source_names
                     and set(evidence.get('groups') or []) != expected_tasks,
                     f"groups={evidence.get('groups')}"),
                    ('样本取自任务表', all(t in expected_tasks
                                        for t in evidence.get('task_sample') or []),
                     f"sample={evidence.get('task_sample')}"),
                ]
                for name, ok, detail in task_checks:
                    print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
                    if not ok:
                        failures.append(f'{name}: {detail}')

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
