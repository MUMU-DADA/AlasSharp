# -*- coding: utf-8 -*-
"""页面识别全量回归：用产品队列的 navigate 任务重跑已验证页面。

为什么值得单独跑：
- 之前的页面验证是分批做的（脚本点坐标导航），而导航后来换成了产品实现
  （运行时向上游要图 + 变体择优 + 未建模画面自救）。改了实现就要重新证明，
  否则"29 个页面已验证"是旧代码的结论。
- 一次跑完还能暴露"某些页只有从特定起点才到得了"这类顺序依赖。

判定：导航任务成功且随后 `page_current` 里确实包含该页。
"""
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import alas_vision as av          # noqa: E402
import adb_util                   # noqa: E402
from queue_navigation import run_navigation  # noqa: E402

try:
    ADB = os.environ['STUB_ADB']
except KeyError:
    # `--report-only` 只重建 docs/regression.md（不导航、不用 adb），不该被这个变量拦住；
    # 真机跑则给出**可照做的**报错，而不是一个光秃秃的 KeyError。
    if '--report-only' in sys.argv:
        ADB = ''
    else:
        raise SystemExit(
            '需要 STUB_ADB 环境变量（指向 adb 可执行文件）。PowerShell 例：\n'
            r'  $env:STUB_ADB = "<仓库>\.runtime\venv314\Lib\site-packages'
            r'\adbutils\binaries\adb.exe"' + '\n'
            '只想重建文档时用：--report-only（不需要该变量）')
SERIAL = os.environ.get('SERIAL', '127.0.0.1:16384')
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')
ALASHUB = os.environ.get('ALASHUB', os.path.join(
    HERE, '..', 'src', 'Alas.DataTool', 'bin', 'Release', 'net10.0', 'alashub.exe'))
PROGRESS = os.path.join(HERE, '..', 'docs', 'page-verification.json')
# 图里**没有入边**的页面不可能是导航目标 —— 它们是同一张画面的另一种状态（皮肤变体）
# 或浮层：page_main_white（新版主界面皮肤，与 page_main 同屏）、page_channel（临时浮层）、
# page_unknown（Page(None)）。对这些页面只能验"同屏被检测到"，不能验"能导航到"。
CO_DETECT = {
    'page_main_white': 'page_main',
    'page_channel': 'page_main',      # 频道是主界面上的浮层，只有 check 素材、无入口
}


def graph_info():
    """向上游要一次图，返回 (无入边节点集合, 节点数, 边数)。"""
    g = op('ui_page_graph')
    has_in = set()
    for node in g['nodes']:
        for link in node['links']:
            has_in.add(link['to'])
    no_in = sorted(n['name'] for n in g['nodes'] if n['name'] not in has_in)
    return set(no_in), g['node_count'], g['edge_count']


def op(op_name, **args):
    resp = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': op_name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(resp.get('error'))
    return resp['result']


def shot():
    adb_util.screencap(PROBE, SERIAL)
    op('screenshot_load', path=PROBE)
    return op('page_current')['hit']


def goto(page):
    r = run_navigation(ALASHUB, page, SERIAL, adb=ADB)
    hops = [l.strip() for l in (r.stdout or '').splitlines() if l.startswith('[hop')]
    return r.returncode == 0, hops


def main():
    report_only = '--report-only' in sys.argv
    no_in, node_n, edge_n = graph_info()
    if report_only:
        # 只重建 docs/regression.md：改文档措辞不该再跑一遍 5 分钟真机导航
        with open(os.path.join(HERE, '..', 'data', 'regress_pages.json'),
                  encoding='utf-8') as f:
            results = json.load(f)
        return write_report(results, no_in, node_n, edge_n)

    with open(PROGRESS, encoding='utf-8') as f:
        targets = sorted(json.load(f)['verified'])
    print('=== 页面识别全量回归：%d 个页面（图 %d 节点 / %d 边）==='
          % (len(targets), node_n, edge_n))
    print('图里无入边的节点（只能同屏检测，不能导航到）：%s' % ', '.join(no_in))
    if not adb_util.ensure(SERIAL):
        print('adb 未就绪')
        return 2

    results = []
    for i, page in enumerate(targets, 1):
        t0 = time.time()
        if page in no_in:
            # 不可导航：先到它的"同屏兄弟"页，再要求它同时被检测到
            sibling = CO_DETECT.get(page)
            if sibling is None:
                verdict, hops, pages = 'no-in-edge', [], shot()
            else:
                ok, hops = goto(sibling)
                pages = shot()
                verdict = 'ok' if (ok and page in pages) else 'co-detect-failed'
        else:
            ok, hops = goto(page)
            pages = shot()
            verdict = 'ok' if (ok and page in pages) else (
                'goto-failed' if not ok else 'not-detected')
        print('[%2d/%d] %-22s %-16s %5.1fs  命中=%s'
              % (i, len(targets), page, verdict, time.time() - t0, pages))
        for h in hops:
            print('        %s' % h)
        results.append({'page': page, 'verdict': verdict, 'hops': hops,
                        'observed': pages, 'no_in_edge': page in no_in,
                        'seconds': round(time.time() - t0, 1),
                        'navigation_entry': 'queue:navigate'})

    ok_n = sum(1 for r in results if r['verdict'] == 'ok')
    print()
    print('回归结果: %d/%d 通过' % (ok_n, len(results)))
    bad = [r for r in results if r['verdict'] != 'ok']
    if bad:
        print('未通过: %s' % ', '.join('%s(%s)' % (r['page'], r['verdict']) for r in bad))
    out = os.path.join(HERE, '..', 'data', 'regress_pages.json')
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(results, f, ensure_ascii=False, indent=2, default=str)
    print('明细: %s' % os.path.abspath(out))
    return write_report(results, no_in, node_n, edge_n)


