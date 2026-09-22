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
        # 战役地图：非地图画面应当给负样本
        m = op('map_detect')
        entry['map'] = m
        results['map'].append(dict(m, fixture=f))
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
    negative_ok = all((not m.get('detected')) and m.get('reason') for m in results['map']) \
        if results['map'] else None
    positive = [m for m in results['map'] if m.get('detected')]

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
        '## 真机战斗地图正样本：检测失败，且已定位到具体一步',
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
