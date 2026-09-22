# -*- coding: utf-8 -*-
"""地图识别的**偏移对齐**诊断：把检出窗口对到关卡 IR 的地图坐标上。

为什么需要它：地图大于屏幕时（例如活动图 9x8 装不下 1280x720），检出的是**可见窗口**
（该样本 8x4），它的网格坐标是**窗口内局部坐标**，不能直接和 IR 的地图坐标比。
> 此前 2-1/10-4/困难 1-4 都恰好完整可见，所以这个缺口一直没暴露；
> 活动图（幽影迷城 A1，I8 = 9x8 = 72 格）是第一个"地图大于屏幕"的样本。

做法（纯数据，不碰视觉）：把窗口在 IR 地图上**滑动**，检验检出的标志是否与 IR 地形自洽：
  - 敌人 / BOSS / 塞壬：必须落在 IR 允许的格子上（ME/MS/MB/MM/--/__/SP/MA），**不能落陆地 ++**；
  - 己方舰队 **不参与硬判据**（`is_fleet`/`is_current_fleet` 是**派生指派**，可能指错格；
    实测困难 1-4 上 `is_current_fleet` 指到陆地格，把唯一候选也否掉了）。
输出候选偏移集合：
  - 唯一候选  → 窗口位置确定，可做严格逐格校验；
  - 多个候选  → 窗口情形约束不足（列出，且**无候选为空**即说明与 IR 不矛盾）；
  - 空集      → 与 IR 矛盾（识别或 IR 有问题，必须查）。

**试过但走不通的消歧手段（记录免得重试）**：想用"每格地形"当额外约束，于是逐格调用上游
`GridPredictor.predict_sea()` 在活动图上取地形 —— 结果**每一格都返回"非海面"**
（活动图格子是灰暗方块，与常规图的蓝色海面渲染完全不同，预测器的颜色判据在这里没有区分度）。
所以活动图仍是"多个候选、无矛盾"这一档；要让窗口偏移唯一确定，需要更强的约束
（例如结合相机跟随当前舰队的假设 + 舰队在地图中的已知位置，但后者依赖出击状态，单张截图给不出）。
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
sys.path.insert(0, HERE)
import alas_vision as av          # noqa: E402

FIXTURES = os.path.join(ROOT, 'data', 'fixtures')
CAMPAIGN = os.path.join(ROOT, 'data', 'campaign')
ENEMY_ALLOWED = {'ME', 'MS', 'MB', 'MM', '--', '__', 'SP', 'MA'}

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def op(name, **args):
    resp = json.loads(av.handle_line(json.dumps({'id': 1, 'op': name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(resp.get('error'))
    return resp['result']


def manifest():
    path = os.path.join(HERE, 'map_fixtures.json')
    if not os.path.exists(path):
        return {}
    with open(path, encoding='utf-8') as f:
        return {k: v for k, v in json.load(f).items() if not k.startswith('_')}


def ir_rows(chapter_rel):
    path = os.path.join(CAMPAIGN, chapter_rel.replace('/', os.sep))
    if not os.path.exists(path):
        return None
    with open(path, encoding='utf-8') as f:
        ir = json.load(f)
    return [r.split() for r in (ir.get('map', {}).get('map_data') or '').strip().split('\n')
            if r.strip()]


def candidate_offsets(flags, rows, win_w, win_h):
    H = len(rows)
    W = max(len(r) for r in rows)
    out = []
    for dy in range(0, max(H - win_h + 1, 0)):
        for dx in range(0, max(W - win_w + 1, 0)):
            ok = True
            for key, names in flags.items():
                lx, ly = (int(v) for v in key.split(','))
                mx, my = lx + dx, ly + dy
                if not (0 <= my < H and 0 <= mx < len(rows[my])):
                    ok = False
                    break
                tok = rows[my][mx].upper()
                if any(n in names for n in ('is_enemy', 'is_boss', 'is_siren')) \
                        and tok not in ENEMY_ALLOWED:
                    ok = False
                    break
            if ok:
                out.append((dx, dy))
    return out


def main():
    mf = manifest()
    only = [a for a in sys.argv[1:] if not a.startswith('-')]
    results = []
    for fixture, info in sorted(mf.items()):
        if only and fixture not in only:
            continue
        path = os.path.join(FIXTURES, fixture)
        if not os.path.exists(path) or not info.get('chapter'):
            continue
        op('screenshot_load', path=path)
        chapter = 'campaign.' + info['chapter'].removesuffix('.json').replace('/', '.')
        det = op('map_detect', chapter=chapter)
        entry = {'fixture': fixture, 'chapter': info['chapter'], 'name': info.get('name')}
        if not det.get('detected'):
            reason = str(det.get('reason') or '')
            entry['status'] = ('待上游相机恢复' if 'Camera outside map:' in reason else '未检出')
            entry['reason'] = reason
            print('%-20s %s: %s' % (fixture, entry['status'], reason))
            results.append(entry)
            continue
        sx, sy = det['shape']
        rows = ir_rows(info['chapter'])
        if rows is None:
            entry['status'] = '缺 IR'
            print('%-20s 缺 IR %s' % (fixture, info['chapter']))
            results.append(entry)
            continue
        H, W = len(rows), max(len(r) for r in rows)
        win_w, win_h = sx + 1, sy + 1
        cands = candidate_offsets(det.get('grid_flags') or {}, rows, win_w, win_h)
        if win_w == W and win_h == H:
            verdict = '完整可见（唯一候选应为 (0,0)）'
        elif cands:
            verdict = '窗口情形（%d 个候选，与 IR 不矛盾）' % len(cands)
        else:
            verdict = '**与 IR 矛盾**（无候选偏移）'
        entry.update({'window': [win_w, win_h], 'map': [W, H],
                      'candidates': cands, 'verdict': verdict})
        print('%-20s 窗口 %dx%d 地图 %dx%d 候选=%d %s'
              % (fixture, win_w, win_h, W, H, len(cands), verdict))
        results.append(entry)

    out = os.path.join(ROOT, 'data', 'map_alignment.json')
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(results, f, ensure_ascii=False, indent=2)
    bad = [r for r in results if r.get('verdict', '').startswith('**')
           or r.get('status') in ('未检出', '缺 IR')]
    recovery = sum(r.get('status') == '待上游相机恢复' for r in results)
    print('偏移对齐：%d 张检查，%d 张失败，%d 张等待上游相机恢复验证'
          % (len(results), len(bad), recovery))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
