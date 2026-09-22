# -*- coding: utf-8 -*-
"""换号/新号后的**基线探针**：这个账号能进哪些章、哪些关。

为什么要单独做：账号一换，之前所有"哪些图能跑、清到什么进度、用哪支舰队"的结论全部作废，
而 `stage_progress.py` 只能逐关读进度、`s3_preflight.py` 是逐章静态检查 —— 都回答不了
"这个号的可达范围是什么"。本脚本用**上游自己的入口 OCR** 逐章问一遍，一张表说清。

    # 前提：游戏已登录，能停在战役页（脚本会自己逐章翻页）
    python tools/diagnostics/account_probe.py --chapters 1-16

输出：每章一行，`stages` 是该章**入口表里认出来的关卡名**（认不出 = 这一章进不去/没解锁）。
注意这**会驱动游戏翻页**（`campaign_ensure_chapter` 点上一章/下一章），是只读之外唯一的动作。
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..')))
import alas_vision as av          # noqa: E402


def op(_n, **a):
    r = json.loads(av.handle_line(json.dumps({'id': 1, 'op': _n, 'args': a})))
    if not r.get('ok'):
        raise RuntimeError(f'{_n}: {r.get("error")}')
    return r['result']


def call(name, args=None, allow=True):
    kw = {'name': name, 'allow_actions': bool(allow)}
    if args:
        kw['args'] = args
    return op('s3_campaign_call', **kw)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--chapters', default='1-16', help='形如 1-16 或 1,3,7')
    p.add_argument('--serial', default=os.environ.get('SERIAL', '127.0.0.1:16384'))
    p.add_argument('--fleet1', type=int, default=3)
    p.add_argument('--fleet2', type=int, default=6)
    args = p.parse_args()

    if '-' in args.chapters:
        a, b = args.chapters.split('-')
        chapters = list(range(int(a), int(b) + 1))
    else:
        chapters = [int(x) for x in args.chapters.split(',')]

    # 章节所在的战役页要求 `Campaign_Name` 有值；随便给一个已存在的关卡名即可
    print('init:', json.dumps(op('s3_campaign_init',
                                 chapter='campaign.campaign_main.campaign_2_1',
                                 serial=args.serial, fleet1=args.fleet1,
                                 fleet2=args.fleet2), ensure_ascii=False)[:150], flush=True)
    inst = av._CAMPAIGN['obj']

    rows = []
    for ch in chapters:
        r = call('campaign_ensure_chapter', [ch])
        err = r.get('error')
        stages = []
        if not err:
            try:
                stages = sorted(inst.stage_entrance.keys(),
                                key=lambda s: (len(s), s))
            except Exception as e:
                err = f'读 stage_entrance 失败: {type(e).__name__}: {e}'
        rows.append({'chapter': ch, 'stages': stages,
                     'error': (str(err)[:80] if err else None)})
        print(f'  第 {ch:2d} 章: ' + (f'{len(stages)} 关 {stages}' if stages
                                      else f'FAIL {err}'), flush=True)

    ok = [r for r in rows if r['stages']]
    print(f'\n可达章节 {len(ok)}/{len(rows)}；'
          f'关卡合计 {sum(len(r["stages"]) for r in ok)}', flush=True)
    out = os.path.join(os.path.dirname(HERE), '..', 'data', 'account_probe.json')
    out = os.path.normpath(out)
    try:
        with open(out, 'w', encoding='utf-8') as f:
            json.dump({'chapters': rows, 'fleet1': args.fleet1,
                       'fleet2': args.fleet2}, f, ensure_ascii=False, indent=1)
        print('written', out, flush=True)
    except Exception as e:
        print('写盘失败:', e, flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())

