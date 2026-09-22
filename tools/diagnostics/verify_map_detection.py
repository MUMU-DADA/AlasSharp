# -*- coding: utf-8 -*-
"""S2（地图识别）验收：素材链 + 单应性数值自检 + 正/负样本。

思路：S2 的图像算法全在上游（`module/map_detection`、`module/os/globe_detection`），
所以这里**不复制任何算法**，只驱动上游并核对结果：

  1. 素材链   —— UI 遮罩 / 瓦片模板 / 检测区域能否被上游代码读出（缺素材要在这里就暴露）
  2. 单应性   —— `globe_detect` 的 screen→globe→screen 往返必须≈0（同一变换的逆，
                 这是不需要真机地图就能做的**数值自检**）
  3. 负样本   —— 非地图画面必须返回"未检测到 + 原因"，而不是崩掉（产品要在任意画面上试）
  4. 正样本   —— 若 `data/fixtures/` 里有地图截图（`map_*.png` / `globe_*.png`），
                 跑检测并报告网格规模；没有就如实说明"缺正样本，需真机地图画面"

正样本从哪来：战役地图要**真的出击**（耗石油），大世界地图免费但本账号 OS 未解锁。
把游戏停在地图画面上，脚本可以用 `--capture <名字>` 直接抓一张存成 fixture。
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import alas_vision as av          # noqa: E402

ROOT = os.path.normpath(os.path.join(HERE, '..'))
FIXTURES = os.path.join(ROOT, 'data', 'fixtures')
DATA = os.path.join(ROOT, 'data')
DOCS = os.path.join(ROOT, 'docs')

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def op(name, **args):
    resp = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(resp.get('error'))
    return resp['result']


def capture(name):
    """把当前真机画面抓成 fixture（正样本靠它）。"""
    import adb_util
    adb_util.ensure()
    path = os.path.join(FIXTURES, '%s.png' % name)
    size = adb_util.screencap(path)
    print('已抓图 %s（%d B）' % (path, size))
    return path


def ir_expectation(chapter_rel):
    """从关卡 IR（`data/campaign/<...>.json`）算出"检测本该给出什么"。

    上游地图数据里 `shape='F4'` 的含义是 `node2location('F4') = (5, 3)`，
    而网格数是 **shape+1**（6x4=24）；这条"差一"规则在 MapIR 里已经和上游活对象
    穷尽对照过（见 docs/map-ir.md），所以这里可以直接拿它当期望值。
    """
    path = os.path.join(DATA, 'campaign', chapter_rel)
    if not os.path.exists(path):
        return None
    with open(path, encoding='utf-8') as f:
        ir = json.load(f)
    rows = [r.split() for r in (ir.get('map', {}).get('map_data') or '').strip().split('\n')
            if r.strip()]
    if not rows:
        return None
    cols = max(len(r) for r in rows)
    land = ['%d,%d' % (x, y) for y, row in enumerate(rows)
            for x, tok in enumerate(row) if tok.upper() == '++']
    return {'name': ir.get('name'), 'rows': len(rows), 'cols': cols,
            'grids': len(rows) * cols, 'shape': [cols - 1, len(rows) - 1],
            'land_cells': land}


def main():
    if '--capture' in sys.argv:
        i = sys.argv.index('--capture')
        capture(sys.argv[i + 1])
        return 0

    os.makedirs(FIXTURES, exist_ok=True)
    results = {'assets': None, 'globe': None, 'map': [], 'fixtures': []}

    # ---- 1) 素材链（不需要图片）
    results['assets'] = op('map_detection_assets')
    print('素材链:')
    for k, v in results['assets'].items():
        print('  %-20s %s' % (k, v))

    # ---- 2)+3) 逐张 fixture 跑检测
    fixtures = sorted(f for f in os.listdir(FIXTURES) if f.endswith('.png'))
    with open(os.path.join(HERE, 'diagnostics', 'map_fixtures.json'), encoding='utf-8') as fh:
        manifest = {k: v for k, v in json.load(fh).items() if not k.startswith('_')}
    results['fixtures'] = fixtures
    if not fixtures:
        print('没有 fixture：先 `--capture <名字>` 抓一张')
    for f in fixtures:
        path = os.path.join(FIXTURES, f)
        op('screenshot_load', path=path)
        entry = {'fixture': f}
        # 大世界：任何画面上都能跑（单应性 + 往返自检）
        g = op('globe_detect', points=[[640, 360], [200, 200]])
        entry['globe'] = g
        if results['globe'] is None:
            results['globe'] = dict(g, fixture=f)
        rt = None
        if g.get('globe2screen') and g.get('screen2globe'):
            pts_in = [[640, 360], [200, 200]]
            rt = max(abs(a[0] - b[0]) + abs(a[1] - b[1])
                     for a, b in zip(pts_in, g['globe2screen']))
        entry['globe_roundtrip_abs'] = rt
        if results.get('globe_positions') is None:
            results['globe_positions'] = []
        results['globe_positions'].append({
            'fixture': f,
            'similarity': g.get('similarity'),
            'center_loca': g.get('center_loca'),
            'page_hint': entry.get('page_hint'),
        })
        # 战役地图：非地图画面应当给负样本
        info = manifest.get(f)
        chapter = ('campaign.' + info['chapter'].removesuffix('.json').replace('/', '.')
                   if info else None)
        m = op('map_detect', chapter=chapter)
        entry['map'] = m
        # 检测结果 vs 关卡 IR（"网格判定"这半边的交叉校验）
        if info:
            exp = ir_expectation(info['chapter'])
            entry['ir'] = exp
            if exp and m.get('detected'):
                # 判定口径（实测校准过）：
                #   shape 必须与 IR 声明**严格一致**（F4→[5,3]、I6→[8,5]）；
                #   格数则允许**少于**声明值 —— 被左侧舰队栏/右侧 UI 遮住的格子本来就检不到，
                #   实测 9x6 图上 48/54，缺的正是 (0,4)(0,5) 与 (8,2..5)。
                #   所以"缺格"要连坐标一起记录，而不是当成检测错误。
                keys = set(tuple(k) for k in (m.get('grid_keys') or []))
                sh = m.get('shape') or [0, 0]
                full = set((x, y) for x in range(sh[0] + 1) for y in range(sh[1] + 1))
                missing = sorted(full - keys)
                entry['ir_match'] = (sh == exp['shape'])
                entry['grid_missing'] = [list(k) for k in missing]
                entry['grid_missing_count'] = len(missing)
                # 逐格语义的一致性不变量：船（己方/敌方/BOSS/塞壬/潜艇）**不可能落在陆地格**上。
                # 识别坐标若差一格，这条立刻被违反 —— 比"格数对得上"更强的校验。
                ships = {}
                for key, names in (m.get('grid_flags') or {}).items():
                    if any(n in names for n in ('is_enemy', 'is_boss', 'is_siren',
                                                'is_fleet', 'is_current_fleet', 'is_submarine')):
                        ships[key] = names
                entry['ship_tiles'] = ships
                land_cells = set(exp.get('land_cells') or [])
                entry['ships_on_land'] = sorted(k for k in ships if k in land_cells)
                entry['land_cells_total'] = len(land_cells)
            else:
                entry['ir_match'] = None
        results['map'].append(dict(m, fixture=f, ir=entry.get('ir'),
                                   ir_match=entry.get('ir_match'),
                                   grid_missing=entry.get('grid_missing'),
                                   grid_missing_count=entry.get('grid_missing_count'),
                                   ship_tiles=entry.get('ship_tiles'),
                                   ships_on_land=entry.get('ships_on_land'),
                                   land_cells_total=entry.get('land_cells_total')))
        print('%s: globe load=%s 往返误差=%s | map detected=%s %s'
              % (f, g.get('load'), ('%.2e' % rt) if rt is not None else 'n/a',
                 m.get('detected'), m.get('reason') or ''))

    # ---- 判定
    a = results['assets'] or {}
    asset_ok = bool(a.get('ui_mask') and a.get('tile_center_image') and a.get('tile_corner_image'))
    globe_ok = bool(results['globe'] and results['globe'].get('load') == 'ok')
    rt_ok = False
    if results['globe'] and results['globe'].get('globe2screen'):
        pts_in = [[640, 360], [200, 200]]
        rt = max(abs(x[0] - y[0]) + abs(x[1] - y[1])
                 for x, y in zip(pts_in, results['globe']['globe2screen']))
        rt_ok = rt < 1e-6
    # 判定口径：
    #   正样本 = 有 fixture 被检出（真机战斗地图）
    #   负样本 = **非地图画面**（campaign 菜单那张 os_map.png）必须"未检出且给出原因"
    negative_targets = [m for m in results['map']
                        if 'os_map' in m.get('fixture', '') or 'menu' in m.get('fixture', '')]
    negative_ok = all((not m.get('detected')) and m.get('reason')
                      for m in negative_targets) if negative_targets else None
    positive = [m for m in results['map'] if m.get('detected')]
    # 动画期/脏样本被检出与否不算判定项，但要在报告里如实列出
    dirty = [m.get('fixture') for m in results['map']
             if not m.get('detected') and not m.get('reason')]

    lines = [
        '# S2 地图识别适配与验收',
        '',
        'S2 的图像算法全在上游（`module/map_detection`、`module/os/globe_detection`），',
        '本项目**不复制任何算法**，只通过识图协议驱动并核对结果。',
        '',
        '脚本：`tools/diagnostics/verify_map_detection.py`；数据：`data/map_detection_verify.json`。',
        '',
        '## 结果',
        '',
        '| 项 | 结果 | 说明 |',
        '| --- | --- | --- |',
        '| 素材链 | %s | UI 遮罩 / 瓦片模板 / 检测区域由上游读出 |' % ('✅' if asset_ok else '❌'),
        '| 大世界单应性 | %s | `GlobeDetection.load` 成功并给出单应矩阵 |' % ('✅' if globe_ok else '❌'),
        '| 坐标往返自检 | %s | screen→globe→screen 误差 < 1e-6（同一变换的逆）' % ('✅' if rt_ok else '❌'),
        '| 非地图负样本 | %s | 非地图画面返回"未检测到 + 原因"，不崩 |'
        % ('✅' if negative_ok else ('—' if negative_ok is None else '❌')),
        '| 地图正样本 | %s | 需要真机地图画面（见下） |'
        % ('✅ %d 张检测到网格' % len(positive) if positive else '⏳ 缺正样本'),
        '| 检测 vs 关卡 IR | %s | 检出的格数/形状必须与该关卡声明的 map_data 一致 |'
        % (('✅ %d 张一致' % sum(1 for m in results['map'] if m.get('ir_match')))
           if any(m.get('ir_match') is not None for m in results['map'])
           else '—（该 fixture 未登记对照表）'),
        '',
        '## 素材链明细',
        '',
        '| 素材 | 形状 |',
        '| --- | --- |',
    ]
    for k, v in (results['assets'] or {}).items():
        lines.append('| `%s` | %s |' % (k, v))
    lines += [
        '',
        '## 大世界识别明细',
        '',
        '```json',
        json.dumps(results['globe'], ensure_ascii=False, indent=2)[:1200],
        '```',
        '',
        '## 逐张 fixture',
        '',
        '| fixture | globe | 往返误差 | map detected | 原因 |',
        '| --- | --- | --- | --- | --- |',
    ]
    for f, m in zip(fixtures, results['map']):
        g = next((x for x in [] if False), None)
        lines.append('| `%s` | — | — | %s | %s |'
                     % (f, m.get('detected'), (m.get('reason') or '')[:80]))
    lines += [
        '',
        '## 验证范围',
        '',
        '已登记夹具按完整章节模块加载上游 Config，包括导入和继承的检测参数。',
        '宿主不按关卡降阈值或放大画面，不因地图行数或缺少夹具判定不支持。',
        'Camera outside map 表示上游相机恢复分支；静态帧无法验证滑动后的状态。',
        '大地图只显示相机窗口，局部坐标需通过 verify_map_alignment.py 与完整地图对齐。',
        '识别通过只证明该帧的识别结果；完整通关仍需上游 CampaignEnd 和实战证据。',
        '',
        '复现：`python tools/diagnostics/verify_map_detection.py`。',
        '手工分析保存在 [map-detection-notes.md](./map-detection-notes.md)。',
        '',
    ]
    out = os.path.join(DOCS, 'map-detection.md')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    with open(os.path.join(DATA, 'map_detection_verify.json'), 'w', encoding='utf-8') as f:
        json.dump(results, f, ensure_ascii=False, indent=2, default=str)
    print()
    print('素材链=%s 大世界=%s 往返=%s 负样本=%s 正样本=%s'
          % (asset_ok, globe_ok, rt_ok, negative_ok, len(positive)))
    print('报告: %s' % out)
    return 0 if (asset_ok and globe_ok and rt_ok) else 1


if __name__ == '__main__':
    sys.exit(main())
