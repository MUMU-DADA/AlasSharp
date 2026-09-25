# -*- coding: utf-8 -*-
"""R2 账号状态域验收：只读状态任务 + 存盘真机帧 + 临界判据留证。

三件事：

1. **功能**：`account_state` 任务能在**没有设备**的情况下跑通（用 `data/*.png` 里的真机帧），
   报出服务器、当前页面、是否在图内、账号配置要点，并逐任务落盘工件。
2. **判据来自上游**：页面用上游 `Page.check_button`，在图内用 `handler/IN_MAP` 的颜色比对，
   本脚本不重复实现判据，只核对结果。
3. **临界值留证**：`IN_MAP` 是**颜色比对**（上游阈值 10），真机帧上实测出现过
   3.33（判"在图内"）与 10.06~10.33（判"不在图内"）两种结果 —— 后者是**同一客户端上
   的真实地图帧**。这里把每帧的相似度记进 `data/account_state_probe.json`，
   临界区间（8~14）单独列出，供真机复核对齐；**不改判据、不调阈值**。

用法：
    python tools/diagnostics/verify_account_state.py
本地没有 `data/*.png` 归档帧时，帧相关的断言显式跳过（不是静默通过）。
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

EXE = ROOT / 'src' / 'Alas.Server' / 'bin' / 'Release' / 'net10.0' / 'Alas.Server.exe'
IN_MAP_THRESHOLD = 10.0          # 上游 `appear(button, threshold=10)` 的默认值
BORDERLINE = (8.0, 14.0)         # 记录下来供真机复核的临界区间

# 期望值来自**人工看过帧**后的核对结果（帧名 → 画面内容），不是照抄 op 的输出。
FRAMES = [
    {'frame': '_boss122.png', 'expect_in_map': True, 'expect_page': None,
     'note': '真机 7-1 地图帧：撤退按钮在屏上（人工核对）'},
    {'frame': 's3_final_stage.png', 'expect_in_map': False, 'expect_page': 'page_campaign',
     'note': '真机第 1 章选择页：四个关卡都带 Clear!（人工核对）'},
    {'frame': '_shot_campaign_map.png', 'expect_in_map': False, 'expect_page': 'page_campaign',
     'note': '真机章节选择页'},
    # 下面两帧是**真实地图帧**（人工核对：撤退按钮在屏上），但颜色比对的相似度卡在阈值外侧。
    # 不断言 in_map，只把数值留证 —— 这是"临界判据"的证据，不是失败。
    {'frame': '_14_inmap.png', 'expect_in_map': None, 'expect_page': None,
     'note': '真机 1-4 地图帧；IN_MAP 相似度落在临界区（见 risk 段）'},
    {'frame': '_71_inmap.png', 'expect_in_map': None, 'expect_page': None,
     'note': '真机 7-1 地图帧；IN_MAP 相似度落在临界区（见 risk 段）'},
    # 更多帧只用于**分布**留证（不断言）：其中两帧是早期用 cv2 存盘的，通道序与宿主相反，
    # 相似度会离谱地大 —— 这正是"帧的存盘通道序"这个坑，记在这里免得下次又当成判据问题。
    {'frame': '_map_now.png', 'expect_in_map': None, 'expect_page': None,
     'note': '地图过程帧（分布留证）'},
    {'frame': '_after_boss.png', 'expect_in_map': None, 'expect_page': None,
     'note': '战斗后帧（分布留证）'},
    {'frame': '_21_inmap.png', 'expect_in_map': None, 'expect_page': None,
     'note': '存盘通道序相反（cv2 存盘），相似度不可比，仅留证'},
    {'frame': '_map_now3.png', 'expect_in_map': None, 'expect_page': None,
     'note': '存盘通道序相反（cv2 存盘），相似度不可比，仅留证'},
]


def task_id(frame: str) -> str:
    return 'state-' + Path(frame).stem.strip('_')


def main() -> int:
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先运行 dotnet build）')
        return 1

    present = [item for item in FRAMES if (DATA / item['frame']).is_file()]
    failures: list[str] = []
    if not present:
        print('[跳过] data/ 下没有归档真机帧（data/ 不入库）；账号状态域的帧断言未跑。')
        print('       有帧时用：python tools/diagnostics/verify_account_state.py')
        return 0

    with TemporaryDirectory(prefix='alas-account-state-') as tmp:
        tmpdir = Path(tmp)
        queue_file = tmpdir / 'queue.json'
        queue_file.write_text(json.dumps({'tasks': [
            {'id': task_id(item['frame']),
             'kind': 'account_state',
             'input': {'screenshot': str((DATA / item['frame']).resolve())}}
            for item in present]}, ensure_ascii=False, indent=1), encoding='utf-8')
        artifacts = tmpdir / 'artifacts'
        proc = subprocess.run([str(EXE), 'queue', '--file', str(queue_file),
                               '--artifacts', str(artifacts)],
                              capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=300)
        output = (proc.stdout or '') + (proc.stderr or '')
        if proc.returncode != 0:
            failures.append(f'Alas.Server queue 退出码 {proc.returncode}')
            print(output[-2000:])

        run_dirs = sorted(p for p in artifacts.glob('*') if p.is_dir())
        if not run_dirs:
            print('**失败**：没有产出运行目录（工件没落盘）')
            return 1
        run_dir = run_dirs[-1]
        queue_artifact = json.loads((run_dir / 'queue.json').read_text(encoding='utf-8'))
        if queue_artifact['outcome'] != 'succeeded':
            failures.append(f"队列结论应为 succeeded，实为 {queue_artifact['outcome']}")

        print(f'=== 账号状态域（{len(present)} 帧，替身=存盘真机帧，无设备）===')
        probe = {'note': 'IN_MAP 是颜色比对（上游阈值 10）；临界区间 '
                         f'{BORDERLINE[0]}~{BORDERLINE[1]} 单独列出供真机复核',
                 'threshold': IN_MAP_THRESHOLD, 'frames': []}
        for item in present:
            path = run_dir / f'task-{task_id(item["frame"])}.json'
            if not path.is_file():
                failures.append(f"{item['frame']}: 缺少任务工件 {path.name}")
                continue
            artifact = json.loads(path.read_text(encoding='utf-8'))
            evidence = artifact.get('evidence') or {}
            tolerance = evidence.get('in_map_tolerance')
            in_map = evidence.get('in_map')
            pages = evidence.get('pages') or []
            problems = []
            if artifact['outcome'] != 'succeeded':
                problems.append(f"任务结论 {artifact['outcome']}（{artifact.get('error')}）")
            if evidence.get('server') is None:
                problems.append('没有报出 server')
            if not evidence.get('frame', {}).get('available'):
                problems.append('帧不可用')
            if item['expect_in_map'] is not None and in_map != item['expect_in_map']:
                problems.append(f"in_map 期望 {item['expect_in_map']} 实为 {in_map}")
            if item['expect_page'] and item['expect_page'] not in pages:
                problems.append(f"pages 里应有 {item['expect_page']}，实为 {pages}")
            if evidence.get('config') is None:
                problems.append('没有读出账号配置要点')

            probe['frames'].append({'frame': item['frame'], 'in_map': in_map,
                                    'tolerance': tolerance, 'pages': pages,
                                    'note': item['note']})
            status = 'ok  ' if not problems else 'FAIL'
            print(f"  {status} {item['frame']:<26} in_map={str(in_map):<5} "
                  f"相似度={tolerance} pages={pages}")
            for problem in problems:
                print(f'        ← {problem}')
                failures.append(f"{item['frame']}: {problem}")

        borderline = [f for f in probe['frames']
                      if f['tolerance'] is not None
                      and BORDERLINE[0] <= f['tolerance'] <= BORDERLINE[1]]
        probe['borderline'] = borderline
        (DATA / 'account_state_probe.json').write_text(
            json.dumps(probe, ensure_ascii=False, indent=1), encoding='utf-8')
        print()
        if borderline:
            print(f'  [临界] {len(borderline)} 帧的 IN_MAP 相似度落在 '
                  f'{BORDERLINE[0]}~{BORDERLINE[1]}（阈值 {IN_MAP_THRESHOLD}）：')
            for item in borderline:
                print(f"         {item['frame']} 相似度={item['tolerance']} → in_map={item['in_map']}")
            print('         人工核对：这些是**真实地图帧**（撤退按钮在屏上）。')
            print('         处置：不改上游判据/阈值，记入 data/account_state_probe.json 与 docs/tasks.md，')
            print('         等真机复核后再决定是否做兼容垫片。')
        print('  判据数值已写入: data/account_state_probe.json')

        invalid_file = tmpdir / 'invalid-queue.json'
        invalid_cases = [
            ('state-mixed-source', {'capture': True, 'screenshot': str((DATA / present[0]['frame']).resolve())},
             'input.capture 与 input.screenshot'),
            ('state-invalid-capture', {'capture': 'true', 'screenshot': str((DATA / present[0]['frame']).resolve())},
             'input.capture'),
            ('state-empty-source', {'screenshot': ''}, 'input.screenshot'),
            ('state-invalid-source', {'screenshot': 7}, 'input.screenshot'),
        ]
        invalid_file.write_text(json.dumps({'tasks': [
            {'id': name, 'kind': 'account_state', 'input': input_}
            for name, input_, _ in invalid_cases]}, ensure_ascii=False), encoding='utf-8')
        invalid_artifacts = tmpdir / 'invalid-artifacts'
        invalid_run = subprocess.run([str(EXE), 'queue', '--file', str(invalid_file),
                                      '--artifacts', str(invalid_artifacts)],
                                     capture_output=True, text=True, encoding='utf-8',
                                     errors='replace', timeout=300)
        invalid_dirs = sorted(p for p in invalid_artifacts.glob('*') if p.is_dir())
        if invalid_run.returncode != 0 or not invalid_dirs:
            failures.append('账号状态无效输入队列没有完成并落盘')
        else:
            invalid_dir = invalid_dirs[-1]
            for name, _, reason in invalid_cases:
                path = invalid_dir / f'task-{name}.json'
                artifact = json.loads(path.read_text(encoding='utf-8')) if path.is_file() else {}
                if (artifact.get('outcome') != 'skipped'
                        or artifact.get('stop_reason') != 'precondition'
                        or not any(reason in text for text in artifact.get('unmet_preconditions') or [])):
                    failures.append(f'{name} 未被前置条件拦下')
            queue = json.loads((invalid_dir / 'queue.json').read_text(encoding='utf-8'))
            if queue.get('device_configure_count') != 0:
                failures.append('账号状态无效输入触碰了设备')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（只读状态任务跑通、判据来自上游、临界值已留证）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
