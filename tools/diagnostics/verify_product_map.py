# -*- coding: utf-8 -*-
"""产品路径检查：`alashub map` 在三张真机 fixture 上的检出与交叉校验。

与 `verify_map_detection.py` 的分工：那个走**识图协议**（Python 侧），
这个走**产品路径**（C# → 进程内 CPython → 上游），确认"适配好的 S2 能被 C# 直接调用"。

三张 fixture 覆盖三种通路：
  map_settled.png    战役 6x4（2-1），带关卡 IR 交叉校验
  map_shape_9x6.png  战役 9x6（10-4），带关卡 IR 交叉校验（含缺格记录）
  map_hard_1_4.png   困难图 7x3（1-4），带 IR 交叉校验（困难图复用同章节地图数据）
  os_live_2.png      海域 9x6（OS 模式），无 IR（OS 地图不走 campaign 的 IR）

判定：退出码为 0，且输出里出现 expected 的格数与 `detected=True`。
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
ALASHUB = os.path.join(ROOT, 'src', 'Alas.DataTool', 'bin', 'Release', 'net8.0', 'alashub.exe')
FIXTURES = os.path.join(ROOT, 'data', 'fixtures')

CASES = [
    ('map_settled.png', 'main', 'campaign_main/campaign_2_1.json', 24),
    ('map_shape_9x6.png', 'main', 'campaign_main/campaign_10_4.json', 48),
    ('map_hard_1_4.png', 'main', 'campaign_main/campaign_1_4.json', 21),
    # 活动图（幽影迷城 A1，IR 是 9x8）的可见窗口是 8x4；窗口情形不做 IR 交叉校验，只验检出
    ('map_event.png', 'main', None, 30),
    ('os_live_2.png', 'os', None, 49),
]

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def main():
    if not os.path.exists(ALASHUB):
        print('缺 alashub：先 dotnet build src/Alas.DataTool -c Release')
        return 2
    failed = 0
    for fixture, mode, chapter, expect in CASES:
        path = os.path.join(FIXTURES, fixture)
        if not os.path.exists(path):
            print('%-18s SKIP（无 fixture，可用 --capture 重抓）' % fixture)
            continue
        cmd = [ALASHUB, 'map', '--fixture', path, '--mode', mode]
        if chapter:
            cmd += ['--chapter', chapter]
        try:
            r = subprocess.run(cmd, capture_output=True, timeout=240)
        except subprocess.TimeoutExpired:
            print('%-18s TIMEOUT' % fixture)
            failed += 1
            continue
        out = (r.stdout or b'').decode('utf-8', 'replace')
        m = re.search(r'detected=(\w+)\s+grids=(\d+)', out)
        detected = m and m.group(1) == 'True'
        grids = int(m.group(2)) if m else None
        ir_line = next((l for l in out.splitlines() if 'shape 一致' in l), '')
        ok = (r.returncode == 0) and detected and grids == expect
        print('%-18s mode=%-4s detected=%-6s grids=%-4s 期望=%-4d %s %s'
              % (fixture, mode, detected, grids, expect,
                 'OK' if ok else 'FAIL', ir_line.strip()[:40]))
        if not ok:
            failed += 1
            tail = '\n'.join(out.strip().splitlines()[-4:])
            print('   ' + tail.replace('\n', '\n   '))
    print('产品路径：%d/%d 通过' % (len(CASES) - failed, len(CASES)))
    return 0 if failed == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
