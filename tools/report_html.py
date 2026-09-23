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
    with tempfile.TemporaryDirectory(prefix='alas-html-') as tmp:
        out = Path(tmp) / 'report.json'
        proc = subprocess.run([str(EXE), 'report', '--artifacts', str(target), '--json', str(out)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=300)
        if not out.is_file():
            raise SystemExit(f'report 没产出 JSON（退出码={proc.returncode}）：{(proc.stdout or "")[-300:]}')
        return json.loads(out.read_text(encoding='utf-8'))


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


def render(report: dict) -> str:
    totals = report.get('totals') or {}
    parts = [
        '<!doctype html><meta charset="utf-8">',
        f'<title>运行报告 — {html.escape(str(report.get("run") or ""))}</title>',
        f'<style>{STYLE}</style>',
        f'<h1>运行报告</h1>',
        f'<div class="sub">{html.escape(str(report.get("run") or ""))}</div>',
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

    scopes = totals.get('log_scopes') or {}
    if scopes:
        parts.append('<h2>日志来源（谁在说话）</h2><div class="cards">')
        parts += [card(f'scope {name}', count) for name, count in sorted(scopes.items())]
        parts.append('</div>')

    items = report.get('items') or []
    tasks = [i for i in items if i.get('level') == 'task']
    stages = [i for i in items if i.get('level') == 'stage']
    if tasks:
        parts.append('<h2>任务</h2><table><tr><th>id</th><th>域</th><th>结论</th>'
                     '<th>错误分类</th><th>错误</th><th>边界快照</th></tr>')
        for item in tasks:
            boundary = item.get('boundary_state') or {}
            frame = '有' if boundary.get('available') else f'<span class="muted">无</span>'
            pages = boundary.get('pages')
            parts.append(
                f'<tr><td>{html.escape(str(item.get("id")))}</td>'
                f'<td>{html.escape(str(item.get("kind")))}</td>'
                f'<td class="{outcome_class(item.get("outcome"))}">{html.escape(str(item.get("outcome")))}</td>'
                f'<td>{html.escape(str(item.get("error_kind") or "—"))}</td>'
                f'<td>{html.escape(str(item.get("error") or "—"))}</td>'
                f'<td>{frame}{" " + html.escape(str(pages)) if pages else ""}</td></tr>')
        parts.append('</table>')
    if stages:
        parts.append('<h2>关卡</h2><table><tr><th>章节</th><th>关卡</th><th>结论</th>'
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
    parts.append('<h2>发现</h2>')
    if findings:
        parts.append('<table><tr><th>代码</th><th>说明</th></tr>' + ''.join(
            f'<tr><td>{html.escape(str(f.get("code")))}</td>'
            f'<td>{html.escape(str(f.get("message")))}</td></tr>' for f in findings) + '</table>')
    else:
        parts.append('<div class="sub">无（证据链完整、没有失败项）</div>')

    parts.append('<h2>原始数据面</h2><pre>' +
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
    report = build_report(target)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(render(report), encoding='utf-8')
    print(f'HTML 已写入: {out_path}')
    print(f'  队列={report.get("queue_outcome")} 批次={report.get("batch_outcome")} '
          f'条目={len(report.get("items") or [])} 发现={len(report.get("findings") or [])}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
