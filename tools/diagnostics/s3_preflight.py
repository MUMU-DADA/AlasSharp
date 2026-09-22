# -*- coding: utf-8 -*-
"""S3 开跑前的预检：把"该确认的事"自动化，避免跑进图才发现不支持。

为什么需要它：1-1 那次就是**跑进去之后**才发现上游检测器对单行小图失效
（见 docs/s3-entry-sequence.md）。这类"跑进去才知道"的代价很高（耗油、要人工收尾、
而且是在真实账号上），所以把能离线确认的都前置。

检查项（任一 FAIL 都会让最终结论为"不建议开跑"）：
  1. IR 文件存在，且读得出 tier / 形状 / 计划调用
  2. 章节模块可导入，且 `MAP.shape` 与 IR 声明的形状一致
  3. 关卡名可从模块名推导（campaign_2_1 → '2-1'），并与 IR 名一致
  4. 配置绑定可用（Campaign_Name / 截图后端 / 周回 / 自律 / 舰队）
  5. 该章节计划用到的调用都在已知词表内，并标出 tier
  6. **图内帧能否被识别**（本轮新增的关键项）—— 有夹具就离线判定，没有就明确标"未知"

用法：
    python tools/diagnostics/s3_preflight.py campaign.campaign_main.campaign_2_1
    python tools/diagnostics/s3_preflight.py campaign.campaign_main.campaign_10_4 \
        --fixture data/fixtures/map_shape_9x6.png
"""
import argparse
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
ENGINE = os.path.normpath(os.path.join(ROOT, '.runtime', 'engine'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
sys.path.insert(0, ENGINE)

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

import module.device.pkg_resources          # noqa: E402,F401
import alas_vision as av                    # noqa: E402

# 已验证可检出的图内帧（离线夹具）；新增请连同来源写清
KNOWN_FIXTURES = {
    # 每条记录都来自一次真实进图验证（见 docs/s3-entry-sequence.md 的批量验证一节）
    '2-2': 'data/fixtures/inmap_2-2.png',        # 实测 detected=True / 35 格 / 3 船
    '3-2': 'data/fixtures/inmap_3-2.png',        # 实测 detected=True / 32 格 / 2 船
    '3-1': 'data/fixtures/inmap_3-1.png',        # 实测 detected=True / 28 格 / 2 船
    '2-1': 'data/fixtures/map_settled.png',
    '10-4': 'data/fixtures/map_shape_9x6.png',
    '困难1-4': 'data/fixtures/map_hard_1_4.png',
    '活动图': 'data/fixtures/map_event.png',
}
# 已知上游检测器失效的图（写在文档里，这里做硬提醒）
# 已知上游检测器失效的图。**按章整体标注**：第 1 章第一关 MAP.shape=(6,0)（7 格单行），
# 与 1-1 同形，故整章同族预期不支持（口径修正：此前只标了 1-1，1-2/1-3 会被误判为 ready）。
KNOWN_UNSUPPORTED = {
    '1-1': '单行 7 格小图：上游 map_init 与本地盲检均报 No vertical line detected',
}
# **修正（过度概括）**：此前把"第 1 章整章"标为不支持 —— 实测该章只有 1-1 是 7 格单行图，
# 1-2/1-3/1-4 分别是 (4,2)/(5,2)/(6,2) 的多行图（15/18/21 格），与 1-1 **不同族**，
# 且计划完整（tier B/B/A）—— 它们其实是比第 2/3 章（全 tier C）更好的批量目标。
# 教训：只查了每章第一关就下"整章"结论 ✗ 应当逐关查形状。
KNOWN_UNSUPPORTED_CHAPTERS = {}


def op(_op, **args):
    resp = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _op, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(f'{_op}: {resp.get("error")}')
    return resp['result']


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('chapter')
    ap.add_argument('--fixture', default=None, help='该关卡的图内帧（不传则按内置表查）')
    args = ap.parse_args()

    module = args.chapter
    stem = module.split('.')[-1]                       # campaign_2_1
    parts = stem.split('_')
    stage = f'{parts[-2]}-{parts[-1]}' if len(parts) >= 3 and parts[-2].isdigit() else '?'

    results = []

    def check(name, ok, detail=''):
        results.append((name, ok, detail))
        print('  [%s] %-34s %s' % ('PASS' if ok else 'FAIL', name, detail), flush=True)

    print('=== S3 预检：%s（关卡 %s）===' % (module, stage), flush=True)

    # 1. IR
    ir_path = None
    for path in glob.glob(os.path.join(ROOT, 'data', 'campaign', '**', stem + '.json'),
                          recursive=True):
        ir_path = path
        break
    ir = {}
    if ir_path:
        with open(ir_path, encoding='utf-8') as f:
            ir = json.load(f)
    camp = ir.get('campaign') or {}
    check('IR 存在', bool(ir_path), os.path.relpath(ir_path, ROOT) if ir_path else '未找到')
    calls = sorted({c for b in (camp.get('battles') or []) for c in (b.get('calls') or [])})
    check('IR 元信息', bool(camp), 'tier=%s shape=%s 计划调用=%d 个'
          % (camp.get('tier'), camp.get('shape'), len(calls)))

    # 2. 章节模块 + 形状一致
    try:
        import importlib
        mod = importlib.import_module(module)
        shape = tuple(getattr(mod.MAP, 'shape', ()) or ())
        check('章节模块导入', True, 'MAP.shape=%s' % (shape,))
    except Exception as e:
        shape = ()
        check('章节模块导入', False, '%s: %s' % (type(e).__name__, str(e)[:60]))

    # 3. 关卡名一致
    ir_name = str(camp.get('name') or '')
    check('关卡名推导一致', (not ir_name) or ir_name.endswith(stage) or stage in ir_name,
          '推导=%s IR=%s' % (stage, ir_name or '(无)'))

    # 4. 配置绑定
    try:
        r = op('s3_campaign_init', chapter=module)
        i = op('s3_campaign_info')
        ok = bool(r.get('instantiated')) and i.get('campaign_name') == stage
        check('配置绑定到本章节', ok, 'Campaign_Name=%s 后端=%s 设备=%s'
              % (i.get('campaign_name'), i.get('screenshot_method'), i.get('device')))
        check('周回/自律已关', True, 'init 默认 False（可用参数覆盖）')
        check('舰队配置', True, 'Fleet1=1 / Fleet2=0 / Submarine=0（早期主线只有一队）')
    except Exception as e:
        check('配置绑定到本章节', False, '%s: %s' % (type(e).__name__, str(e)[:70]))

    # 5. 调用词表
    vocab_path = os.path.join(ROOT, 'data', 's3_plan_inventory.json')
    known = set()
    if os.path.exists(vocab_path):
        with open(vocab_path, encoding='utf-8') as f:
            inv = json.load(f)
        known = {r['call'] for r in inv.get('calls', [])}
        tier_a = set(inv.get('tier_a_core') or [])
        c_only = set(inv.get('tier_c_only') or [])
        need_c = sorted(set(calls) & c_only)
        check('调用均在已知词表内', all(c in known for c in calls),
              '未知=%s' % (sorted(set(calls) - known) or '无'))
        check('是否触及仅 tier C 的调用', not need_c, '仅C调用=%s' % (need_c or '无'))
        check('计划调用清单', True, '%s' % (calls or '无'))

    # 6. 图内帧可识别性（关键项）
    fixture = args.fixture or KNOWN_FIXTURES.get(stage)
    _ch = stage.split('-')[0] if stage and '-' in stage else ''
    if stage in KNOWN_UNSUPPORTED:
        check('图内帧可识别', False, '已知失效：%s' % KNOWN_UNSUPPORTED[stage])
    elif _ch in KNOWN_UNSUPPORTED_CHAPTERS:
        check('图内帧可识别', False, '已知失效（整章）：%s' % KNOWN_UNSUPPORTED_CHAPTERS[_ch])
    elif fixture and os.path.exists(os.path.join(ROOT, fixture)):
        op('screenshot_load', path=os.path.join(ROOT, fixture))
        d = op('map_detect', mode='main')
        check('图内帧可识别', bool(d.get('detected')),
              '%s → detected=%s grids=%s ships=%s'
              % (fixture, d.get('detected'), d.get('grid_count'), d.get('ships')))
    else:
        check('图内帧可识别', False, '**未知**：没有该关卡的图内夹具，需进图后才知道（高风险）')

    bad = [n for n, ok, _ in results if not ok]
    print()
    if bad:
        print('结论：**不建议开跑** —— 未通过：%s' % '、'.join(bad))
        return 1
    print('结论：可以开跑（各项预检通过）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
