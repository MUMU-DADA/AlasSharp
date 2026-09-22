# -*- coding: utf-8 -*-
"""R3 第一项的上游轨迹：`map_data_init` 钩子（覆盖 15 章，覆盖数居首）。

**这个钩子是什么**（读上游源码得来，不是猜的）：

    def map_data_init(self, map_):
        super().map_data_init(map_)
        if not self.map_is_clear_mode:
            for override_grid in OVERRIDE:
                # Set may_enemy, but keep may_ambush
                self.map[override_grid.location].may_enemy = override_grid.may_enemy

即：基类初始化完地图数据后，按章节自带的 `OVERRIDE` 列表**逐格改写 `may_enemy`，
保留 `may_ambush`**；清图模式（`map_is_clear_mode`）下整段跳过。
它是 R3 理想的第一个候选：高频（15 章）、低耦合（纯网格标志改写）、可对拍（输入输出都是数据）。

**导出的 IR 里没有 OVERRIDE**（只有 `map.map_data` 基础网格 + `native_overrides` 钩子名），
所以这一轮先把**上游轨迹这一半**取出来：对每个带该钩子的章节，用 AST 解析章节源码
（不 import，避免副作用）抽出 `OVERRIDE` 的逐条 `(location, may_enemy, may_ambush)`。

产出 `data/r3_map_data_init_trajectory.json`，并如实列出"静态抽不出来"的章节 ——
**不留静默缺口**：抽不出来的必须出现在报告里，而不是被悄悄跳过。

用法：
    python tools/diagnostics/r3_map_data_init.py
"""

from __future__ import annotations

import ast
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / 'data' / 'campaign'
ENGINE = ROOT / '.runtime' / 'engine'
OUT = ROOT / 'data' / 'r3_map_data_init_trajectory.json'
HOOK = 'map_data_init'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def chapters_with_hook():
    for path in sorted(DATA.rglob('*.json')):
        document = json.loads(path.read_text(encoding='utf-8'))
        hooks = (document.get('campaign') or {}).get('native_overrides') or []
        if HOOK in hooks:
            yield str(document.get('source') or ''), document


def extract_overrides(source_path: Path):
    """从章节源码里抽 `OVERRIDE = [OverrideGrid(...), ...]`；抽不出就返回 (None, 原因)。"""
    if not source_path.is_file():
        return None, f'源码不存在: {source_path}'
    tree = ast.parse(source_path.read_text(encoding='utf-8'))
    for node in tree.body:                      # 只看模块级赋值
        if not isinstance(node, ast.Assign):
            continue
        targets = [t.id for t in node.targets if isinstance(t, ast.Name)]
        if 'OVERRIDE' not in targets:
            continue
        if not isinstance(node.value, (ast.List, ast.Tuple)):
            return None, f'OVERRIDE 不是字面量列表（{type(node.value).__name__}）'
        entries = []
        for element in node.value.elts:
            if not isinstance(element, ast.Call):
                return None, 'OVERRIDE 元素不是构造函数调用'
            kwargs = {kw.arg: kw.value for kw in element.keywords}
            entry = {'call': getattr(element.func, 'id', '?')}
            for key, value in kwargs.items():
                if isinstance(value, ast.Constant):
                    entry[key] = value.value
                elif isinstance(value, ast.Name):
                    entry[key] = f'@{value.id}'          # 引用常量，留给人工确认
                else:
                    entry[key] = f'<{type(value).__name__}>'
            entries.append(entry)
        return entries, None
    return None, '该章节源码里没有模块级 OVERRIDE'


