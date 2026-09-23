# -*- coding: utf-8 -*-
"""真机冒烟：把"等设备在线才能做"的几件事收成一个命令。

为什么需要它：设备不在线时这些项只能挂着，而挂着的清单最容易烂在文档里。
把它们做成一条可执行的流程，**模拟器一起来就能一次性收口**，不用重新勘察要测什么。

当前收口的三件事：

1. `account_state` 的 `capture=true` **当场抓帧**路径（离线只用存盘帧验过）；
2. `IN_MAP` 判据的**现场取值** —— 存盘帧上真机地图帧出现过 3.33 / 10.06 / 10.33，
   跨过上游阈值 10（见 `docs/tasks.md` 第五节）。这里记录现场值，**不自动改阈值**：
   现场数据到了再决定要不要做兼容垫片。
3. 一次**有界**的战役冒烟（默认 dry-run；要真跑必须显式 `--allow-actions`），
   产出完整工件链供 `alashub report` 复核。

没有设备时：打印明确的跳过原因并返回 0（这样它可以常驻在 `verify_all` 里，
   而不是每轮都红）。

用法：
    python tools/diagnostics/device_smoke.py                 # 只读 + dry-run
    python tools/diagnostics/device_smoke.py --allow-actions # 追加真机抓帧与一次真跑
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path
import contextlib
from datetime import datetime
from tempfile import TemporaryDirectory

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

#: 设备在线后要收口的**全部**未完成项。前 3 项由本脚本执行，其余是既有脚本
#: （放在这里是为了让"还欠什么"一眼可见，而不是散在几份文档里）。
CHECKLIST = [
    ('本脚本：当场抓帧 + IN_MAP 现场取值 + 一次有界战役冒烟',
     r'python tools\diagnostics\device_smoke.py --allow-actions',
     '真跑会消耗石油；冒烟上限 max_rounds=2 / max_seconds=600'),
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
     '**控件规则：跑之前要知道它会走到哪一层**。`verify_controls.py` 经产品路径 `alashub goto page_campaign`'
     '再点 `handler/STRATEGY_OPEN` —— 进入战役图的阵型/潜艇面板（**离「出击」只差一步，但它不会开战**：脚本第 31 行起有红线，命中即拒绝点击，注释写明「开战会消耗石油、确认不可逆」）。所以它既不是"无人值守随便跑"，也不像我先前写的"会进入出击流程"那样重 —— 以此条为准。'
     '`verify_text_input.py` 会往装备码输入框打字（已跑通 3/3）；原语（返回键/长按/滑动）已跑通 4/4，安全。**三项都已在窗口内跑过**：原语 4/4、文本输入 3/3、控件规则 15 命中/13 未命中/5 被拦（33 项）。'),
    ('用新运行时跑一次**真机通关**（R2 的真实路径证据）',
     r'alashub campaign campaign.campaign_main.campaign_1_1 --run --allow-actions --artifacts runs\  '
     r'（或 device_smoke 的第 3 项）',
     '现有 4 条通关证据出自运行时之前的 CLI 路径，需要一条走新运行时的'),
    ('周期任务执行环的第一次真跑',
     r'periodic_preflight（勘察 + 两道闸）已就绪；执行 op 故意未实现',
     '【需本人授权】第一次真跑要有人看着 —— 静态读代码证明不了「点错地方」（意料之外的弹窗可能让按坐标的点击落到购买上）'),
    ('【需本人授权】本局撤退判 withdrawn 的真机记录',
     '进入一张图后主动撤退（消耗石油、改变账号状态）',
     'R0 遗留缺口：现有 2 起撤退都是上一局清理/导航期，不是本局结论。'
     '这条**不自动执行**，等明确授权'),
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
    if not EXE.is_file():
        print(f'**失败**：未找到 {EXE.relative_to(ROOT)}（先运行 dotnet build）')
        return 1

    state, detail = device_state()
    if state is None:
        print(f'[跳过] 设备 {SERIAL} 不在线（adb get-state: {detail}）。')
        print()
        print('模拟器起来后，**未完成的真机项**按这个顺序收口（本脚本跑前三项，其余是既有脚本）：')
        print()
        for index, (name, command, note) in enumerate(CHECKLIST, 1):
            print(f'  {index}. {name}')
            print(f'     {command}')
            if note:
                print(f'     ↳ {note}')
        print()
        print('  * 一揽子跑：python tools\\diagnostics\\verify_all.py --device-only'
              '（依次跑上面的设备类步骤 1~4；第 5 项要真跑、第 6 项要授权，都不在里面）')
        return 0

    print(f'=== 真机冒烟：{SERIAL} ===')
    results = {'serial': SERIAL, 'allow_actions': allow_actions, 'items': {}}
    failures: list[str] = []
    # 持久目录（真机证据不能随进程退出消失）；用 nullcontext 保持原有缩进结构。
    with contextlib.nullcontext(artifacts_root()) as tmp:
        tmpdir = Path(tmp)
        tasks = [
            # 只读，但要**当场抓帧**：这是离线验收覆盖不到的那条路径。
            # 没加 --allow-actions 时它会被前置条件拦下（dry-run 不碰设备）→ 记 skipped，
            # 这正是"默认不动设备"该有的样子。
            {'id': 'live-state', 'kind': 'account_state', 'input': {'capture': True}},
        ]
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
                print('           默认不碰设备；要抓现场帧请加 --allow-actions。')
            else:
                print(f"[现场状态] 帧可用={frame.get('available')} pages={evidence.get('pages')} "
                      f"in_map={evidence.get('in_map')} 相似度={tolerance} "
                      f"outcome={live_doc.get('outcome')}")
            if allow_actions and live_doc.get('outcome') != 'succeeded':
                failures.append(f"现场抓帧未成功: {live_doc.get('outcome')} {live_doc.get('error')}")
            elif allow_actions and frame.get('available') and tolerance is not None:
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
