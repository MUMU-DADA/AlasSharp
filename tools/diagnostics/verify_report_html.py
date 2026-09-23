# -*- coding: utf-8 -*-
"""HTML 视图验收：界面能显示的东西，必须与数据面一致。

三段：

1. **不丢事实**：数据面 JSON 里的每个任务 id、每个发现代码、队列/批次结论，都必须出现在
   生成的 HTML 里 —— 这是"先做数据面再做界面"要守的那条线：**界面不许悄悄吞掉事实**。
2. **单文件自足**：HTML 里不出现 `http://` / `https://` / `src=` / `<link` 这类外部引用 ——
   双击就能看，不依赖网络、不依赖旁边的静态资源。
3. **缺工件也能看**：把运行目录里的一个工件删掉后再生成一次，HTML 仍能产出，且把
   `relocated_artifact` / `artifact_missing` 这类发现显示出来（界面不该因为数据不全就崩或空白）。

用法：
    python tools/diagnostics/verify_report_html.py
"""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'
GENERATOR = ROOT / 'tools' / 'report_html.py'
CHAPTER = 'campaign.campaign_main.campaign_2_1'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def run(*args, timeout=300):
    return subprocess.run([str(a) for a in args], capture_output=True, text=True,
                          encoding='utf-8', errors='replace', timeout=timeout)


def build_run(tmpdir: Path):
    queue_file = tmpdir / 'queue.json'
    queue_file.write_text(json.dumps({'tasks': [
        {'id': 'first', 'kind': 'campaign_batch', 'input': {'chapters': [CHAPTER]}},
        {'id': 'second', 'kind': 'task_schedule', 'input': {'limit': 3}},
    ]}, ensure_ascii=False), encoding='utf-8')
    artifacts = tmpdir / 'artifacts'
    run(EXE, 'queue', '--file', queue_file, '--artifacts', artifacts)
    return artifacts


def data_face(artifacts: Path) -> dict:
    out = artifacts.parent / 'report.json'
    run(EXE, 'report', '--artifacts', artifacts, '--json', out)
    return json.loads(out.read_text(encoding='utf-8'))


def render(artifacts: Path, html_path: Path):
    proc = run(sys.executable, GENERATOR, artifacts, '-o', html_path)
    return proc, (html_path.read_text(encoding='utf-8') if html_path.is_file() else '')


def main() -> int:
    if not EXE.is_file() or not GENERATOR.is_file():
        print('**失败**：缺 alashub 或 tools/report_html.py')
        return 1

    failures: list[str] = []
    with tempfile.TemporaryDirectory(prefix='alas-html-verify-') as tmp:
        tmpdir = Path(tmp)
        artifacts = build_run(tmpdir)
        if not list(artifacts.glob('*/queue.json')):
            print('**失败**：队列没产出工件')
            return 1
        report = data_face(artifacts)
        _, html = render(artifacts, tmpdir / 'view.html')

        print('=== 不丢事实（界面 vs 数据面）===')
        facts = []
        for item in report.get('items') or []:
            if item.get('id'):
                facts.append(('任务 id', str(item['id'])))
            if item.get('stage'):
                facts.append(('关卡', str(item['stage'])))
        for finding in report.get('findings') or []:
            facts.append(('发现代码', str(finding.get('code'))))
        missing = [f'{kind}={value}' for kind, value in facts if value not in html]
        checks = [
            ('数据面里的任务/关卡/发现都在 HTML 里出现', not missing,
             f'缺：{missing[:5]}（共 {len(facts)} 条事实）'),
            ('HTML 非空且含标题', '运行报告' in html, f'长度={len(html)}'),
            ('队列结论出现', str(report.get('queue_outcome')) in html,
             f"queue_outcome={report.get('queue_outcome')}"),
        ]
        for name, ok, detail in checks:
            print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
            if not ok:
                failures.append(f'{name}: {detail}')

        print()
        print('=== 单文件自足 ===')
        external = [token for token in ('http://', 'https://', '<link', 'src=') if token in html]
        ok = not external
        print(f"  {'ok  ' if ok else 'FAIL'} 无外部引用" + ('' if ok else f'  ← 命中 {external}'))
        if not ok:
            failures.append(f'HTML 有外部引用: {external}')

        print()
        print('=== 缺工件也能看 ===')
        run_dir = sorted(p for p in artifacts.glob('*') if p.is_dir())[-1]
        stage_artifacts = sorted(run_dir.glob('sortie-*.json'))
        if not stage_artifacts:
            print('  [跳过] 这次运行没有关卡工件（dry-run 也可能没有）')
        else:
            stage_artifacts[0].unlink()
            broken_report = data_face(artifacts)
            proc, broken_html = render(artifacts, tmpdir / 'broken.html')
            codes = [f.get('code') for f in broken_report.get('findings') or []]
            broken_checks = [
                ('删掉工件后仍能生成', proc.returncode == 0 and len(broken_html) > 0,
                 f'rc={proc.returncode} len={len(broken_html)}'),
                ('发现被显示出来', all(str(code) in broken_html for code in codes if code),
                 f'codes={codes}'),
                ('证据完整变为 False', broken_report.get('evidence_complete') is False,
                 f"evidence_complete={broken_report.get('evidence_complete')}"),
            ]
            for name, ok, detail in broken_checks:
                print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
                if not ok:
                    failures.append(f'{name}: {detail}')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（界面不丢事实、单文件自足、缺工件也能看）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
