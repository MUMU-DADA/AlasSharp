# -*- coding: utf-8 -*-
"""R3 第二步：把 17 个候选钩子**按形态分类**，再决定谁值得迁移。

为什么先分类（上一轮的教训）：`map_data_init` 覆盖 15 章、看着最该先做，结果 0/15 章
能被静态固定成统一形态 —— 直接照一章的写法动手，会造出一个对其它 14 章都不成立的"通用能力"。
所以先按**钩子体的形态**给全部候选分桶，用数据决定顺序，而不是用覆盖数决定顺序。

分桶（启发式，标签就写在报告里，不假装是精确定论）：

  * `pure_delegate` —— 体里只有 `super().X(...)`：**没有可迁移的东西**；
  * `data_only`     —— 不调用 `self.*` 方法，只做数据/常量改写：是**章节自己的数据**，
                       属于上游关卡规则，不该搬进 C#；
  * `self_calls`    —— 调用了其它 `self.*` 方法（依赖引擎状态）：这才是**引擎能力**，
                       迁移要逐个看依赖闭包与对拍成本；
  * `not_in_module` —— IR 说这章覆盖了该钩子，但源码里找不到定义（继承来的）：如实列出。

产出 `docs/archive/reports/r3-hook-shapes.md`（入库）。用法：
    python tools/diagnostics/r3_hook_shapes.py
"""

from __future__ import annotations

import ast
import json
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / 'data' / 'campaign'
ENGINE = ROOT / '.runtime' / 'engine'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def hook_chapters():
    """{hook: [source, ...]}，按 source 排序。"""
    by_hook = defaultdict(list)
    for path in sorted(DATA.rglob('*.json')):
        document = json.loads(path.read_text(encoding='utf-8'))
        hooks = (document.get('campaign') or {}).get('native_overrides') or []
        source = str(document.get('source') or '')
        for hook in hooks:
            by_hook[str(hook)].append(source)
    return by_hook


def find_hook_body(tree: ast.AST, hook: str):
    """在模块里找 `def <hook>(self, ...)`（任意类内），返回函数节点或 None。"""
    for node in ast.walk(tree):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == hook:
            return node
    return None


def classify(body: ast.AST, module_methods: set):
    """返回 (标签, 细节)。

    关键区分（第一版没做，读源码时当场发现）：`self.X()` 里的 X 有两种完全不同的东西 ——
      * X 是**本章节模块自己定义的方法**（例：`self.before_boss()` 后再 `super().clear_boss()`）
        → 这是章节自己的调度顺序，不是引擎能力；
      * X 解析到基类/引擎（例：`self._goto(...)`、`self.map.select(...)`）
        → 这才是引擎能力，迁移要算依赖闭包。
    不区分的话会把"调用自己的前置步骤再委托"误报成引擎候选。
    """
    super_calls, self_calls, module_names, has_control_flow = 0, set(), set(), False
    for node in ast.walk(body):
        if isinstance(node, (ast.If, ast.For, ast.While, ast.Try)):
            has_control_flow = True
        if isinstance(node, ast.Call):
            func = node.func
            if isinstance(func, ast.Attribute):
                if isinstance(func.value, ast.Call) and isinstance(func.value.func, ast.Name) \
                        and func.value.func.id == 'super':
                    super_calls += 1
                elif isinstance(func.value, ast.Name) and func.value.id == 'self':
                    self_calls.add(func.attr)
        elif isinstance(node, ast.Name) and isinstance(node.ctx, ast.Load):
            if node.id not in ('self', 'super', 'True', 'False', 'None'):
                module_names.add(node.id)
    local_calls = sorted(c for c in self_calls if c in module_methods)
    engine_calls = sorted(c for c in self_calls if c not in module_methods)
    detail = {'super_calls': super_calls, 'local_calls': local_calls,
              'engine_calls': engine_calls,
              'module_names': sorted(module_names)[:6], 'control_flow': has_control_flow}
    if engine_calls:
        return 'engine_calls', detail
    if local_calls:
        return 'chapter_local', detail
    if super_calls and not module_names and not has_control_flow:
        return 'pure_delegate', detail
    return 'data_only', detail


