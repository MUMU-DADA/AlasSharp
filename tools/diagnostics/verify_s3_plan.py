# -*- coding: utf-8 -*-
"""S3 计划读取的离线回归：协议读出的 `plan_steps` 必须与 IR 的 `battle_*` 方法一致。

为什么需要它：S3 的执行器（`s3_run_plan`）依赖两件事 ——
  ① 从 IR 里取出该章节的 `battle_*` 方法序列（**计划步骤**）；
  ② 把它交给上游执行。
本脚本只验证 ①（**离线、不碰游戏**）：对若干章节，比对
  `s3_run_plan(dry_run=True).plan_steps` 与该章节 IR 里的 `battle_*` 方法名（含顺序）。
一旦有人改动 IR 导出器、op 的排序逻辑或 dry-run 分支，这里会立刻红。

用法：`python tools/diagnostics/verify_s3_plan.py`
"""
import glob
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

import alas_vision as av          # noqa: E402

# 覆盖三种形态：tier C（计划不完整）/ tier B / tier A，以及"已实测跑通"的关卡
CASES = [
    'campaign.campaign_main.campaign_2_1',
    'campaign.campaign_main.campaign_2_2',
    'campaign.campaign_main.campaign_3_2',
    'campaign.campaign_main.campaign_1_4',
]


def op(_op, **args):
    resp = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _op, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(f'{_op}: {resp.get("error")}')
    return resp['result']


def ir_methods(stem):
    """从 IR 里取该章节的 battle_* 方法名（按方法序号排序，与执行器同序）。"""
    for path in glob.glob(os.path.join(ROOT, 'data', 'campaign', '**', stem + '.json'),
                          recursive=True):
        with io.open(path, encoding='utf-8') as f:
            ir = json.load(f)
        battles = [b['method'] for b in ((ir.get('campaign') or {}).get('battles') or [])
                   if str(b.get('method', '')).startswith('battle_')]

        def idx(name):
            m = re.search(r'(\d+)', name)
            return int(m.group(1)) if m else 9999
        return sorted(battles, key=idx)
    return None


def main():
    ok = True
    print('%-46s %-8s %s' % ('章节', 'IR', '协议侧 plan_steps'))
    for chapter in CASES:
        stem = chapter.split('.')[-1]
        expect = ir_methods(stem)
        got = op('s3_run_plan', chapter=chapter, dry_run=True).get('plan_steps')
        if expect is None:
            print('%-46s %-8s %s' % (stem, '无IR', got))
            ok = False
            continue
        same = list(got or []) == list(expect)
        ok = ok and same
        print('%-46s %-8s %-28s %s' % (stem, ','.join(expect), ','.join(got or []),
                                       'OK' if same else '**不一致**'))
    # 安全锁也必须还在：不带 allow_actions 的真跑请求应被拒绝
    refused = op('s3_run_plan', chapter=CASES[0], dry_run=False).get('refused')
    print('安全锁（真跑无授权应被拒）: %s' % ('OK' if refused else '**失效**'))
    ok = ok and bool(refused)
    print()
    print('S3 计划读取回归：%s' % ('通过' if ok else '**失败**'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
