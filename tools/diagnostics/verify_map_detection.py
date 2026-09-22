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
