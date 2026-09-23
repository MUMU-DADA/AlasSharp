# -*- coding: utf-8 -*-
"""产品路径检查：`alashub map` 与同帧上游地图视图对照。

与 `verify_map_detection.py` 的分工：那个走**识图协议**（Python 侧），
这个走**产品路径**（C# → 进程内 CPython → 上游），确认"适配好的 S2 能被 C# 直接调用"。

必需 fixture 覆盖以下通路：
  map_settled.png    战役 6x4（2-1），带关卡 IR 交叉校验
  map_shape_9x6.png  战役 9x6（10-4），带关卡 IR 交叉校验（含缺格记录）
  map_hard_1_4.png   困难图 7x3（1-4），带 IR 交叉校验（困难图复用同章节地图数据）
  os_live_2.png      海域（OS 模式），与上游 OSConfig + View 对拍

判定：退出码为 0、检出地图，且产品结果符合 IR 或同帧上游视图。
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
ALASHUB = os.path.join(ROOT, 'src', 'Alas.DataTool', 'bin', 'Release', 'net10.0', 'alashub.exe')
FIXTURES = os.path.join(ROOT, 'data', 'fixtures')

CASES = [
    ('map_settled.png', 'main', 'campaign_main/campaign_2_1.json', 24),
    ('map_shape_9x6.png', 'main', 'campaign_main/campaign_10_4.json', 48),
    ('map_hard_1_4.png', 'main', 'campaign_main/campaign_1_4.json', 21),
    # 活动图（幽影迷城 A1，IR 是 9x8）的可见窗口是 8x4；窗口情形不做 IR 交叉校验，只验检出
    ('map_event.png', 'main', None, 30),
    ('os_live_2.png', 'os', None, None),
]

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def _native_os_reference(path):
    """直接驱动上游 OSConfig + View；只共用必需的通用兼容垫片。"""
    sys.path.insert(0, os.path.join(ROOT, 'tools'))
    import alas_vision as vision
    from module.base.utils import load_image
    from module.config.config import AzurLaneConfig
    from module.map_detection.os_grid import OSGrid
    from module.map_detection.view import View
    from module.os.config import OSConfig
    from module.os_handler.enemy_searching import EnemySearchingHandler

    config = AzurLaneConfig('alas').merge(OSConfig())
    vision.apply_numpy2_compat()
    vision.apply_points_empty_compat()
    vision.apply_os_mask_compat()
    image = load_image(path)
    in_map = bool(EnemySearchingHandler.is_in_map(vision._make_main_shim(image)))
    view = View(config, mode='os', grid_class=OSGrid)
    vision.set_os_mask_mode(True)
    try:
        view.load(image)
    finally:
        vision.set_os_mask_mode(False)
    view.predict()
    keys = sorted(tuple(int(n) for n in key) for key in view.grids)
    shape = tuple(int(n) for n in view.shape)
    backend = type(view.backend).__name__.lower()
    if not in_map or backend != config.DETECTION_BACKEND or view.grid_class is not OSGrid \
            or not keys or any(
            x < 0 or y < 0 or x > shape[0] or y > shape[1] for x, y in keys):
        raise AssertionError('上游 OS 在图、后端或网格结果不一致')
    return {'backend': backend, 'grids': len(keys),
            'shape': shape, 'grid_class': view.grid_class.__name__, 'in_map': in_map}


def native_os_reference(path):
    previous_dir = os.getcwd()
    try:
        return _native_os_reference(path)
    finally:
        os.chdir(previous_dir)


def main():
    if not os.path.exists(ALASHUB):
        print('缺 alashub：先 dotnet build src/Alas.DataTool -c Release')
        return 2
    failed = 0
    for fixture, mode, chapter, expect in CASES:
        path = os.path.join(FIXTURES, fixture)
        if not os.path.isfile(path):
            print('%-18s FAIL（缺必需 fixture）' % fixture)
            failed += 1
            continue
        reference = None
        if mode == 'os':
            try:
                reference = native_os_reference(path)
            except Exception as error:
                print('%-18s FAIL（上游 OS 直接对照失败: %s: %s）'
                      % (fixture, type(error).__name__, error))
                failed += 1
                continue
            expect = reference['grids']
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
        m = re.search(r'\[map\s*\]\s+backend=(\w+)\s+detected=(\w+)\s+grids=(\d+)', out)
        backend = m.group(1) if m else None
        detected = m and m.group(2) == 'True'
        grids = int(m.group(3)) if m else None
        ir_line = next((l for l in out.splitlines() if 'shape 一致' in l), '')
        ok = (r.returncode == 0) and detected and grids == expect
        if reference is not None:
            ok = ok and backend == reference['backend']
        detail = (('上游 %s in_map=%s shape=%s class=%s' %
                   (reference['backend'], reference['in_map'],
                    reference['shape'], reference['grid_class']))
                  if reference is not None else ir_line.strip()[:40])
        print('%-18s mode=%-4s backend=%-11s detected=%-6s grids=%-4s 期望=%-4d %s %s'
              % (fixture, mode, backend, detected, grids, expect,
                 'OK' if ok else 'FAIL', detail))
        if not ok:
            failed += 1
            tail = '\n'.join(out.strip().splitlines()[-4:])
            print('   ' + tail.replace('\n', '\n   '))
    print('产品路径：%d/%d 通过' % (len(CASES) - failed, len(CASES)))
    return 0 if failed == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
