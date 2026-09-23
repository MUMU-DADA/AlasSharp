# -*- coding: utf-8 -*-
"""把一次运行的报告渲染成**单文件 HTML**（R4 的第一个界面交付物）。

为什么是这个形态：

* **不需要框架决策**：产物是一个自带样式的 HTML 文件，双击就开，不引入任何前端栈；
* **不需要设备**：它只消费 `alashub report --json` 的输出，而那是已经验收过的数据面；
* **顺带是一次架构检查**：如果某些信息在 HTML 里显示不出来，说明**数据面缺字段** ——
  这正是"先做数据面、再做界面"要暴露的东西，而不是等界面写完才发现。

用法：
    python tools/report_html.py <运行目录或 artifacts 根目录> [-o 输出.html]
"""

from __future__ import annotations

import html
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

STYLE = """
body { font: 14px/1.5 -apple-system, "Segoe UI", "Microsoft YaHei", sans-serif;
       margin: 0; padding: 24px; background: #f6f7f9; color: #1c1f23; }
h1 { font-size: 20px; margin: 0 0 4px; }
h2 { font-size: 15px; margin: 24px 0 8px; color: #444; }
.sub { color: #6b7280; font-size: 12px; margin-bottom: 16px; }
.cards { display: flex; flex-wrap: wrap; gap: 12px; }
.card { background: #fff; border: 1px solid #e3e6ea; border-radius: 8px;
        padding: 10px 14px; min-width: 130px; }
.card .k { color: #6b7280; font-size: 11px; text-transform: uppercase; letter-spacing: .04em; }
.card .v { font-size: 18px; margin-top: 2px; }
table { border-collapse: collapse; width: 100%; background: #fff;
        border: 1px solid #e3e6ea; border-radius: 8px; overflow: hidden; }
th, td { text-align: left; padding: 7px 10px; border-bottom: 1px solid #eef0f3; }
th { background: #fafbfc; color: #6b7280; font-weight: 600; font-size: 12px; }
tr:last-child td { border-bottom: none; }
.ok { color: #0a7c3f; font-weight: 600; }
.bad { color: #b42318; font-weight: 600; }
.warn { color: #b45309; font-weight: 600; }
.muted { color: #9aa1a9; }
pre { background: #fff; border: 1px solid #e3e6ea; border-radius: 8px;
      padding: 10px; overflow-x: auto; font-size: 12px; }
"""