def module_local_methods(tree: ast.AST):
    """模块里**自己定义**的方法名（用于把 `self.X()` 分成章节本地调用与引擎调用）。"""
    names = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.ClassDef):
            for child in node.body:
                if isinstance(child, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    names.add(child.name)
    return names


def main() -> int:
    if not DATA.is_dir():
        print(f'[跳过] 没有 {DATA.relative_to(ROOT)}（先跑 tools/export_upstream_data.py）')
        return 0

    by_hook = hook_chapters()
    classification = {}
    for hook, sources in sorted(by_hook.items()):
        buckets = defaultdict(list)
        details = {}
        for source in sources:
            path = ENGINE / source
            if not path.is_file():
                buckets['not_in_module'].append(source)
                details[source] = {'reason': '源码不存在'}
                continue
            tree = ast.parse(path.read_text(encoding='utf-8'))
            body = find_hook_body(tree, hook)
            if body is None:
                buckets['not_in_module'].append(source)
                details[source] = {'reason': '该模块里没有这个方法的定义（继承来的）'}
                continue
            label, detail = classify(body, module_local_methods(tree))
            buckets[label].append(source)
            details[source] = detail
        classification[hook] = {'buckets': {k: v for k, v in buckets.items()},
                                'details': details}

    lines = [
        '# R3 钩子形态分类（决定谁值得迁移）',
        '',
        '> 本页由 `tools/diagnostics/r3_hook_shapes.py` 从关卡 IR + 章节源码**静态**生成，**不手写**。',
        '> 分桶是启发式：标签给出**判断依据**（super 调用数 / 调用了哪些 self 方法 / 引用哪些模块常量 / 有无控制流），',
        '> 便于人工复核，而不是让读者相信一个结论。',
        '',
        '## 分桶含义',
        '',
        '| 标签 | 含义 | 对 R3 的意义 |',
        '| --- | --- | --- |',
        '| `pure_delegate` | 体里只有 `super().X(...)` | **没有可迁移的东西** |',
        '| `data_only` | 不调用 `self.*`，只做数据/常量改写 | 是**章节自己的数据**（属上游关卡规则），不该搬进 C# |',
        '| `self_calls` | 调用其它 `self.*` 方法 | 这才是**引擎能力**候选，需逐个看依赖闭包与对拍成本 |',
        '| `not_in_module` | IR 说覆盖了该钩子，但源码里没有定义 | 继承来的；如实列出，不猜 |',
        '',
        '## 逐钩子',
        '',
        '| 钩子 | 覆盖章节 | pure_delegate | data_only | chapter_local | **engine_calls** | not_in_module | 例 |',
        '| --- | --- | --- | --- | --- | --- | --- | --- |',
    ]
    for hook, info in sorted(classification.items(),
                             key=lambda kv: -len(by_hook[kv[0]])):
        buckets = info['buckets']
        example = ''
        for label in ('engine_calls', 'chapter_local', 'data_only', 'pure_delegate', 'not_in_module'):
            if buckets.get(label):
                example = f"`{buckets[label][0].split('/')[-1]}`({label})"
                break
        lines.append(f"| `{hook}` | {len(by_hook[hook])} | {len(buckets.get('pure_delegate', []))} "
                     f"| {len(buckets.get('data_only', []))} | {len(buckets.get('chapter_local', []))} | {len(buckets.get('engine_calls', []))} "
                     f"| {len(buckets.get('not_in_module', []))} | {example} |")

    engine_candidates = {h: i for h, i in classification.items()
                         if i['buckets'].get('engine_calls')}
    lines += [
        '',
        '## 结论（用数据说话）',
        '',
        f'- **有引擎能力候选（出现 `self_calls`）的钩子：{len(engine_candidates)} 个** —— '
        + ('、'.join(f"`{h}`" for h in sorted(engine_candidates)) if engine_candidates else '无'),
        f"- 只做数据改写（`data_only`）的钩子："
        f"{sum(1 for i in classification.values() if i['buckets'].get('data_only'))} 个"
        ' —— 这些留在上游，不搬进 C#。',
        f"- 纯委托（`pure_delegate`）："
        f"{sum(1 for i in classification.values() if i['buckets'].get('pure_delegate'))} 个"
        ' —— 没有工作量。',
        '',
        '> 与 `docs/archive/reports/r3-candidates.md`（按覆盖数排序）**配合使用**：覆盖数决定"影响面"，',
        '> 形态决定"值不值得做、做了能不能对拍"。上一轮 `map_data_init` 就是覆盖数第一但形态不统一。',
        '',
        '复现：`python tools/diagnostics/r3_hook_shapes.py`。',
        '',
    ]
    (ROOT / 'docs' / 'archive/reports/r3-hook-shapes.md').write_text('\n'.join(lines), encoding='utf-8')

    print(f'=== R3 钩子形态分类（{len(classification)} 个钩子）===')
    for hook, info in sorted(classification.items(), key=lambda kv: -len(by_hook[kv[0]])):
        buckets = info['buckets']
        summary = ' '.join(f'{k}={len(v)}' for k, v in sorted(buckets.items()))
        print(f'  {hook:34s} {len(by_hook[hook]):3d} 章  {summary}')
    print()
    print(f'报告: docs/archive/reports/r3-hook-shapes.md')
    print(f'结果: OK（{len(classification)} 个钩子全部有分类，未分类 0）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
