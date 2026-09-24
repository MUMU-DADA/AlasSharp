# -*- coding: utf-8 -*-
"""`IN_MAP` 阈值垫片的断言：它一旦被摘掉，这条要变红。

背景（2026-09-23 真机根因）：本客户端「撤退」按钮颜色偏一点，`IN_MAP` 相似度落在 10.0~10.4，
而上游阈值是 10 → "已进图"被判成"不在图内"，`enter_map` 白等 62s 后 `GameStuckError`。
修法是 `apply_in_map_threshold_compat()` 只放宽**这一个素材**的阈值（见 `docs/archive/history/device-stall-in-map.md`）。

**为什么需要这条**：修完之后一直只有手工验证。垫片是"少一行调用就静默失效"的东西 ——
没有断言，将来任何人重构 `op_s3_campaign_init` 都可能把它丢掉，而症状（真机卡死 62 秒）
要到下一次真机运行才暴露。这里用**归档的真机帧**把它钉住，不需要设备。

用法：
    python tools/diagnostics/verify_in_map_shim.py
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
DATA = ROOT / 'data'
sys.path.insert(0, str(ROOT / 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

os.chdir(ENGINE)
import alas_vision as av                                        # noqa: E402

#: 真机卡死时刻的现场帧：游戏**已经在地图内**（舰队就位、右下角「撤退」按钮在屏）
FRAME = DATA / '_live_enter_map_stall.png'


def measure() -> tuple[bool, float]:
    import json
    result = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': 'appear_on', 'args': {'asset': 'handler/IN_MAP'}})))['result']
    return bool(result['appear']), float(result['tolerance'])


def main() -> int:
    if not FRAME.is_file():
        print(f'[跳过] 没有归档现场帧 {FRAME.name}（data/ 不入库）—— 本断言未跑。')
        return 0
    av.op_screenshot_load({'path': str(FRAME)})

    before_appear, before_tolerance = measure()
    av.apply_in_map_threshold_compat()
    after_appear, after_tolerance = measure()

    print('=== IN_MAP 阈值垫片 ===')
    print(f'  现场帧: {FRAME.name}（游戏在地图内，撤退按钮在屏）')
    print(f'  垫片前: appear={before_appear}  相似度={before_tolerance}')
    print(f'  垫片后: appear={after_appear}  相似度={after_tolerance}')

    failures = []
    checks = [
        # 这条钉住"上游默认阈值下确实判不出来"—— 也就钉住了这个问题的存在
        ('上游阈值下判不出来（相似度落在 10 外侧）',
         before_appear is False and before_tolerance >= 10.0,
         f'appear={before_appear} tolerance={before_tolerance}'),
        # 这条钉住"垫片确实起作用"
        ('垫片后判得出来（就是那张地图帧）',
         after_appear is True, f'appear={after_appear} tolerance={after_tolerance}'),
        # 只放宽不收紧：阈值仍是 10 的调用方不该被改小
        ('阈值仍留在临界带附近，不是无脑放宽',
         after_tolerance <= 20.0, f'tolerance={after_tolerance}（阈值 20）'),
    ]
    for name, ok, detail in checks:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}" + ('' if ok else f'  ← {detail}'))
        if not ok:
            failures.append(f'{name}: {detail}')

    print()
    if failures:
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    print('结果: OK（上游阈值判不出、垫片后判得出；且没有无脑放宽）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