def build_report(target: Path) -> dict:
    """取一次运行的报告 JSON。

    **参数要按目标类型分开**（这里踩过）：`report --run <运行目录>` 针对**具体某次运行**，
    `report --artifacts <根目录>` 只取**最近一次**。早先一律用 `--artifacts`，
    在"根目录下只有一次运行"时碰巧对，多于一次时就会去读最近那次、把别的页也渲染成同一份。
    """
    selector = '--artifacts' if is_root(target) else '--run'
    with tempfile.TemporaryDirectory(prefix='alas-html-') as tmp:
        out = Path(tmp) / 'report.json'
        proc = subprocess.run([str(EXE), 'report', selector, str(target), '--json', str(out)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=300)
        if not out.is_file():
            raise SystemExit(f'report 没产出 JSON（退出码={proc.returncode}）：{(proc.stdout or "")[-300:]}')
        return json.loads(out.read_text(encoding='utf-8'))


def build_runs(target: Path) -> dict:
    """artifacts 根目录下的**多次运行**列表（`alashub runs --json`）。"""
    with tempfile.TemporaryDirectory(prefix='alas-html-') as tmp:
        out = Path(tmp) / 'runs.json'
        proc = subprocess.run([str(EXE), 'runs', '--artifacts', str(target), '--json', str(out)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=300)
        if not out.is_file():
            raise SystemExit(f'runs 没产出 JSON（退出码={proc.returncode}）：{(proc.stdout or "")[-300:]}')
        return json.loads(out.read_text(encoding='utf-8'))


def is_root(target: Path) -> bool:
    """是不是 artifacts 根目录（下面有多个运行目录）。"""
    if (target / 'queue.json').is_file() or (target / 'index.json').is_file():
        return False
    return any((child / 'queue.json').is_file() or (child / 'index.json').is_file()
               for child in target.iterdir() if child.is_dir())


def render_index(target: Path, document: dict) -> str:
    """多次运行的索引页：每行一次运行，链到各自的 report.html。"""
    rows = []
    for entry in document.get('runs') or []:
        name = Path(str(entry.get('directory') or '')).name
        link = f'../{target.name}-views/{name}.html'
        rows.append(
            f'<tr><td><a href="{html.escape(link)}">{html.escape(name)}</a></td>'
            f'<td class="{outcome_class(entry.get("queue_outcome"))}">'
            f'{html.escape(str(entry.get("queue_outcome") or "—"))}</td>'
            f'<td class="{outcome_class(entry.get("batch_outcome"))}">'
            f'{html.escape(str(entry.get("batch_outcome") or "—"))}</td>'
            f'<td>{html.escape(str(entry.get("tasks", "—")))}</td>'
            f'<td>{html.escape(str(entry.get("stages", "—")))}</td>'
            f'<td>{"✔" if entry.get("evidence_complete") else "✗"}</td></tr>')
    return '\n'.join([
        '<!doctype html><meta charset="utf-8">',
        f'<title>运行列表 — {html.escape(target.name)}</title>',
        f'<style>{STYLE}</style>',
        '<h1>运行列表</h1>',
        f'<div class="sub">{html.escape(str(target))} —— 共 {len(rows)} 次运行</div>',
        '<table><tr><th>运行</th><th>队列</th><th>批次</th><th>任务</th><th>关卡</th>'
        '<th>证据完整</th></tr>' + ''.join(rows) + '</table>',
        '<div class="sub">点运行名进入该次的详细视图（每个页面都是单文件，可单独拷走）。</div>',
    ])


def evidence_summary(artifact_path, limit: int = 4) -> str:
    """从任务的工件里取**标量摘要**（如 batch_outcome / cleared / enabled_count / pages）。

    为什么在这里读工件、而不是往报告里塞字段：报告只内联摘要、明细留在 `task-*.json`
    （见 `docs/runtime.md` 第九节）。界面要显示的是"一眼能看懂的那几个数"，
    按需读工件既能拿到它们，又不改变数据面已有的取舍。
    """
    if not artifact_path:
        return ''
    try:
        document = json.loads(Path(str(artifact_path)).read_text(encoding='utf-8'))
    except Exception:
        return ''
    parts = []
    for key, value in (document.get('evidence') or {}).items():
        if isinstance(value, (str, int, float, bool)) and value not in (None, ''):
            parts.append(f'{key}={value}')
        if len(parts) >= limit:
            break
    return ' · '.join(parts)


def outcome_class(value) -> str:
    text = str(value)
    if text in ('cleared', 'succeeded', 'dry_run', 'ok'):
        return 'ok'
    if text in ('failed', 'error'):
        return 'bad'
    if text in ('cancelled', 'partial', 'skipped'):
        return 'warn'
    return 'muted'


def card(key: str, value) -> str:
    return f'<div class="card"><div class="k">{html.escape(key)}</div>' \
           f'<div class="v">{html.escape(str(value))}</div></div>'


def failures_of(items: list) -> list:
    """失败与未通关的条目：**按结论判，不按"有没有通关"判**。

    判据是被测试逼出来的：起初写的是"关卡侧看 `cleared` 是否 false"，于是**dry-run 的关卡
    全被列成失败**（dry-run 本来就不会通关）—— 这违反本项目的一条硬规矩：
    **"没跑"与"跑失败"必须分开**（`AGENTS.md` 任务域边界）。
    所以这里只收失败类结论：failed / error / incomplete / defeated / ended_unknown / withdrawn
    （撤退是真实事件，值得摆出来）；dry_run / skipped 一律不算。
    """
    bad = ('failed', 'error', 'incomplete', 'defeated', 'ended_unknown', 'withdrawn')
    out = []
    for item in items:
        outcome = str(item.get('outcome'))
        if item.get('level') == 'task' and outcome in ('failed', 'error'):
            out.append(item)
        elif item.get('level') == 'stage' and outcome in bad:
            out.append(item)
    return out


def failure_row(item: dict) -> str:
    what = item.get('id') or f"{item.get('chapter')}/{item.get('stage')}"
    return (f'<tr><td>{html.escape(str(what))}</td>'
            f'<td>{html.escape(str(item.get("level")))}</td>'
            f'<td class="{outcome_class(item.get("outcome"))}">'
            f'{html.escape(str(item.get("outcome")))}</td>'
            f'<td>{html.escape(str(item.get("error") or "—"))}</td></tr>')


def render(report: dict) -> str:
    totals = report.get('totals') or {}
    items_all = report.get('items') or []
    failures = failures_of(items_all)
    parts = [
        '<!doctype html><meta charset="utf-8">',
        f'<title>运行报告 — {html.escape(str(report.get("run") or ""))}</title>',
        f'<style>{STYLE}</style>',
        f'<h1>运行报告</h1>',
        f'<div class="sub">{html.escape(str(report.get("run") or ""))}</div>',
        # 页首锚点：一次运行可能有几十个关卡，先给一条能跳的路（纯 HTML，无 JS）
        '<div class="sub">跳到：'
        '<a href="#failures">失败与未通关</a> · '
        '<a href="#tasks">任务</a> · '
        '<a href="#stages">关卡</a> · '
        '<a href="#findings">发现</a> · '
        '<a href="#raw">原始数据面</a></div>',
        '<div class="cards">',
        card('队列结论', report.get('queue_outcome') or '—'),
        card('批次结论', report.get('batch_outcome') or '—'),
        card('提前停止', report.get('stopped_early')),
        card('停止原因', report.get('stop_reason') or '—'),
        card('工件数', totals.get('artifacts')),
        card('宿主启动', report.get('host_start_count')),
        card('设备配置', report.get('device_configure_count')),
        card('日志条目', totals.get('log_entries')),
        card('日志错误', totals.get('log_errors')),
        card('证据完整', report.get('evidence_complete')),
        '</div>',
    ]

    # 失败优先区块：打开报告先看"哪儿出问题了"（生成期过滤，无 JS）
    parts.append('<h2 id="failures">失败与未通关</h2>')
    if failures:
        parts.append('<table><tr><th>条目</th><th>层</th><th>结论</th><th>错误</th></tr>'
                     + ''.join(failure_row(i) for i in failures) + '</table>')
    else:
        parts.append('<div class="sub">无（这一份里没有失败项）</div>')
    scopes = totals.get('log_scopes') or {}
    if scopes:
        parts.append('<h2>日志来源（谁在说话）</h2><div class="cards">')
        parts += [card(f'scope {name}', count) for name, count in sorted(scopes.items())]
        parts.append('</div>')

    items = report.get('items') or []
    tasks = [i for i in items if i.get('level') == 'task']
    stages = [i for i in items if i.get('level') == 'stage']
    if tasks:
        parts.append('<h2 id="tasks">任务</h2><table><tr><th>id</th><th>域</th><th>结论</th>'
                     '<th>摘要</th><th>错误分类</th><th>错误</th><th>边界快照</th></tr>')
        for item in tasks:
            boundary = item.get('boundary_state') or {}
            frame = '有' if boundary.get('available') else f'<span class="muted">无</span>'
            pages = boundary.get('pages')
            parts.append(
                f'<tr><td>{html.escape(str(item.get("id")))}</td>'
                f'<td>{html.escape(str(item.get("kind")))}</td>'
                f'<td class="{outcome_class(item.get("outcome"))}">{html.escape(str(item.get("outcome")))}</td>'
                f'<td class="muted">{html.escape(evidence_summary(item.get("artifact")))}</td>'
                f'<td>{html.escape(str(item.get("error_kind") or "—"))}</td>'
                f'<td>{html.escape(str(item.get("error") or "—"))}</td>'
                f'<td>{frame}{" " + html.escape(str(pages)) if pages else ""}</td></tr>')
        parts.append('</table>')
    if stages:
        parts.append('<h2 id="stages">关卡</h2><table><tr><th>章节</th><th>关卡</th><th>结论</th>'
                     '<th>通关</th><th>错误</th></tr>')
        for item in stages:
            parts.append(
                f'<tr><td>{html.escape(str(item.get("chapter")))}</td>'
                f'<td>{html.escape(str(item.get("stage")))}</td>'
                f'<td class="{outcome_class(item.get("outcome"))}">{html.escape(str(item.get("outcome")))}</td>'
                f'<td>{"✔" if item.get("cleared") else "—"}</td>'
                f'<td>{html.escape(str(item.get("error") or "—"))}</td></tr>')
        parts.append('</table>')

    findings = report.get('findings') or []
    parts.append('<h2 id="findings">发现</h2>')
    if findings:
        parts.append('<table><tr><th>代码</th><th>说明</th></tr>' + ''.join(
            f'<tr><td>{html.escape(str(f.get("code")))}</td>'
            f'<td>{html.escape(str(f.get("message")))}</td></tr>' for f in findings) + '</table>')
    else:
        parts.append('<div class="sub">无（证据链完整、没有失败项）</div>')

    parts.append('<h2 id="raw">原始数据面</h2><pre>' +
                 html.escape(json.dumps(report, ensure_ascii=False, indent=1)[:4000]) + '</pre>')
    return '\n'.join(parts)


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先 dotnet build）')
        return 1
    target = Path(sys.argv[1]).resolve()
    out_path = Path(sys.argv[sys.argv.index('-o') + 1]).resolve() if '-o' in sys.argv \
        else target / 'report.html'

    # artifacts 根目录（下面有多次运行）→ 生成索引页 + 每次运行各一页
    if is_root(target):
        document = build_runs(target)
        index_path = out_path if out_path.name != 'report.html' else target / 'index.html'
        index_path.parent.mkdir(parents=True, exist_ok=True)
        index_path.write_text(render_index(target, document), encoding='utf-8')
        # **页面写到 <root>/views/，不写进运行目录**：运行目录里的文件数是"工件数"的一部分，
        # 往里塞生成物会篡改证据记录（实测过一次：工件数会从 3 变 4）。
        views = target.parent / (target.name + '-views')   # 写到根目录之外：report --artifacts 会扫根目录的子目录取最近一次，views/ 放在里面会被当成一次运行（实测报 log_missing）
        views.mkdir(exist_ok=True)
        made = 0
        for entry in document.get('runs') or []:
            run_dir = Path(str(entry.get('directory') or ''))
            if not run_dir.is_dir():
                continue
            (views / f'{run_dir.name}.html').write_text(
                render(build_report(run_dir)), encoding='utf-8')
            made += 1
        print(f'索引已写入: {index_path}（{made} 次运行各生成一页，页面在 {views.name}/）')
        return 0

    report = build_report(target)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(render(report), encoding='utf-8')
    print(f'HTML 已写入: {out_path}')
    print(f'  队列={report.get("queue_outcome")} 批次={report.get("batch_outcome")} '
          f'条目={len(report.get("items") or [])} 发现={len(report.get("findings") or [])}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
