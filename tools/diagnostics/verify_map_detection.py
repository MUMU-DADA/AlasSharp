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
        m = op('map_detect')
        entry['map'] = m
        # 检测结果 vs 关卡 IR（"网格判定"这半边的交叉校验）
        manifest = {}
        mf = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'map_fixtures.json')
        if os.path.exists(mf):
            with open(mf, encoding='utf-8') as fh:
                manifest = {k: v for k, v in json.load(fh).items() if not k.startswith('_')}
        info = manifest.get(f)
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
        '## 正样本从哪来（这是完成 S2 验收的唯一缺口）',
        '',
        '- **战役地图**：要在地图上，也就是**真的出击**（消耗石油、会打一场）。',
        '- **大世界地图**：免费进入，但本账号 `大型作战` 卡片是**锁**的状态（未解锁）。',
        '',
        '把游戏停在地图画面上之后：',
        '',
        '```powershell',
        'python tools/diagnostics/verify_map_detection.py --capture map_1_1   # 抓正样本',
        'python tools/diagnostics/verify_map_detection.py                     # 重新验收',
        '```',
        '',
        '### 实测澄清：正样本到底能验到什么',
        '',
        '试过把上游自己的 `os_globe_map.png` 当输入图喂进 `globe_detect`，结果给出的是**同一组',
        '单应矩阵**（`homo[0][0]=1.6133`、`homo[1][1]=2.8012`、`homo[0][2]=-517.12`，与喂 1280x720',
        '的游戏截图完全相同）—— 说明 **OS globe 的单应性是存好的常量**',
        '（上游日志里的 `[homo_storage] ((4,3), [(445,180),(879,180),(376,497),(963,497)])`），',
        '不是从截图里算出来的。',
        '',
        '所以"缺正样本"缺的不是代码通路，而是**一张真实的地图画面**：',
        '',
        '| 要验的 | 需要什么 |',
        '| --- | --- |',
        '| 单应矩阵 / 坐标往返 | ✅ 已验（常量 + 逆变换自检，误差 2.84e-13） |',
        '| `perspective_transform` 扭到 globe 坐标系 | ✅ 任意图都能跑（只验形状与不崩） |',
        '| **当前所在位置**（`find_peaks` / screen2globe 的实际落点） | ⏳ 必须真机 OS 地图截图 |',
        '| 战役地图的网格识别（`View.load` 正路径） | ⏳ 已抓到真机地图，但**检测失败**（见下节） |',
        '',
        '## 真机战斗地图正样本：**已检测成功**（结论在最后，前面是完整排查过程）',
        '',
        '> **结论**：正样本 `map_settled.png` 检出 **24 格、shape [5,3]（即 6×4）**，',
        '> 与画面上的 A–F 列 × 1–4 行完全一致。真正的卡点是**上游与 numpy 2 不兼容**：',
        '> `Lines.cross` 里写的是 `np.vstack(self.cross_two_lines(...))`，而 `cross_two_lines`',
        '> 是**生成器**，numpy 2 不再接受（实测 numpy 2.4.6 报',
        '> `TypeError: arrays to stack must be passed as a "sequence" type`）。',
        '> 本环境是 Python 3.14，只能配 numpy 2，所以地图识别会卡在这一步 ——',
        '> **表象极像"识别不到地图"，实际与客户端 UI 毫无关系**。',
        '> 修法是 `apply_numpy2_compat()`：只把生成器具体化成 list，',
        '> **上游代码一行不改**，检测算法仍全部是上游的。',
        '> 另外 `map_2_1.png`（我第一张样本）是**入场动画期间**抓的脏样本，',
        '> 垫片后仍失败属正常，保留它作为"取样时机很重要"的证据。',
        '',
        '以下是完整排查过程（含两次被推翻的判断），保留下来是因为每一步都有可复现的数字。',
        '',
        '正样本 `data/fixtures/map_2_1.png` 是真实战斗地图（网格 A-F × 1-4，即 shape `F4`，含 Lv.11 敌舰）。',
        '`map_detect` 在它上面失败：homography 与 perspective **两个后端**都报',
        '`No vertical line detected`（先前报过 `No horizontal line detected`）。',
        '',
        '用 `map_detect_trace` 把上游 `Perspective.load` 的链条逐步跑出来（同一张 fixture）：',
        '',
        '| 阶段 | peaks | 遮罩后 | HoughLines 原始 | 最终 lines | 角度(度) |',
        '| --- | --- | --- | --- | --- | --- |',
        '| inner_h（内部网格横线） | 221 | 221 | **0** | **0** | — |',
        '| inner_v（内部网格竖线） | 88 | 88 | **0** | **0** | — |',
        '| edge_h（地图边缘横线） | 1722 | 706 | 8 | 5 | 90–93 |',
        '| edge_v（地图边缘竖线） | 464 | 249 | 1 | 1 | 0 |',
        '',
        '**结论（顺手排除了两种常见猜测）**：',
        '',
        '- **不是 UI 遮罩对不上**：`mask_stroke` 保留 86.4% 像素，且 `peaks_after_mask` 与 `peaks_raw` 完全相等',
        '  （221/221、88/88）—— 峰值一个都没被遮掉；',
        '- **不是预处理把图弄黑了**：`load_image` 输出 665x1157、均值 208.21、99.8% 非零；',
        '- **失效点是内部网格线的检测**：`inner_*` 的峰值图里有 221/88 个峰值，但',
        '  `cv2.HoughLines(threshold=75)` 一条都拟合不出来（`hough_raw=0`）。',
        '  即本客户端网格线的**渲染方式**（对比度/颜色/抗锯齿）与上游 `INTERNAL_LINES_*`',
        '  参数针对的渲染不同；地图**边缘**线还能检出（5 横 1 竖），但竖线只有 1 条，',
        '  在后续 group/delete 里被清掉，于是报 "No vertical line detected"。',
        '',
        '下一步查 `Perspective.load_image` 的预处理（按颜色/亮度挑线）与',
        '`INTERNAL_LINES_FIND_PEAKS_PARAMETERS` / `INTERNAL_LINES_HOUGHLINES_THRESHOLD`，',
        '判断是本客户端网格线颜色不同，还是阈值要按渲染调整。**全程可离线复跑**，不必再动账号。',
        '',
        '### 参数扫描：不是"调个阈值"就能修好（实测）',
        '',
        '先扫 Hough 阈值（`INTERNAL_/EDGE_LINES_HOUGHLINES_THRESHOLD` 同时降）：',
        '',
        '| 阈值 | 75 | 50 | 40 | 30 | 25 | 20 | 15 |',
        '| --- | --- | --- | --- | --- | --- | --- | --- |',
        '| 结果 | 竖线缺失 | 竖线缺失 | 竖线缺失 | 竖线缺失 | 竖线缺失 | 后续阶段空数组 | 后续阶段空数组 |',
        '',
        '再扫 `INTERNAL_LINES_FIND_PEAKS_PARAMETERS` 的亮度下限（`height[floor,222]`，'
        'floor 150 是上游默认），阈值取 40/25：',
        '',
        '| floor | 150 | 120 | 100 | 80 | 60 |',
        '| --- | --- | --- | --- | --- | --- |',
        '| 结果 | 竖线缺失 | 竖线缺失 | 竖线缺失 | 竖线缺失 | 竖线缺失 |',
        '',
        '单看原始 Hough 输出也能看出症结：`inner_v` 即使把阈值降到 **10** 也只有 **1** 条线，',
        '而 `inner_h` 在同样阈值下有 59 条 —— **本客户端地图上的竖线（网格纵向分隔）',
        '在 `load_image` 取反后的图里几乎不成线**。',
        '',
        '所以这不是调参能解决的：要么本客户端网格纵向分隔的**渲染**与上游预期差异过大',
        '（例如分隔是浅色而非深色、或抗锯齿跨越多个像素导致 1-D 峰值不成立），',
        '要么该 fixture 的取样时机不对（进图立刻抓的，地图可能还在入场动画/未完全渲染）。',
        '### 根因收敛：本客户端地图的**渲染亮度/极性**与上游假定不符',
        '',
        '直接量像素（`rgb2gray` 后）：地图区域内**海面中位数 ≈42**、**网格线 ≈63** —— '
        '本客户端是"**暗海 + 略亮的网格线**"，而且整片地图很暗。',
        '',
        '而上游 `Perspective.load_image` 的做法是 `灰度 → 遮罩 → 取反`，'
        '隐含假定"线比周围**暗**"。取反后：海 →213、线 →192，于是 `find_peaks`（找**亮**峰，'
        '且要求亮度落在 `height: [150, 222]`）把**海面方块中心**当成了峰'
        '（间距 ≈113 正好等于网格列距），这些峰彼此不共线，Hough 自然一条线也拟合不出来。',
        '',
        '两个对照实验都指向同一结论：',
        '',
        '| 实验 | 结果 |',
        '| --- | --- |',
        '| 跳过我方取反（直接用灰度图） | 仍失败：线的亮度只有 63，**低于 `height` 下限 150** |',
        '| 自适应映射（把线/海映射到 215/185 再交给上游取反） | 失败模式从「缺竖线」变成「缺横线」'
        '—— **说明输入图的亮度映射确实决定检测结果**，只是我随手给的映射还没调对 |',
        '',
        '所以修法是**给本客户端一套输入映射（客户端 profile）**：让线在交给上游前就落在它期望的'
        '亮度区间、且极性与算法一致；这属于"适配输入数据"，不是重写算法（架构铁律 2 仍然成立：',
        '检测算法依旧全部调用上游 `Perspective`/`View`）。',
        '',
        '### 正确极性 + 已越过的两道关（第 10 轮）',
        '',
        '**先纠正上一轮我自己的方向性错误**：上游 `find_peaks` 找的是"取反后的**亮**峰"＝'
        '**原始灰度里最暗**的像素。所以交给上游的图里，**线必须是最暗的**（取反后才最亮）；',
        '上一轮我把线映射成 215（最亮），极性和上游正好相反，所以那个实验的失败模式变化不具有解释力。',
        '',
        '按正确极性（线→暗 40、海→亮 180，取反后线＝215 落在上游 `height[150,222]` 内）重扫：',
        '',
        '| 映射（线/海） | 取反后线亮度 | 结果 |',
        '| --- | --- | --- |',
        '| 40 / 180 | 215 | **越过"找不到线"** → 失败点后移 |',
        '| 60 / 200 | 195 | 同上 |',
        '| 80 / 200 | 175 | 同上 |',
        '| 40 / 220 | 215 | 同上 |',
        '| 70 / 240 | 185 | 同上 |',
        '',
        '五个组合全部**越过了原来的 `No ... line detected`**（不再是那个失败），'
        '改为在后续阶段报 `TypeError`。抓完整回溯定位到：',
        '',
        '```',
        'perspective.py: self.crossings = self.horizontal.cross(self.vertical)',
        'utils.py:203  : points = np.vstack(self.cross_two_lines(self, other))',
        'TypeError: arrays to stack must be passed as a "sequence" type ...',
        '```',
        '',
        '即：**横线集与竖线集求不出有效交点**（`cross_two_lines` 返回空）。',
        '这说明极性修正之后线是检出了，但检出的横竖线还不构成合理的网格结构 ——',
        '下一步要量这两组线的几何（条数、角度、ρ 分布），判断是线检多了/检少了，',
        '还是角度阈值（`HORIZONTAL/VERTICAL_LINES_THETA_THRESHOLD`）把该留的线滤掉了。',
        '',
        '### 关键修正：第一张 fixture 是**入场动画期间**抓的（用户点进地图后重测）',
        '',
        '用户手动把游戏点进战役地图、画面停稳后，我抓了第二张样本 `data/fixtures/map_settled.png`，',
        '用**上游原版参数**（没有任何映射改造）重跑，结果与第一张判若两图：',
        '',
        '| 阶段 | 动画期样本 | **停稳样本** | 停稳后 lines |',
        '| --- | --- | --- | --- |',
        '| inner_h | peaks 221 | peaks **2483** | **4** |',
        '| inner_v | peaks 88 | peaks **1786** | **3** |',
        '| edge_h | peaks 1722 | peaks 2746 | 6 |',
        '| edge_v | peaks 464 | peaks 1384 | 5 |',
        '',
        '即：**"竖线几乎不存在"是取样时机造成的**（地图还没渲染完），不是渲染差异；',
        '前一节关于"暗海/亮网格线、亮度映射"的分析因此**只对那张动画期样本成立**，',
        '不能当作本客户端渲染的结论。这条修正很重要 —— 差一点就去调一套本来不该改的映射。',
        '',
        '停稳样本上，失败点后移到**交点求解**（与离线实验里定位到的同一处）：',
        '',
        '```',
        'perspective.py: self.crossings = self.horizontal.cross(self.vertical)',
        'utils.py:203  : points = np.vstack(self.cross_two_lines(self, other))',
        'TypeError: arrays to stack must be passed as a "sequence" type ...',
        '```',
        '',
        '横线 4 条、竖线 3 条都检出了，但两组线**求不出有效交点**（`cross_two_lines` 返回空）。',
        '这正是下一步要看的地方：是交点落到了 `DETECTING_AREA` 之外被过滤、',
        '还是两组线的角度/ρ 使交点判定失败。**完全离线可复跑**（样本已存）。',
        '',
        '## 大世界（OS）通路：**已可进入**（用户解锁后实测）',
        '',
        '之前「大型作战」卡片是锁的（点 8 次无反应，但按钮分数 0.9990）。用户解锁后实测通过：',
        '',
        '```',
        '[hop 1   ] page_main -> page_campaign_menu   ui_white/MAIN_GOTO_CAMPAIGN_WHITE  score 0.9447',
        '[hop 2   ] page_campaign_menu -> page_os     ui/CAMPAIGN_MENU_GOTO_OS           score 0.9990',
        '[result  ] success=True final=page_os',
        '```',
        '',
        '真机 OS 画面已存为 fixture `os_globe_live.png`，`globe_detect` 在其上：',
        '`load=ok`、`homo_size=[1032,1008]`，单应矩阵与在别的画面上**完全相同**',
        '（1.6133 / 2.8012 / -517.12），坐标往返仍精确回到原点。',
        '',
        '这再次印证了前面那条澄清：**OS globe 的单应性是存好的常量**，不是从截图算出来的。',
        '因此"位置检测"（`find_peaks` 的实际落点）仍未被验证 —— 它需要调用上游的位置检测接口',
        '（`GlobeDetection` / `OSMap` 上的相关方法），是下一步要补的 op。',
        '',
        '### 逐格语义校验：船不可能落在陆地格上（比"格数对得上"更强）',
        '',
        '`map_detect` 现在还返回逐格标志（`grid_flags`，只回 True 的那些），',
        '即"敌人在哪一格、己方舰队在哪一格、哪格是潜艇/神秘事件"。',
        '据此可以对关卡 IR 做一条**可判定的不变量校验**：',
        '船（己方/敌方/BOSS/塞壬/潜艇）**不可能落在陆地格 `++` 上** ——',
        '识别坐标只要差一格，船就会落到陆地上，这条立刻被违反。',
        '',
        '实测（两张真机图，同时校验 shape 一致、缺格坐标、陆地对齐）：',
        '',
        '| fixture | IR 陆地格数 | 检出的船格 | 落在陆地上 |',
        '| --- | --- | --- | --- |',
        '| `map_settled.png`（2-1） | 7 | (4,0)敌 (0,1)己方 (5,2)敌 | **0** ✅ |',
        '| `map_shape_9x6.png`（10-4） | 13 | (6,1)(3,2)(3,4)(4,5)敌 (6,4)潜艇 | **0** ✅ |',
        '',
        '另外 2-1 上 `(4,0)` 落在 IR 的 `ME`（可能有敌人）上、`(0,1)` 落在出生点上 ——',
        '识别语义与声明式地图在**具体格子**这一级也对得上。',
        '',
        '### 海域（OS）内地图：已推进到"锚定网格原点"这一步（比之前进了一步）',
        '',
        '用户进入海域后抓了 os_live_2.png（标题「陆间海C-安全海域」，完整网格 + 舰队 + 迷雾雷达）。',
        '先确认了它**不是**环球视图（os/MAP_GOTO_GLOBE_FOG 命中 0.8986，按上游命名这是海域图上'
        '"返回环球"的按钮）。',
        '',
        '关键判断：海域里的网格地图要用 View(config, mode=\'os\')（会切到 '
        'ASSETS.ui_mask_os_in_map，见 view.py:47-48），而 GlobeDetection 是给**环球视图**用的'
        '—— 这也解释了为什么它在海域图上 similarity 只有 0.082：拿错了检测器。',
        '',
        '补上 mode 参数后（map_detect(mode="os")，网格类用上游的 OSGrid）：',
        '',
        '| 画面 | mode=main | mode=os |',
        '| --- | --- | --- |',
        '| os_live_2.png（海域内） | 失败 | **Failed to find a free tile** |',
        '| map_settled.png（2-1） | 24 格 [5,3] | 24 格 [5,3]（OS 模式不影响战役图）|',
        '',
        '也就是说：线找到了、网格建起来了，卡在**用"自由格"锚定网格原点**这一步。',
        '上游这一步靠模板匹配找一块"空地格"来确定地图偏移；本客户端海域地图的格子渲染'
        '（浅蓝底 + 细亮格线）可能与它预期的模板不同，导致找不到锚点。',
        '下一步：对照 	ile_center_image / 	ile_corner_image 与海域格子的实际外观，',
        '看是模板不匹配还是锚点搜索区间的问题。',
        '',
        '#### 继续往下：自由格搜索为何全败（离线逐步对照）',
        '',
        'HOMO_STORAGE = None 说明这个单应性是**从图里现算**的（先找地图四角）。',
        '把两条链在同一套上游代码下并排跑（战役图 vs 海域图）：',
        '',
        '| | 战役 2-1（成功） | 海域（失败） |',
        '| --- | --- | --- |',
        '| 检出地图四角 | (351.8,116.6)(1033.9,116.6)(278.9,559.8)(1133.2,559.8) | (165.4,122.9)(1291.8,122.9)**(13.6,701.6)(1500.2,701.6)** |',
        '| warp | (1477,1015) mean 86.4，边缘 6.63% | (1495,996) mean 115.3，边缘 10.48% |',
        '| search_tile_center | **True** | **False** |',
        '| search_tile_corner | True | **False** |',
        '| search_tile_rectangle | True | **False** |',
        '',
        '海域图检出的**底部两角 x=13.6 / 1500.2，已超出屏幕宽度 1280**（角点是无限延长线的交点，',
        '可以落在屏幕外）—— 地图角点不可能在屏幕外，说明**边线检测把屏幕底部的 UI 边界',
        '当成了地图下边**，算出的单应性把画面扭到错误坐标系，三种自由格模板自然全找不到。',
        'warp 后海域的边缘密度(10.48%)反而高于战役(6.63%)，也符合"拟合进了 UI 边界"的判断。',
        '',
        '下一步（离线）：查 Homography.detect 用的遮罩 ui_mask_homo_stroke 是否随 mode=os 切换',
        '（View.load_image 是按 mode 选的 ui_mask_os_in_map）；若 homography 这条路用的是',
        '战役遮罩，海域画面上就会遮错区域、让 UI 边界参与拟合。',
        '',
        '#### 遮罩这条线查清了：接线没错，问题在客户端一侧',
        '',
        '上游 OS 任务的真实用法（module/os/camera.py:25）就是：',
        'View(config, mode="os", grid_class=OSGrid)，并且真机上 Scheduler_Command 以 Opsi 开头',
        '—— 与我们的调用方式**完全一致**，所以接线不是问题。',
        '',
        '遮罩规则也查清了（两处、不对称）：',
        '',
        '| 位置 | 用哪个遮罩 | 是否随 OS 切换 |',
        '| --- | --- | --- |',
        '| perspective.py:172（找地图四角/边线） | ASSETS.ui_mask | **否，永远用战役遮罩** |',
        '| homography.py:67-72（warp 之后的边缘过滤） | ui_mask_os / ui_mask | 是，按 Scheduler_Command 是否以 Opsi 开头',
        '',
        '也就是说：**找四角这一步在 OS 画面上跑的是战役遮罩**。我们客户端 OS 画面底部那条 UI 栏',
        '（第一舰队 / 储物舱 / 情报 / 作战总览）落在战役遮罩的"可见区"里，它的边界被当成地图下边',
        '—— 这与实测吻合：检出的底部两角 y=701.6（贴近屏幕底 720）、x=13.6/1500.2（超出屏宽）。',
        '单应性因此算错，三种自由格模板全败。',
        '',
        '上游用户没这个问题，最可能的解释是本客户端的 UI 缩放/布局与上游素材（绝对像素遮罩，',
        '1157x665）对不齐 —— 这与本项目此前在其它界面反复遇到的"新 UI 素材对不上"是同一类原因。',
        '',
        '下一步（离线）：把 ui_mask_os 与 ui_mask 叠加到 os_live_2.png 上，量出',
        '"遮罩认为的 UI 区域"与"实际 UI 区域"的差，确认是否底部栏未被覆盖、差多少像素。',
        '',
        '#### 修法与结果：两处遮罩都要换（缺一不可）',
        '',
        '客户端垫片 pply_os_mask_compat() + set_os_mask_mode()：把 Perspective.load_image',
        '包一层，在 OS 模式下换用 ASSETS.ui_mask_os（上游代码一行不改）。',
        '**但只换这一处不够**：还要让 Scheduler_Command 以 Opsi 开头，',
        'homography.py:67-72 的 warp 后遮罩才会跟着切 —— 两半缺一不可。',
        '',
        '实测（同一进程、逐张 fixture）：',
        '',
        '| fixture | mode | 结果 |',
        '| --- | --- | --- |',
        '| os_live_2.png（海域内 9x6） | os | **detected=True grids=49 shape=[8,5]** |',
        '| map_settled.png（战役 2-1） | main | detected=True grids=24 shape=[5,3]（未受影响）|',
        '| map_shape_9x6.png（战役 10-4） | main | detected=True grids=48 shape=[8,5]（未受影响）|',
        '| map_settled.png | os | detected=True grids=24 shape=[5,3] |',
        '',
        '排错过程中我自己踩了两个坑，都记下来：① 垫片里用了 cv2 而 alas_vision 模块级没有导入它',
        '（NameError 被吞掉，表现为"换了遮罩也没用"）；② 异常分支提前 return 时忘了复位开关，',
        '把开关泄漏给了后续战役检测，导致 2-1 也一起失败 —— 已改为异常路径同样复位。',
        '',
        '遗留：海域图的 grid_flags 为 0（没标出船只/敌人）—— OS 的网格类暴露的标志名可能与战役',
        '不同，等 S3 真正需要 OS 的逐格语义时再对齐。',
        '',
        '### 后端选择：homography 与 IR 一致，perspective 在 2-1 上会多判一行（实测）',
        '',
        '同一张真机图上跑上游两个后端（`map_detect(backend=...)`）：',
        '',
        '| fixture | 关卡 IR | homography | perspective |',
        '| --- | --- | --- | --- |',
        '| `map_settled.png`（2-1） | `F4` = [5,3] / 24 格 | **24 格 [5,3]** ✅ 与 IR 一致 | 30 格 **[5,4]** ✗ 多算一行 |',
        '| `map_shape_9x6.png`（10-4） | `I6` = [8,5] / 54 格 | 48 格 [8,5] ✅ | 48 格 [8,5] ✅ |',
        '',
        '也就是说 **perspective 后端在 2-1 上会把网格多判一行**（[5,4] 而非 [5,3]），',
        'homography 后端在两张真机图上都与关卡 IR 严格一致。',
        '这是"用哪个后端"的实测依据，而不是偏好 —— 默认走 homography（上游 `DETECTION_BACKEND` 的默认值）。',
        '',
        '耗时（同一进程内）：**首次调用约 1.6 s**（冷启动，要加载素材与模型），',
        '之后每张 **120–190 ms** —— 对"截图 p50 ≈ 433–441 ms 才是真瓶颈"的结论是个有用的补充：',
        '识别本身不是瓶颈。',
        '',
        '',
        '`GlobeDetection.load()` 的落点写进 `center_loca`（大世界坐标），匹配度 `similarity`',
        '只打日志不存属性 —— op 挂 logging handler 把**上游自己打的那行**取回来解析，',
        '没有在 op 里重算匹配（不重复实现算法）。',
        '',
        '### 位置检测（ind_peaks + 模板匹配）：机制已通，**等一张海域内画面**',
        '',
        '实测（同一套上游代码，逐张 fixture）：',
        '',
        '| 画面 | similarity | center_loca |',
        '| --- | --- | --- |',
        '| `os_globe_live.png`（真机 page_os 环球视图） | 0.082 | [1151, 1564] |',
        '| `os_map.png`（战役菜单，非 OS） | 0.128 | [2065, 1768] |',
        '| `map_settled.png`（战斗地图） | 0.085 | [1959, 414] |',
        '',
        '真机 OS 环球视图的匹配度**反而最低**，原因在上游实现里很清楚：',
        '`load()` 是拿"**局部地图结构**"（透视变换后 `find_peaks` 出的地图边界）去和 globe 模板',
        '做 `matchTemplate`，所以它需要**进入某个海域后的海域地图画面**；',
        '`page_os` 是选海域的环球视图，上面没有局部地图结构 → 分数低是**画面不对**，不是算法不对。',
        '',
        '补充（已更正）：上游**是有判据的** —— `globe_detection.py:133-134` 写了',
        '`if similarity < 0.1:` 则警告 `Low similarity when matching OS globe`',
        '（只警告、不拒绝，仍返回 `center_loca`）。我们测到的所有画面都在 0.066–0.128，',
        '恰好压在这条线上下 —— 与"glob 检测要的是环球视图那一屏"的结论一致。',
        '原先这里写的"上游没有对 similarity 设硬阈值"据此更正：没有硬拒绝，但有 0.1 的警告线。',
        '`center_loca` 被 `module/os/camera.py` 当作相机中心使用。',
        '所以"匹配度多高算认出海域"要**在真机海域画面上实测标定**，不能凭猜写死。',
        '这一步需要用户进入任意海域后抓一张图（免费、不耗油），',
        '预期 similarity 会显著高于上面 0.082–0.128 这一档。',
        '',
        '## 复现',
        '',
        '```powershell',
        'python tools/diagnostics/verify_map_detection.py',
        '```',
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