def write_report(results, no_in, node_n, edge_n):
    """生成 docs/regression.md。独立成函数是为了 `--report-only` 能只重建文档。"""
    ok_n = sum(1 for r in results if r['verdict'] == 'ok')
    bad = [r for r in results if r['verdict'] != 'ok']
    entry_note = (
        '本次样本经 `alashub queue --file` 的 `navigate` 任务采集。'
        if results and all(r.get('navigation_entry') == 'queue:navigate' for r in results)
        else '当前存档是退役直接导航入口的历史样本；需重新运行脚本验证队列入口。')
    # ---- 账号前提：页面可达性受解锁进度影响，基线数字必须带上它。
    # 教训（2026-09-23）：29/34 与记录的 33/34 差 5 个页，一度被当成回归；
    # 实际是那条 33/34 测自**换号之前的旧号**。数字脱离账号前提就会误导。
    def account_line():
        try:
            with open(os.path.join(HERE, '..', 'data', 'account_probe.json'),
                      encoding='utf-8') as stream:
                probe = json.load(stream)
            chapters = probe.get('chapters') or []
            unlocked = [c['chapter'] for c in chapters if c.get('stages')]
            stages = sum(len(c.get('stages') or []) for c in chapters)
            return ('账号前提：按 `data/account_probe.json`，本次仅解锁 **%d 章 / %d 关**（%s）。'
                    '未解锁功能的入口不可达，会直接反映在上面的通过数里 —— '
                    '**换号后必须重记基线，不能与旧数字直接比较。**'
                    % (len(unlocked), stages,
                       '、'.join('第 %d 章' % c for c in unlocked) or '无'))
        except Exception:
            return ('账号前提：未找到 `data/account_probe.json`，**本次基线未记录账号解锁进度** —— '
                    '与历史数字比较前，先确认两次跑的是同一个账号。')

    # ---- 报告（生成 docs/regression.md，避免手写漂移）
    lines = [
        '# 页面识别全量回归（产品路径）',
        '',
        '用 `alashub queue --file` 的 `navigate` 任务对**已验证的每个页面**重跑一遍：既验证页面规则在各自页面上命中，',
        '也验证导航器（运行时取自上游的页面图 + 变体择优 + 未建模画面自救）本身没退化。',
        entry_note,
        '',
        '为什么需要单独做这一遍：早先的页面验证是分批做的（诊断脚本按资产坐标导航），',
        '后来导航换成了产品实现 —— 实现变了，"已验证"就必须重新证明。',
        '',
        '设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服）。',
        '脚本：`tools/diagnostics/regress_pages.py`；原始数据 `data/regress_pages.json`。',
        account_line(),
        '',
        '',
        '## 结果：%d / %d 通过' % (ok_n, len(results)),
        '',
        '| 页面 | 结果 | 耗时 | 跳数 | 导航输出 |',
        '| --- | --- | --- | --- | --- |',
    ]
    for r in results:
        lines.append('| `%s` | %s | %.1fs | %d | %s |'
                     % (r['page'], r['verdict'], r['seconds'], len(r['hops']),
                        '<br>'.join(h.replace('|', '/') for h in r['hops']) or '—'))
    lines += [
        '',
        '## 顺带发现：上游页面图里有"无入边"节点',
        '',
        '上游图共 %d 节点 / %d 边，其中 **%d 个节点没有任何入边**：%s。'
        % (node_n, edge_n, len(no_in), ', '.join('`%s`' % n for n in no_in)),
        '',
        '这类节点**不可能是导航目标**（没人能"走到"它），它们是：',
        '',
        '- `page_main_white`：同一张主界面的另一种皮肤，与 `page_main` 同屏命中；',
        '- `page_channel`：世界频道浮层，上游只定义了 check 素材、没有任何入口素材；',
        '- `page_unknown`：`Page(None)`，合成实体；',
        '- `page_rpg_city`：RPG 活动的城内界面，只有出边（回主界面/回剧情页），没有入边。',
        '',
        '所以对它们只能验"同屏被检测到"（本脚本用它的同屏兄弟页做锚点），',
        '不能验"能导航到"—— 这是上游的设计，不是缺陷。',
        '',
    ]
    if bad:
        lines += ['', '## 未通过', '']
        for r in bad:
            lines.append('- `%s`：%s（命中=%s）' % (r['page'], r['verdict'], r['observed']))
    lines += [
        '',
        '## 复现',
        '',
        '```powershell',
        '$env:STUB_ADB = "<adb.exe>"',
        'python tools/diagnostics/regress_pages.py',
        '```',
        '',
    ]
    path = os.path.join(HERE, '..', 'docs', 'regression.md')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    print('报告: %s' % os.path.abspath(path))
    return 0 if not bad else 1


if __name__ == '__main__':
    sys.exit(main())
