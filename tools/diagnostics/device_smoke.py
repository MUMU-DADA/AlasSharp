# -*- coding: utf-8 -*-
"""真机冒烟：把"等设备在线才能做"的几件事收成一个命令。

为什么需要它：设备不在线时这些项只能挂着，而挂着的清单最容易烂在文档里。
把它们做成一条可执行的流程，**模拟器一起来就能一次性收口**，不用重新勘察要测什么。

当前收口的三件事：

1. `account_state` 的 `capture=true` **当场抓帧**路径（`--read-only-device` 不授予游戏动作）；
2. `IN_MAP` 判据的**现场取值** —— 存盘帧上真机地图帧出现过 3.33 / 10.06 / 10.33，
   跨过上游阈值 10（见 `docs/tasks.md` 第五节）。这里记录现场值，**不自动改阈值**：
   现场数据到了再决定要不要做兼容垫片。
3. 一次**有界**的战役冒烟（默认 dry-run；要真跑必须显式 `--allow-actions`），
   产出完整工件链供 `alashub report` 复核。

没有设备时：打印明确的跳过原因并返回 0（这样它可以常驻在 `verify_all` 里，
   而不是每轮都红）。

用法：
    python tools/diagnostics/device_smoke.py                    # dry-run，不抓现场帧
    python tools/diagnostics/device_smoke.py --read-only-device # 只读现场抓帧
    python tools/diagnostics/device_smoke.py --allow-actions    # 抓帧并追加一次真跑
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path
import contextlib
from datetime import datetime

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DATA = ROOT / 'data'
sys.path.insert(0, str(ROOT / 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

EXE = ROOT / 'src' / 'Alas.DataTool' / 'bin' / 'Release' / 'net8.0' / 'alashub.exe'
ADB = ROOT / '.runtime' / 'venv314' / 'Lib' / 'site-packages' / 'adbutils' / 'binaries' / 'adb.exe'
SERIAL = os.environ.get('ALAS_SERIAL', '127.0.0.1:16384')
CHAPTER = 'campaign.campaign_main.campaign_1_1'
IN_MAP_THRESHOLD = 10.0
BORDERLINE = (8.0, 14.0)

#: 设备在线后要收口的未完成项。已归档的本局撤退不再列为欠账。
#: （放在这里是为了让"还欠什么"一眼可见，而不是散在几份文档里）。
CHECKLIST = [
    ('当前队列入口：只读抓帧 + IN_MAP 现场取值 + observe 观测',
     r'python tools\diagnostics\device_smoke.py --read-only-device',
     '只用 --run --read-only-device，不授予点击或出击权限'),
    ('用新运行时跑一次真机通关',
     r'python tools\diagnostics\device_smoke.py --allow-actions',
     '会消耗石油；上限 max_rounds=2 / max_seconds=600；须按成功结算核对'),
    ('当前队列入口的导航真机回归',
     r'alashub queue --file <navigate 队列.json> --run --allow-actions --serial <设备>',
     '导航会点击游戏页面；仅离线替身和旧入口现场记录不足以证明新入口'),
    ('设备引擎回归（后端可切换 / 抓图 / 点击）',
     r'python tools\diagnostics\verify_device_engine.py',
     'R1 起设备 I/O 走宿主，后端换了要重跑'),
    ('页面识别全量回归（产品导航器）',
     r'python tools\diagnostics\regress_pages.py',
     '约 5 分钟；**最近一次 29/34**（2026-09-23，仅解锁第 1 章的账号）；数字必须连着账号前提看（生成文档头部会自动写本次解锁进度）'),
    ('页面入口分诊（哪个入口不存在 / 哪个判据认不出）',
     r'python tools\diagnostics\page_entry_probe.py',
     '**先回到主界面**（不在 page_main 它会明确跳过、不猜）；产出每个目标页第一跳的'
     '最佳变体、是否在屏、相似度 —— 真机踩过：同一份失败列表里混着"账号门禁"与"判据认不出"两类根因'),
    ('控件规则 + 控制原语 + 文本输入',
     r'python tools\diagnostics\verify_primitives.py（原语，安全）',
     '**控件规则：跑之前要知道它会走到哪一层**。`verify_controls.py` 经产品队列的 `navigate` 任务到 page_campaign'
     '再点 `handler/STRATEGY_OPEN` —— 进入战役图的阵型/潜艇面板（**离「出击」只差一步，但它不会开战**：脚本第 31 行起有红线，命中即拒绝点击，注释写明「开战会消耗石油、确认不可逆」）。所以它既不是"无人值守随便跑"，也不像我先前写的"会进入出击流程"那样重 —— 以此条为准。'
     '`verify_text_input.py` 会往装备码输入框打字（已跑通 3/3）；原语（返回键/长按/滑动）已跑通 4/4，安全。**三项都已在窗口内跑过**：原语 4/4、文本输入 3/3、控件规则 15 命中/13 未命中/5 被拦（33 项）—— **被拦的 5 项读法（已更正一次，以此为准）**：闸门是"模板分 ≥0.85 才点"，被拦说明**那一刻目标控件没有被确信看见**，是**画面/时机**问题，不是素材问题。我先前写过"本客户端模板与上游存图不一致"，**那是错的**：拿同一台设备的真机地图帧复测，`handler/STRATEGY_OPEN` 模板分 **1.000**（颜色 5.80）、`handler/IN_MAP` 1.000、`ui/BACK_ARROW` 1.000、`map/FLEET_NUM_1` 1.000 —— 模板是匹配的。所以这 5 项的正确状态是**"还没验到"**（要先把画面走到它能被确信看见的状态再跑），**不要去改上游素材、也不要放宽闸门**。要判断某个素材到底匹不匹配，用 `python tools\\diagnostics\\probe_asset_match.py <帧.png> <素材id>`（模板分 + 颜色判据一次给全，别再写临时代码）。'),
    ('周期任务新原生调度入口的真机回归',
     r'先核对 periodic_plan，再通过 queue --file 提交单个 periodic_run 任务',
     '【需本人授权】执行环已实现，但新适配器未做真机回归；本脚本不自动执行周期任务'),
]


def artifacts_root() -> Path:
    """真机运行的工件目录 —— **必须持久**。

    本轮实测踩到的坑：这里原本用 `TemporaryDirectory`，脚本退出后整个工件目录被删掉，
    而真机证据恰恰是最稀缺的（离线可以随时重跑，真机要等设备在线）。于是"跑了真机、
    事后再想看那一帧/那一步的 traceback"变成不可能。改为落在 `data/device_runs/<时间戳>/`
    （可用 `--artifacts <目录>` 覆盖）。
    """
    if '--artifacts' in sys.argv:
        path = Path(sys.argv[sys.argv.index('--artifacts') + 1])
    else:
        path = DATA / 'device_runs' / datetime.now().strftime('%Y%m%dT%H%M%S')
    path.mkdir(parents=True, exist_ok=True)
    return path


def adb(*args, timeout=20):
    binary = str(ADB) if ADB.is_file() else 'adb'
    try:
        proc = subprocess.run([binary, *args], capture_output=True, text=True,
                              encoding='utf-8', errors='replace', timeout=timeout)
        return proc.returncode, (proc.stdout or '') + (proc.stderr or '')
    except Exception as error:                      # adb 不存在/超时都算"没设备"
        return 1, f'{type(error).__name__}: {error}'


def device_state():
    code, out = adb('-s', SERIAL, 'get-state')
    return ('device' if code == 0 and 'device' in out else None), out.strip()[:120]


def main() -> int:
    allow_actions = '--allow-actions' in sys.argv
    read_only_device = '--read-only-device' in sys.argv
    if allow_actions and read_only_device:
        print('**失败**：--allow-actions 与 --read-only-device 只能选一个')
        return 2
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先运行 dotnet build）')
        return 1

    state, detail = device_state()
    if state is None:
        print(f'[跳过] 设备 {SERIAL} 不在线（adb get-state: {detail}）。')
        print()
        print('模拟器起来后，未完成的真机项按这个顺序收口：')
        print()
        for index, (name, command, note) in enumerate(CHECKLIST, 1):
            print(f'  {index}. {name}')
            print(f'     {command}')
            if note:
                print(f'     ↳ {note}')
        print()
        print('  * --device-only 仅运行登记的设备检查；真跑与周期任务仍需分别授权。')
        return 0

    print(f'=== 真机冒烟：{SERIAL} ===')
    results = {'serial': SERIAL, 'allow_actions': allow_actions,
               'read_only_device': read_only_device, 'items': {}}
    failures: list[str] = []
    # 持久目录（真机证据不能随进程退出消失）；用 nullcontext 保持原有缩进结构。
    with contextlib.nullcontext(artifacts_root()) as tmp:
        tmpdir = Path(tmp)
        tasks = [
            # 只读，但要**当场抓帧**：这是离线验收覆盖不到的那条路径。
            # 没加任何授权时它会被前置条件拦下（dry-run 不碰设备）→ 记 skipped，
            # 这正是"默认不动设备"该有的样子。
            {'id': 'live-state', 'kind': 'account_state', 'input': {'capture': True}},
        ]
        if read_only_device:
            tasks.append({'id': 'observe', 'kind': 'observe', 'required': True,
                          'input': {'seconds': 2, 'tick_seconds': 0.5}})
        if allow_actions:
            tasks.append({'id': 'campaign-smoke', 'kind': 'campaign_batch',
                          'input': {'chapters': [CHAPTER], 'max_rounds': 2, 'max_seconds': 600}})
        queue_file = tmpdir / 'queue.json'
        queue_file.write_text(json.dumps({'tasks': tasks}, ensure_ascii=False, indent=1),
                              encoding='utf-8')
        artifacts = tmpdir / 'artifacts'
        cmd = [str(EXE), 'queue', '--file', str(queue_file), '--artifacts', str(artifacts),
               '--serial', SERIAL]
        if allow_actions:
            cmd += ['--run', '--allow-actions']
        elif read_only_device:
            cmd += ['--run', '--read-only-device']
        proc = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8',
                              errors='replace', timeout=1800)
        print(proc.stdout[-3000:] if proc.stdout else '')
        if proc.stderr:
            print('stderr: ' + proc.stderr[-800:])
        run_dirs = sorted(p for p in artifacts.glob('*') if p.is_dir())
        if not run_dirs:
            print('**失败**：没有产出运行目录')
            return 1
        run_dir = run_dirs[-1]

        # ---- 1) 当场抓帧 + IN_MAP 现场取值
        live = run_dir / 'task-live-state.json'
        if live.is_file():
            live_doc = json.loads(live.read_text(encoding='utf-8'))
            evidence = live_doc.get('evidence') or {}
            tolerance = evidence.get('in_map_tolerance')
            frame = evidence.get('frame') or {}
            results['items']['live_state'] = {
                'outcome': live_doc.get('outcome'),
                'frame_available': frame.get('available'),
                'pages': evidence.get('pages'),
                'in_map': evidence.get('in_map'),
                'in_map_tolerance': tolerance,
                'error': live_doc.get('error'),
            }
            if live_doc.get('outcome') == 'skipped':
                print(f"[现场状态] 未跑（{live_doc.get('error')}）")
                print('           默认不碰设备；只读抓帧请加 --read-only-device。')
            else:
                print(f"[现场状态] 帧可用={frame.get('available')} pages={evidence.get('pages')} "
                      f"in_map={evidence.get('in_map')} 相似度={tolerance} "
                      f"outcome={live_doc.get('outcome')}")
            if (allow_actions or read_only_device) and live_doc.get('outcome') != 'succeeded':
                failures.append(f"现场抓帧未成功: {live_doc.get('outcome')} {live_doc.get('error')}")
            elif (allow_actions or read_only_device) and frame.get('available') and tolerance is not None:
                lo, hi = BORDERLINE
                verdict = ('临界' if lo <= tolerance <= hi else
                           '偏内' if tolerance < lo else '偏外')
                print(f'[IN_MAP  ] 现场相似度={tolerance} 阈值={IN_MAP_THRESHOLD} → {verdict}')
                if verdict == '临界':
                    print('           现场也是临界值：这正是需要决定"是否做兼容垫片"的数据，'
                          '不要直接调阈值 —— 先看同一会话里 in_map 的判定是否与画面一致。')
                results['items']['in_map_threshold_check'] = {
                    'tolerance': tolerance, 'threshold': IN_MAP_THRESHOLD, 'verdict': verdict}
        else:
            failures.append('没有产出 live-state 工件')

        observation = run_dir / 'task-observe.json'
        if read_only_device:
            if not observation.is_file():
                failures.append('只读队列没有产出 observe 工件')
            else:
                observed = json.loads(observation.read_text(encoding='utf-8'))
                observed_evidence = observed.get('evidence') or {}
                results['items']['observe'] = {
                    'outcome': observed.get('outcome'),
                    'ticks': observed_evidence.get('ticks'),
                    'errors': observed_evidence.get('errors'),
                    'read_only': observed_evidence.get('read_only'),
                    'error': observed.get('error'),
                }
                print(f"[队列观测] outcome={observed.get('outcome')} "
                      f"ticks={observed_evidence.get('ticks')} "
                      f"errors={observed_evidence.get('errors')}")
                if (observed.get('outcome') != 'succeeded'
                        or (observed_evidence.get('ticks') or 0) < 1
                        or observed_evidence.get('errors') != 0
                        or observed_evidence.get('read_only') is not True):
                    failures.append('新队列入口的只读观测未完成或证据不完整')

        # ---- 2) 战役冒烟
        smoke = run_dir / 'task-campaign-smoke.json'
        if smoke.is_file():
            doc = json.loads(smoke.read_text(encoding='utf-8'))
            results['items']['campaign_smoke'] = {'outcome': doc.get('outcome'),
                                                  'error': doc.get('error'),
                                                  'evidence': doc.get('evidence')}
            print(f"[战役冒烟] outcome={doc.get('outcome')} error={doc.get('error')}")
            if allow_actions and doc.get('outcome') != 'succeeded':
                failures.append(f"真跑冒烟未成功: {doc.get('outcome')} {doc.get('error')}")

        results['run_directory'] = str(run_dir)
        report = subprocess.run([str(EXE), 'report', '--run', str(run_dir)],
                                capture_output=True, text=True, encoding='utf-8',
                                errors='replace', timeout=120)
        print('--- 运行报告（证据完整性）---')
        print(report.stdout[-1500:] if report.stdout else '')

    results['failures'] = failures
    (DATA / 'device_smoke.json').write_text(json.dumps(results, ensure_ascii=False, indent=1),
                                            encoding='utf-8')
    print('证据已写入: data/device_smoke.json')
    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        return 1
    print('结果: OK（真机冒烟项已跑；IN_MAP 现场取值已记录）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