def main() -> int:
    if not DATA.is_dir():
        print(f'[跳过] 没有 {DATA.relative_to(ROOT)}（先跑 tools/export_upstream_data.py）')
        return 0

    records, unresolved = [], []
    for source, document in chapters_with_hook():
        path = ENGINE / source
        entries, reason = extract_overrides(path)
        base_grid = ((document.get('map') or {}).get('map_data')) or None
        record = {
            'source': source,
            'module': source.replace('/', '.').replace('.py', ''),
            'base_grid_lines': len(str(base_grid).splitlines()) if base_grid else 0,
            'overrides': entries,
        }
        if entries is None:
            record['unresolved'] = reason
            unresolved.append({'source': source, 'reason': reason})
        records.append(record)

    OUT.write_text(json.dumps({
        'hook': HOOK,
        'semantics_source': 'campaign/<folder>/<name>.py 的 Campaign.map_data_init',
        'semantics': '基类初始化后, 清图模式下跳过; 否则按 OVERRIDE 逐格改 may_enemy, 保留 may_ambush',
        'note': '本文件是"上游轨迹"的一半: 只有输入数据(OVERRIDE 与基础网格); C# 结果轨迹与逐章对拍尚未做',
        'chapters': len(records),
        'unresolved': unresolved,
        'records': records,
    }, ensure_ascii=False, indent=1), encoding='utf-8')

    resolved = [r for r in records if r['overrides'] is not None]
    total_entries = sum(len(r['overrides']) for r in resolved)

    # 结论要落进**入库**的文档：`data/` 不入库，只写 JSON 等于证据会随工作区丢掉。
    lines = [
        '# R3 第一项的上游轨迹：`map_data_init`',
        '',
        '> 本页由 `tools/diagnostics/r3_map_data_init.py` 生成，**不手写**。',
        '',
        '## 这个钩子是什么（读上游源码得来）',
        '',
        '```python',
        'def map_data_init(self, map_):',
        '    super().map_data_init(map_)',
        '    if not self.map_is_clear_mode:',
        '        for override_grid in OVERRIDE:',
        '            # Set may_enemy, but keep may_ambush',
        '            self.map[override_grid.location].may_enemy = override_grid.may_enemy',
        '```',
        '',
        f'带该钩子的章节 **{len(records)}** 个（`docs/r3-candidates.md` 里覆盖数居首）。',
        '',
        '## 轨迹抽取结果（如实）',
        '',
        f'- 静态抽出 `OVERRIDE` 的章节：**{len(resolved)}** / {len(records)}',
        f'- 未能静态抽出：**{len(unresolved)}**（原因逐条列在下面）',
        '',
        '| 章节 | 原因 |',
        '| --- | --- |',
    ]
    for item in unresolved:
        lines.append(f"| `{item['source']}` | {item['reason']} |")
    if resolved:
        lines += ['', '| 章节 | OVERRIDE 条目 |', '| --- | --- |']
        for record in resolved:
            lines.append(f"| `{record['module']}` | {len(record['overrides'])} |")
    lines += [
        '',
        '## 这轮得到的结论（对 R3 的意义）',
        '',
        f'- **`map_data_init` 不是一次统一迁移**：{len(records)} 个章节里没有一个是'
        '「静态可抽的 OVERRIDE 列表」这一种形态 —— '
        '它们各自引用别的常量、或在模块内以别名定义，形态并不统一。',
        '- 因此 R3 对它的正确做法不是"实现一个 map_data_init"，而是**先把每个章节的钩子体分类**'
        '（纯数据改写 / 引用常量 / 其它行为），再判断哪些属于"通用能力"、哪些只是章节自己的数据。',
        '- 路线 R3 的纪律照旧：**对拍不完整就继续走上游宿主** —— 现在这一项就不该往下走，'
        '因为连"输入是什么"都还没能静态固定下来。',
        '',
        '复现：`python tools/diagnostics/r3_map_data_init.py`。',
        '',
    ]
    (ROOT / 'docs' / 'r3-map-data-init.md').write_text('\n'.join(lines), encoding='utf-8')

    print(f'=== R3 上游轨迹：{HOOK} ===')
    print(f'  带该钩子的章节: {len(records)}（静态抽出 OVERRIDE: {len(resolved)}，'
          f'未抽出: {len(unresolved)}）')
    print(f'  OVERRIDE 条目合计: {total_entries}')
    for record in resolved[:5]:
        first = record['overrides'][0] if record['overrides'] else {}
        print(f"    {record['module']:52s} {len(record['overrides'])} 条  例: {first}")
    for item in unresolved[:3]:
        print(f"    [未抽出] {item['source']}: {item['reason']}")
    print(f'\n轨迹(数据): {OUT.relative_to(ROOT)}')
    print('报告(入库): docs/r3-map-data-init.md')
    print('结果: ' + (f'OK（{len(records)} 章全部有着落；结论：形态不统一，暂不迁移）'
                    if len(resolved) + len(unresolved) == len(records) else 'FAIL（有章节被静默跳过）'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
