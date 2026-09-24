# -*- coding: utf-8 -*-
"""判据临界扫描：本客户端上还有哪些按钮卡在 `appear_on` 阈值边上。

**为什么做**（2026-09-23 真机卡点的直接后续）：`IN_MAP` 的相似度落在 10.0~10.4、
上游阈值是 10，于是"游戏已进图"被判成"不在图内"，`enter_map` 白等 62s 后 `GameStuckError`。
那个临界值在**离线存档帧上早就量到过**，只是当时判断"存帧不足以证明现场"。
这说明一件事：**阈值类的判据必须成批地量，不能等它卡住再单独去查。**

做法：拿真机帧（含卡死现场帧与若干历史帧），对 `module.*.assets` 里**所有带颜色判据的按钮**
算一遍 `color_similarity(get_color(...), button.color)`，列出落在**临界带**里的那些：

  * `< 10`  → 当前判"命中"，但余量很小 —— 客户端的微小渲染差异就会让它翻面；
  * `10~20` → 当前判"未命中"，但与非命中帧（实测 ≥ 83）相比仍离得很近 —— **就是 IN_MAP 那类**。

产出 `docs/archive/reports/button-threshold-sweep.md`（入库）与 `data/button_threshold_sweep.json`。
用法：
    python tools/diagnostics/button_threshold_sweep.py
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
DOCS = ROOT / 'docs'
DATA = ROOT / 'data'
FRAGILE = (4.0, 20.0)          # 临界带：余量小或"刚好没命中"

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

os.chdir(ENGINE)
sys.path.insert(0, str(ROOT / 'tools'))     # 宿主模块在 tools/ 下，先入 path 再 chdir 到上游
import alas_vision as av                                       # noqa: E402
from module.base.utils import color_similarity, get_color       # noqa: E402

#: 真机帧：卡死现场帧 + 历史真机帧（地图/非地图都要，才能看出"命中"与"未命中"的间隔）
FRAMES = [
    ('_live_enter_map_stall.png', '真机 1-1 卡死现场（游戏在地图内）'),
    ('_14_inmap.png', '真机 1-4 地图帧'),
    ('_71_inmap.png', '真机 7-1 地图帧'),
    ('_boss122.png', '真机地图帧（该帧 IN_MAP 相似度 3.33）'),
    ('s3_final_stage.png', '真机第 1 章选择页（非地图）'),
    ('_shot_campaign_map.png', '真机章节选择页（非地图）'),
]


#: 需要预先导入的 assets 模块（宿主是**懒加载**的：不导入就没有素材清单）。
#: 只列引擎判定会用到的业务模块，**不** walk 整个 `module` 包 —— 那会把 webui 之类一起拖进来
#: （webui 会替换假 PIL 模块，污染后续图像代码）。
ASSET_MODULES = (
    'ui', 'ui_white', 'handler', 'map', 'combat', 'os', 'os_handler', 'raid',
    'coalition', 'meowfficer', 'freebies', 'event', 'shop', 'shop_event', 'storage',
    'retire', 'research', 'dorm', 'island', 'tactical', 'task', 'mission', 'campaign',
)


def buttons_with_color():
    """返回 [(asset_id, button, expected_color)]：带颜色判据、区域可用的按钮。"""
    import importlib
    for name in ASSET_MODULES:                 # 先把素材模块导进来，否则清单是空的
        try:
            importlib.import_module(f'module.{name}.assets')
        except Exception:
            continue
    out = []
    for asset_id in sorted(av._asset_id_map().values()):
        try:
            button = av._resolve(asset_id)
            expected = av._color_of(button)
            area = getattr(button, 'area', None)
            if not expected or not area or len(area) != 4:
                continue
            out.append((asset_id, button, expected))
        except Exception:
            continue
    return out


def main() -> int:
    frames = [(name, note) for name, note in FRAMES if (DATA / name).is_file()]
    if not frames:
        print('[跳过] data/ 下没有可用的真机帧（data/ 不入库）；判据扫描未跑。')
        return 0

    buttons = buttons_with_color()
    print(f'=== 判据临界扫描（{len(buttons)} 个带颜色判据的按钮 × {len(frames)} 张真机帧）===')
    per_frame, in_band = {}, {}
    for name, note in frames:
        av.op_screenshot_load({'path': str(DATA / name)})
        image = av._state['image']
        hits = []
        for asset_id, button, expected in buttons:
            try:
                got = [float(v) for v in get_color(image, button.area)]
                similarity = float(color_similarity(got, expected))
            except Exception:
                continue
            if FRAGILE[0] <= similarity <= FRAGILE[1]:
                hits.append({'asset': asset_id, 'similarity': round(similarity, 2),
                             'expected': [round(float(v), 1) for v in expected],
                             'got': [round(v, 1) for v in got]})
        hits.sort(key=lambda h: h['similarity'])
        per_frame[name] = {'note': note, 'fragile': hits}
        for hit in hits:
            in_band.setdefault(hit['asset'], []).append((name, hit['similarity']))
        print(f"  {name:32s} 临界 {len(hits):3d} 个"
              + (f"  最小: {hits[0]['asset']} {hits[0]['similarity']}" if hits else ''))

    repeated = {asset: hits for asset, hits in in_band.items() if len(hits) >= 2}
    lines = [
        '# 判据临界扫描：还有哪些按钮卡在阈值边上',
        '',
        '> 本页由 `tools/diagnostics/button_threshold_sweep.py` 从真机帧生成，**不手写**。',
        '> 起因：`IN_MAP` 的相似度落在 10.0~10.4（上游阈值 10）导致"已进图"被判成"不在图内"，',
        '> 真机白等 62s 后 `GameStuckError` —— 阈值类判据必须**成批地量**，不能等它卡住再单独查。',
        '',
        f'扫描范围：{len(buttons)} 个带颜色判据的按钮 × {len(frames)} 张真机帧；'
        f'临界带取 **{FRAGILE[0]} ~ {FRAGILE[1]}**（<10 判命中但余量小；10~20 判未命中但离得很近）。',
        '',
        '## 在多张帧上都落在临界带的素材（优先复核）',
        '',
        '| 素材 | 出现帧数 | 相似度 |',
        '| --- | --- | --- |',
    ]
    for asset, hits in sorted(repeated.items(), key=lambda kv: -len(kv[1])):
        detail = '、'.join(f'{name.split("_")[1] if "_" in name else name}:{sim}' for name, sim in hits[:4])
        lines.append(f'| `{asset}` | {len(hits)} | {detail} |')
    lines += ['', '## 逐帧临界清单', '']
    for name, info in per_frame.items():
        lines += [f'### `{name}`（{info["note"]}）', '',
                  '| 素材 | 相似度 | 期望色 | 实测色 |', '| --- | --- | --- | --- |']
        for hit in info['fragile'][:15]:
            lines.append(f"| `{hit['asset']}` | **{hit['similarity']}** | {hit['expected']} | {hit['got']} |")
        if not info['fragile']:
            lines.append('| — | 无 | | |')
        lines.append('')
    lines += [
        '## 怎么用这份清单',
        '',
        '临界 ≠ 缺陷：按钮不在屏上时相似度天然离得远，落在 4~20 说明"离阈值很近"。',
        '要判断是否真的会误判，需要看**该按钮应当出现的画面**上它的取值（像 `IN_MAP` 那样：',
        '地图帧 10.19 却应当命中）。所以复核顺序是：先在多帧上稳定出现的（上表），',
        '再结合"它本该在哪些页面命中"去看。',
        '',
        '复现：`python tools/diagnostics/button_threshold_sweep.py`。',
        '',
    ]
    DOCS.mkdir(parents=True, exist_ok=True)
    (DOCS / 'archive/reports/button-threshold-sweep.md').write_text('\n'.join(lines), encoding='utf-8')
    (DATA / 'button_threshold_sweep.json').write_text(json.dumps(
        {'fragile_band': FRAGILE, 'buttons': len(buttons), 'frames': per_frame},
        ensure_ascii=False, indent=1), encoding='utf-8')

    print()
    print(f'  多帧稳定落在临界带的素材: {len(repeated)} 个'
          + (f"（{', '.join(sorted(repeated)[:6])}）" if repeated else ''))
    print('报告: docs/archive/reports/button-threshold-sweep.md')
    print('结果: OK（清单已生成；临界≠缺陷，复核方法写在报告里）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
