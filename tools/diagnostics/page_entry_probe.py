# -*- coding: utf-8 -*-
"""决定性检查：进不去的那 5 个页面，是"未解锁"还是"走到一半失败"。

背景（上一轮）：真机页面回归从已知状态重跑，稳定得到 29/34，失败的 5 个页
（`page_event_list` / `page_guild` / `page_meowfficer` / `page_os` / `page_private_quarters`）
全是 `goto-failed`。已记录的 33/34 基线跑于**换号之前**，而当前账号只解锁第 1 章。

判据（便宜且决定性）：站在 `page_main` 上，沿上游页面图求出到每个目标页的**第一跳按钮**，
在**实时帧**上量它是否在屏：

  * 第一跳按钮不在屏上 → 该入口在这台账号上根本不存在 → **未解锁**，不是回归；
  * 第一跳按钮在屏上却走不到 → 才是真的导航问题，需要继续查。

只读：只抓一帧 + 只做判据，不点击。
用法：
    python tools/diagnostics/page_entry_probe.py
"""

from __future__ import annotations

import json
import os
import sys
from collections import deque
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
DATA = ROOT / 'data'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

os.chdir(ENGINE)
sys.path.insert(0, str(ROOT / 'tools'))
import alas_vision as av                                        # noqa: E402

TARGETS = ['page_event_list', 'page_guild', 'page_meowfficer', 'page_os',
           'page_private_quarters']
STARTS = ['page_main', 'page_main_white']


def graph_edges():
    """上游页面图 → {page: [(button, target), ...]}（按钮是资产 id）。

    **形状假设必须先验**：实测第一次跑时这里静默解析出 0 条边，脚本却报"未得出" ——
    那种"看起来跑完了、其实什么都没解析到"的输出比报错更危险。所以这里形状不符就**立刻中止**，
    并把实际的键打印出来，方便下一次照着改。
    """
    graph = av.op_ui_page_graph({})
    keys = sorted(graph.keys()) if isinstance(graph, dict) else type(graph).__name__
    nodes = graph.get('nodes') if isinstance(graph, dict) else None
    if not isinstance(nodes, list) or not nodes:
        raise SystemExit(f'页面图形状不符：顶层键={keys}；本脚本需要 `nodes` 列表。'
                         f'（先看 op_ui_page_graph 的真实返回再改解析）')
    edges, sample = {}, None
    for node in nodes:
        # 实测形状（由本函数的"形状不符就中止"打印出来）：节点是
        # `{"name": "page_x", "check": "ui/X_CHECK", "links": [{"to": ..., "button": ..., "variants": [...]}]}`
        # —— 一开始我按 `page`/`edges`/`target` 解析，静默得到 0 条边。
        page = node.get('name')
        edges[page] = [(link.get('button'), link.get('to'), link.get('variants') or [])
                       for link in node.get('links', [])]
        if sample is None and node.get('links'):
            sample = node
    total = sum(len(v) for v in edges.values())
    if total == 0:
        raise SystemExit(f'页面图解析出 0 条边：顶层键={keys}；'
                         f'首个节点样例={json.dumps(sample or nodes[0], ensure_ascii=False)[:300]}')
    print(f'  页面图: {len(edges)} 页 / {total} 条边')
    return edges, graph


def first_hop(edges, starts, target):
    """BFS：从起始页集合到目标页的最短路径，返回 (第一跳按钮候选, 路径)。

    **返回的是候选列表（含 variants）**：上游页面图的每条边带 `variants`
    （例如 `ui/GOTO_MAIN` 与 `ui_white/GOTO_MAIN_WHITE`），本客户端主界面是新版白皮，
    只测默认变体会得出"入口不在屏上"的**错误结论** —— 实测踩过：
    `ui/MAIN_GOTO_CAMPAIGN` 不在屏（175.57），而 `ui_white/MAIN_GOTO_CAMPAIGN_WHITE` 才是对的。
    """
    queue = deque((start, []) for start in starts if start in edges)
    seen = set(starts)
    while queue:
        page, path = queue.popleft()
        for button, nxt, variants in edges.get(page, []):
            if nxt is None or nxt in seen:
                continue
            step = path + [(page, button, nxt)]
            if nxt == target:
                candidates = list(variants) or [button]
                return (candidates, [f'{p} -{b}-> {n}' for p, b, n in step])
            seen.add(nxt)
            queue.append((nxt, step))
    return (None, [])


def main() -> int:
    print('=== 页面入口存在性检查（只读：抓一帧 + 量判据，不点击）===')
    av.op_device_capture_set({'raw': True})
    current = av.op_page_current({})['hit']
    print(f'  当前画面: {current}')
    if not any(page in current for page in STARTS):
        print(f'  [跳过] 当前不在 {" / ".join(STARTS)}；先把画面带回主界面再跑本检查。')
        return 0

    edges, graph = graph_edges()
    print(f'  页面图: {len(edges)} 页')
    rows, missing, present = [], 0, 0
    for target in TARGETS:
        candidates, path = first_hop(edges, STARTS, target)
        if candidates is None:
            rows.append({'page': target, 'first_hop': None, 'on_screen': None,
                         'note': '页面图里从主界面不可达'})
            continue
        # 逐个变体量，取**最像的那个**（与导航器"按分数择优"同一口径）。
        best = None
        for asset in candidates:
            try:
                appear = av.op_appear_on({'asset': asset})
                tolerance = appear.get('tolerance')
            except Exception:
                continue
            if tolerance is None:
                continue
            if best is None or tolerance < best['similarity']:
                best = {'asset': asset, 'similarity': round(float(tolerance), 2),
                        'color': appear.get('color'), 'expected': appear.get('expected')}
        on_screen = None if best is None else best['similarity'] < 10.0
        missing += 1 if on_screen is False else 0
        present += 1 if on_screen else 0
        rows.append({'page': target, 'candidates': candidates, 'best': best,
                     'on_screen': on_screen, 'path': path})
        print(f"  {target:24s} 最佳变体={str(best and best['asset']):40s} "
              f"在屏={on_screen} 相似度={best and best['similarity']}")

    verdict = ('入口不在屏上 → 未解锁（不是回归）' if missing and not present
               else '入口在屏上 → 需要继续查导航' if present and not missing
               else '混合：部分入口存在、部分不存在' if present and missing
               else '未得出（判据取不到）')
    print(f'  判定: {verdict}')
    (DATA / 'page_entry_probe.json').write_text(json.dumps(
        {'current_pages': current, 'graph_pages': len(edges), 'verdict': verdict,
         'entries': rows}, ensure_ascii=False, indent=1), encoding='utf-8')
    print('  证据: data/page_entry_probe.json')
    return 0


if __name__ == '__main__':
    sys.exit(main())
