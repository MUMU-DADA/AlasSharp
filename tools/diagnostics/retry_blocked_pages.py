# -*- coding: utf-8 -*-
"""定向重试：此前因外部条件阻塞、换账号后可能已解锁的页面。

与 regress_pages.py 的区别：那个跑的是"已确认可达"的集合（回归），
这个跑的是"此前不可达"的集合（探索）。判定同样严格：
队列 `navigate` 任务成功 **且** 随后 page_current 包含该页，才算真机命中。

命中的页面会写回 docs/page-verification.json（唯一真值来源），
随后用 report_pages.py / status.py 重新生成文档即可。
未命中的页面连同实测证据（落在哪一页、按钮分多少）一起打印，便于判断是
"功能仍未解锁" 还是 "客户端 UI 变了"。
"""
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import alas_vision as av          # noqa: E402
import adb_util                   # noqa: E402
from queue_navigation import run_navigation  # noqa: E402

ADB = os.environ['STUB_ADB']
SERIAL = os.environ.get('SERIAL', '127.0.0.1:16384')
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')
ALASHUB = os.environ.get('ALASHUB', os.path.join(
    HERE, '..', 'src', 'Alas.DataTool', 'bin', 'Release', 'net10.0', 'alashub.exe'))
PROGRESS = os.path.join(HERE, '..', 'docs', 'page-verification.json')

# 岛屿相关按要求跳过（page_island 与 9 个 island 子页都不在列表里）
DEFAULT_TARGETS = [
    'page_guild', 'page_meowfficer', 'page_os', 'page_private_quarters',
    'page_event_list', 'page_mail',
    'page_event', 'page_raid', 'page_sp', 'page_hospital', 'page_coalition',
    'page_rpg_stage', 'page_rpg_story',
]


def op(op_name, **args):
    resp = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': op_name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(resp.get('error'))
    return resp['result']


def shot(path=PROBE):
    adb_util.screencap(path, SERIAL)
    op('screenshot_load', path=path)
    return op('page_current')['hit']


def goto(page):
    r = run_navigation(ALASHUB, page, SERIAL, adb=ADB)
    hops = [l.strip() for l in (r.stdout or '').splitlines() if l.startswith('[hop')]
    failure = [l.strip() for l in (r.stdout or '').splitlines() if l.startswith('[failure')]
    path = [l.strip() for l in (r.stdout or '').splitlines() if l.startswith('[path')]
    return r.returncode == 0, hops, failure, path


def main():
    targets = (os.environ.get('TARGETS') or '').split(',') if os.environ.get('TARGETS') \
        else DEFAULT_TARGETS
    targets = [t.strip() for t in targets if t.strip()]
    print('=== 定向重试 %d 个此前阻塞的页面 ===' % len(targets))
    if not adb_util.ensure(SERIAL):
        print('adb 未就绪')
        return 2

    with open(PROGRESS, encoding='utf-8') as f:
        prog = json.load(f)
    verified = prog.setdefault('verified', {})
    blocked = prog.setdefault('blocked', {})

    results = []
    for i, page in enumerate(targets, 1):
        t0 = time.time()
        ok, hops, failure, path = goto(page)
        pages = shot()
        arrived = page in pages
        verdict = 'ok' if (ok and arrived) else ('goto-failed' if not ok else 'not-detected')
        print('[%2d/%d] %-22s %-13s %5.1fs  arrived=%s'
              % (i, len(targets), page, verdict, time.time() - t0, pages))
        for line in path + hops[:4] + failure:
            print('        %s' % line)
        results.append({'page': page, 'verdict': verdict, 'hops': hops,
                        'failure': failure, 'observed': pages,
                        'seconds': round(time.time() - t0, 1)})
        if verdict == 'ok':
            verified[page] = pages
            blocked.pop(page, None)
        else:
            # 只记"为什么没到"，不覆盖已有的具体原因（原因多半是人工判定的）
            blocked.setdefault(page, '定向重试仍未到达：落在 %s（%s）' % (pages, verdict))

    ok_n = sum(1 for r in results if r['verdict'] == 'ok')
    print()
    print('结果: %d/%d 到达' % (ok_n, len(results)))
    new = [r['page'] for r in results if r['verdict'] == 'ok']
    if new:
        print('新解锁: %s' % ', '.join(new))
        with open(PROGRESS, 'w', encoding='utf-8') as f:
            json.dump(prog, f, ensure_ascii=False, indent=2, sort_keys=True, default=str)
        print('已写回 %s' % os.path.abspath(PROGRESS))
    out = os.path.join(HERE, '..', 'data', 'retry_blocked.json')
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(results, f, ensure_ascii=False, indent=2, default=str)
    print('明细: %s' % os.path.abspath(out))
    return 0


if __name__ == '__main__':
    sys.exit(main())
