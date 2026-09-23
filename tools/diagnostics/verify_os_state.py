# -*- coding: utf-8 -*-
"""R2 第三域验收：大世界/海域只读探针（`kind = "os_state"`）。

两件事（加一件接线回归）：

1. **只读探针跑得通**：用存盘帧跑 `os_state` 任务，判据来自宿主已有的
   `map_detect(mode="os")` 产品路径，本脚本只核对证据字段与工件，不重复实现识别。
2. **"没跑"不等于"跑失败"**：`capture=true` 在 dry-run 下必须记 **skipped + 原因**，
   而不是 failed —— 这正是 R2 通用任务模型的核心口径，顺手在这里也验一遍。
3. **断点续跑的接线**：跑两次真实 CLI，第二次必须读到上一次的 `state.json`。
   为什么要专门测接线：`selftest-runtime` 的用例直接往 `ResumeCompleted` 塞 id（单元路径），
   曾经因此漏掉"CLI 读的是本次运行目录、永远读不到断点"这个真实缺陷。

"没检测到"（`detected=false`）是**有效状态**：可能这一帧不在海域里（夹具是球面/大世界帧）。
所以断言的是"字段齐全、结论是跑通"，不是"必须 detected=true"。

用法：
    python tools/diagnostics/verify_os_state.py
没有 `data/fixtures/os_map.png` 时显式跳过（不静默通过）。
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DATA = ROOT / 'data'
sys.path.insert(0, str(ROOT / 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net10.0' / 'alashub.exe'
FIXTURE = DATA / 'fixtures' / 'os_map.png'
GLOBE_FIXTURE = DATA / 'fixtures' / 'os_globe_view.png'


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先运行 dotnet build）')
        return 1
    if not FIXTURE.is_file():
        print(f'[跳过] 没有 {FIXTURE.relative_to(ROOT)}（`alashub map` 的默认夹具）；'
              f'大世界探针的帧断言未跑。')
        return 0

    failures: list[str] = []
    with TemporaryDirectory(prefix='alas-os-state-') as tmp:
        tmpdir = Path(tmp)
        queue_file = tmpdir / 'queue.json'
        tasks = [
            {'id': 'os-map', 'kind': 'os_state',
             'input': {'screenshot': str(FIXTURE.resolve())}},
            # dry-run 下 capture=true 必须被前置条件拦下（记 skipped，不是 failed）
            {'id': 'os-capture-dry', 'kind': 'os_state', 'input': {'capture': True}},
            {'id': 'os-invalid-detect', 'kind': 'os_state',
             'input': {'screenshot': str(FIXTURE.resolve()), 'detect': 'unknown'}},
            {'id': 'os-invalid-capture', 'kind': 'os_state',
             'input': {'screenshot': str(FIXTURE.resolve()), 'capture': 'false'}},
        ]
        if GLOBE_FIXTURE.is_file():
            tasks.append({'id': 'os-globe', 'kind': 'os_state',
                          'input': {'screenshot': str(GLOBE_FIXTURE.resolve()),
                                    'detect': 'globe'}})
        else:
            print(f'[跳过] 没有 {GLOBE_FIXTURE.relative_to(ROOT)}；globe 分支未验。')
        queue_file.write_text(json.dumps({'tasks': tasks}, ensure_ascii=False, indent=1),
                              encoding='utf-8')
        artifacts = tmpdir / 'artifacts'
        proc = subprocess.run([str(EXE), 'queue', '--file', str(queue_file),
                               '--artifacts', str(artifacts)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=300)
        if proc.returncode != 0:
            failures.append(f'alashub queue 退出码 {proc.returncode}')
            print((proc.stdout or '')[-1500:])
        run_dirs = sorted(p for p in artifacts.glob('*') if p.is_dir())
        if not run_dirs:
            print('**失败**：没有产出运行目录')
            return 1
        run_dir = run_dirs[-1]
        queue_artifact = json.loads((run_dir / 'queue.json').read_text(encoding='utf-8'))

        print('=== 大世界/海域只读探针（存盘帧，无设备）===')
        checks: list[tuple[str, bool, str]] = []

        map_artifact_path = run_dir / 'task-os-map.json'
        if not map_artifact_path.is_file():
            failures.append('缺少 task-os-map.json')
            evidence = {}
            outcome = None
        else:
            artifact = json.loads(map_artifact_path.read_text(encoding='utf-8'))
            outcome = artifact['outcome']
            evidence = artifact.get('evidence') or {}
        checks += [
            ('探针任务成功', outcome == 'succeeded', f'outcome={outcome}'),
            ('证据带 mode=os', evidence.get('mode') == 'os', f"mode={evidence.get('mode')}"),
            ('证据带来源', str(evidence.get('source', '')).startswith('file:'),
             f"source={evidence.get('source')}"),
            ('证据带帧形状', (evidence.get('frame') or {}).get('shape') == [720, 1280, 3],
             f"frame={evidence.get('frame')}"),
            ('证据带 detected 字段', 'detected' in evidence, '缺 detected'),
            ('证据带 grid_count 字段', 'grid_count' in evidence, '缺 grid_count'),
            ('队列结论 partial（成功与跳过并存）', queue_artifact['outcome'] == 'partial',
             f"outcome={queue_artifact['outcome']}"),
        ]

        globe_evidence = {}
        if GLOBE_FIXTURE.is_file():
            globe_artifact_path = run_dir / 'task-os-globe.json'
            globe_artifact = json.loads(globe_artifact_path.read_text(encoding='utf-8')) \
                if globe_artifact_path.is_file() else {}
            globe_evidence = globe_artifact.get('evidence') or {}
            globe = globe_evidence.get('globe') or {}
            checks += [
                ('globe 探针成功', globe_artifact.get('outcome') == 'succeeded',
                 f"outcome={globe_artifact.get('outcome')} error={globe_artifact.get('error')}"),
                ('globe 保留上游单应性与坐标结果',
                 globe_evidence.get('detect') == 'globe'
                 and globe.get('load') == 'ok'
                 and bool(globe.get('homo_data'))
                 and bool(globe.get('screen2globe'))
                 and bool(globe.get('globe2screen')),
                 f"load={globe.get('load')} roundtrip={globe.get('roundtrip_error')}"),
                ('globe 保留中心与上游日志',
                 bool(globe_evidence.get('globe_center'))
                 and bool(globe_evidence.get('log_lines')),
                 f"center={globe_evidence.get('globe_center')}"),
            ]

        for task_id, field in [('os-invalid-detect', 'input.detect'),
                               ('os-invalid-capture', 'input.capture')]:
            path = run_dir / f'task-{task_id}.json'
            artifact = json.loads(path.read_text(encoding='utf-8')) if path.is_file() else {}
            checks.append((f'{task_id} 被前置条件拦下',
                           artifact.get('outcome') == 'skipped'
                           and artifact.get('stop_reason') == 'precondition'
                           and any(field in reason for reason in artifact.get('unmet_preconditions') or []),
                           f"outcome={artifact.get('outcome')} unmet={artifact.get('unmet_preconditions')}"))

        capture_artifact_path = run_dir / 'task-os-capture-dry.json'
        if not capture_artifact_path.is_file():
            failures.append('缺少 task-os-capture-dry.json')
            capture = {}
        else:
            capture = json.loads(capture_artifact_path.read_text(encoding='utf-8'))
        checks += [
            ('capture 在 dry-run 记 skipped（不是 failed）', capture.get('outcome') == 'skipped',
             f"outcome={capture.get('outcome')}"),
            ('skipped 带原因', 'capture=true' in str(capture.get('error', '')),
             f"error={capture.get('error')}"),
            # dry-run 且没给 serial 时，两条前置条件同时不满足（dry-run 不碰设备 + 需要 serial）——
            # 断言"被记下来了且说清了 dry-run 这条"，不写死条数。
            ('前置条件被记录',
             bool(capture.get('unmet_preconditions'))
             and any('真跑会话' in text for text in capture.get('unmet_preconditions') or []),
             f"unmet={capture.get('unmet_preconditions')}"),
        ]

        for name, ok, detail in checks:
            print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
            if not ok:
                failures.append(f'{name}: {detail}')

        (DATA / 'os_state_probe.json').write_text(json.dumps({
            'note': '大世界只读探针的实测证据（detected=false 表示该帧不在海域里，是有效状态）',
            'evidence': evidence,
            'globe_evidence': globe_evidence if GLOBE_FIXTURE.is_file() else None,
            'capture_precondition': {'outcome': capture.get('outcome'),
                                     'error': capture.get('error'),
                                     'unmet_preconditions': capture.get('unmet_preconditions')},
        }, ensure_ascii=False, indent=1), encoding='utf-8')
        print('  证据已写入: data/os_state_probe.json')

        # ---- 断点续跑的**接线**回归：跑两次真实 CLI，第二次必须读到上一次的 state.json。
        # 为什么要专门测接线：`selftest-runtime` 的用例是直接往 ResumeCompleted 塞 id（单元路径），
        # 曾经因此漏掉"CLI 读的是本次运行目录、永远读不到断点"这个真实缺陷。
        print()
        print('=== 断点续跑接线（两次真实 CLI）===')
        resume_queue = tmpdir / 'resume-queue.json'
        resume_queue.write_text(json.dumps({'tasks': [
            {'id': 'a-os', 'kind': 'os_state', 'input': {'screenshot': str(FIXTURE.resolve())}},
            {'id': 'b-bad', 'kind': 'campaign_batch', 'input': {'chapters': []}, 'required': True},
        ]}, ensure_ascii=False, indent=1), encoding='utf-8')
        resume_root = tmpdir / 'resume-artifacts'
        first = subprocess.run([str(EXE), 'queue', '--file', str(resume_queue),
                                '--artifacts', str(resume_root)],
                               capture_output=True, text=True, encoding='utf-8',
                               errors='replace', timeout=300)
        second = subprocess.run([str(EXE), 'queue', '--file', str(resume_queue),
                                 '--artifacts', str(resume_root), '--resume'],
                                capture_output=True, text=True, encoding='utf-8',
                                errors='replace', timeout=300)
        out2 = second.stdout or ''
        # 再放一个**排序靠后**的杂目录（名字在时间戳之后），里面塞一份"什么都没完成"的
        # state.json —— 判定口径若被绕过，`--resume` 会用它，于是"跳过已完成任务"那句话就没了。
        stray = resume_root / 'zzz-bogus'
        stray.mkdir(parents=True, exist_ok=True)
        (stray / 'state.json').write_text(json.dumps({'completed': []}), encoding='utf-8')
        third = subprocess.run([str(EXE), 'queue', '--file', str(resume_queue),
                                '--artifacts', str(resume_root), '--resume'],
                               capture_output=True, text=True, encoding='utf-8',
                               errors='replace', timeout=300)
        out3 = third.stdout or ''
        # 消费侧容忍度：断点改成累积语义后会写出 `carried_over` 值（继承来的），
        # 报告读 state.json 不能因此报出 state 相关发现 —— 这条手工验过一次，现在钉进套件。
        # 取"真正的运行目录"用与产品同一口径（含 queue.json），顺带也验证了杂目录不算运行。
        real_runs = sorted(p for p in resume_root.glob('*')
                           if p.is_dir() and (p / 'queue.json').is_file())
        resume_report_json = tmpdir / 'resume-report.json'
        subprocess.run([str(EXE), 'report', '--run', str(real_runs[-1]),
                        '--json', str(resume_report_json)],
                       capture_output=True, text=True, encoding='utf-8',
                       errors='replace', timeout=300)
        resume_report = json.loads(resume_report_json.read_text(encoding='utf-8')) \
            if resume_report_json.is_file() else {}
        resume_checks = [
            ('第一次运行产出断点文件',
             bool(list(resume_root.glob('*/state.json'))), '没有 state.json'),
            ('第二次运行读到上一次的断点',
             '跳过 1 个已完成任务' in out2 and 'a-os' in out2,
             f'stdout 里没有续跑记录: {[l for l in out2.splitlines() if "断点" in l][:2]}'),
            ('已完成任务记 skipped（不是重跑）',
             'outcome=skipped' in out2 and '断点续跑' in out2, '任务的续跑结论不对'),
            # 与 report/runs 同一口径：杂目录不是一次运行，断点只从真运行里取。
            # 同时这条还盯着**累积语义**：第 2 次续跑（A 跳过、B 失败）之后，A 仍必须留在
            # "已完成"里 —— 若写入时只取本次切片，第 3 次就会把 A 重新执行（战役域＝再花一次石油）。
            ('杂目录不算运行，且断点累积（A 仍被跳过，不重跑）',
             'zzz-bogus' not in out3 and '跳过 1 个已完成任务' in out3 and 'a-os' in out3,
             f'第三次续跑不对: {[l for l in out3.splitlines() if "断点" in l][:2]}'),
            # 消费者读新写出的 state.json 不能出问题（累积语义引入的 carried_over 值）
            ('报告能容忍 carried_over（不报 state 相关发现）',
             bool(resume_report) and not [f for f in resume_report.get('findings') or []
                                          if 'state' in str(f.get('code'))],
             f"findings={resume_report.get('findings')}"),
        ]
        for name, ok, detail in resume_checks:
            print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
            if not ok:
                failures.append(f'{name}: {detail}')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（只读探针跑通、判据来自宿主、capture 在 dry-run 记 skipped 而非失败）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
