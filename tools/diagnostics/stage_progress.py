# -*- coding: utf-8 -*-
"""读一张关卡的**游戏自带进度**：威胁排除百分比 + 三个星级条件。

为什么需要它：我此前用过两个"清图判据"都被实测证伪 —— `campaign_end=True`
（`execute_a_battle` 走 `withdraw()` 时同样为真）和我自己数的 `enemies_left`。
真正可信的只有**游戏自己写在关卡信息面板上的字**：`威胁排除: 100%` 与三颗星。
上游 `map_get_info()` 就是读它们（`get_map_clear_percentage()` + `MAP_STAR_1/2/3`），
本脚本只负责把面板点出来、让上游读、再把结果打出来。

    # 先归位到章节页（可选；已经在 page_campaign 就加 --no-goto）
    python tools/diagnostics/stage_progress.py --chapter campaign.campaign_main.campaign_11_1

输出里 `map_progress` 就是判据：
    map_clear_pct / map_clear_percentage  游戏面板上的"威胁排除"（1.0 = 100%）
    map_achieved_star_1/2/3               三颗星是否点亮
    map_is_100_percent_clear / map_is_3_stars / map_is_threat_safe

注意：**读面板不改游戏状态**（点开节点 → 读 → 返回键关闭），不会出击。
"""
import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..')))
import alas_vision as av          # noqa: E402
from queue_navigation import run_navigation  # noqa: E402

ALASHUB = os.environ.get('ALASHUB', os.path.normpath(os.path.join(
    HERE, '..', '..', 'src', 'Alas.DataTool', 'bin', 'Release', 'net8.0', 'alashub.exe')))


def op(op_name, **args):
    resp = json.loads(av.handle_line(json.dumps({'id': 1, 'op': op_name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(f'{op_name}: {resp.get("error")}')
    return resp['result']


def call(name, args=None, allow=True, store=None, timeout_note=''):
    kw = {'name': name, 'allow_actions': bool(allow)}
    if args:
        kw['args'] = args
    if store:
        kw['store'] = store
    r = op('s3_campaign_call', **kw)
    print(f'  call {name}{timeout_note}: error={r.get("error")} '
          f'completed={r.get("completed")} ms={r.get("ms")}', flush=True)
    return r


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--chapter', default='campaign.campaign_main.campaign_11_1')
    p.add_argument('--stage', default=None, help='默认从 chapter 名推出来（campaign_11_1 -> 11-1）')
    p.add_argument('--serial', default=os.environ.get('SERIAL', '127.0.0.1:16384'))
    p.add_argument('--no-goto', action='store_true', help='不先运行导航队列任务到 page_campaign')
    p.add_argument('--fleet1', type=int, default=3)
    p.add_argument('--fleet2', type=int, default=6)
    args = p.parse_args()

    stage = args.stage
    if not stage:
        stem = args.chapter.split('.')[-1]          # campaign_11_1
        parts = stem.split('_')
        stage = f'{parts[-2]}-{parts[-1]}' if len(parts) >= 3 else stem

    if not args.no_goto:
        print('=== navigate page_campaign ===', flush=True)
        # **不用管道抓输出**：本机沙箱下"管道式 stdio"会以 EPERM 失败（实测）；
        # 这里只需要它的副作用（把游戏开到章节页），所以输出直接丢给 DEVNULL。
        try:
            r = run_navigation(ALASHUB, 'page_campaign', args.serial,
                               timeout=180, capture_output=False)
            print(f'  exit={r.returncode}', flush=True)
            if r.returncode != 0:
                print('  导航未完成；不在未知页面继续读取关卡进度', flush=True)
                return r.returncode
        except OSError as e:
            # 沙箱下 python 起子进程可能直接 EPERM（实测）。此时不是"逻辑错"，
            # 而是"本进程不许 spawn" —— 让调用方先跑导航队列任务。
            print(f'  !! 起不了子进程（{type(e).__name__}: {e}）；'
                  '请先自行运行导航队列任务到 page_campaign，或加 --no-goto', flush=True)
            return 2

    print('=== init ===', flush=True)
    print(' ', json.dumps(op('s3_campaign_init', chapter=args.chapter, serial=args.serial,
                             fleet1=args.fleet1, fleet2=args.fleet2),
                         ensure_ascii=False)[:220], flush=True)

    chapter_no = int(stage.split('-')[0])

    # **先清掉可能开着的关卡面板**：上一次读完之后游戏可能停在"面板打开"的状态，
    # 这时章节页的入口读不出来（实测：`campaign_ensure_chapter` 报 CampaignNameError、
    # `campaign_get_entrance` 跟着报 Stage not found ✗）。判据用上游自己的素材
    # （面板上有"立即前往"按钮），开着才按返回键 —— 在纯章节页盲按返回会退到章节选择页。
    try:
        op('device_capture_set')
        if op('appear_on', asset='map/MAP_PREPARATION', threshold=10).get('appear'):
            print('  检测到关卡面板开着 -> 先按返回键关掉', flush=True)
            op('device_back')
            time.sleep(1.5)
    except Exception as e:
        print(f'  （面板预检跳过: {type(e).__name__}: {e}）', flush=True)

    call('campaign_ensure_chapter', [chapter_no])
    # 取入口按钮 → 存回实例属性；再让上游自己点它（`@` 形式参数解析成实例属性）
    r = call('campaign_get_entrance', [stage], store='_probe_entrance')
    if r.get('error'):
        print('取不到入口，退出', flush=True)
        return 1
    call('device.click', ['@_probe_entrance'], timeout_note='（点开关卡节点）')

    # 面板出现 + "威胁排除"读条动画结束：用上游自己的 `handle_map_preparation()`。
    # **每次轮询前必须重新截图** —— 上游的 `appear()` 判的是 `self.device.image`，
    # 而这个方法自己不会截图（它本来是被 `enter_map` 的 while 循环包着调用的）。
    # 少了这一步，读到的永远是点击前那一帧 → `Map_info 0%`、三颗星全 false（实测踩过 ✗）。
    prep = None
    for i in range(12):
        time.sleep(1.0)
        op('s3_campaign_call', name='device.screenshot', allow_actions=False)
        rr = op('s3_campaign_call', name='handle_map_preparation', allow_actions=True)
        prep = rr.get('value')
        if prep:
            print(f'  面板就绪（第 {i + 1} 次轮询，{i + 1}s）: {str(prep)[:80]}', flush=True)
            break
    else:
        print('!! 面板一直没就绪（handle_map_preparation 始终返回 None）', flush=True)

    call('map_get_info')

    info = op('s3_campaign_info')
    print('=== 关卡进度（游戏自带）===', flush=True)
    print(json.dumps(info.get('map_progress'), ensure_ascii=False, indent=1), flush=True)

    # 关面板：走设备层的返回键（`device_back` op）。
    # 别用 `s3_campaign_call` 调 `device.back` —— 上游的 `Device` 没有 `back()` 方法，
    # 返回键在设备**方法层**（`device_back` op 里调的 `dev.back()` 才是对的）。
    print('  device_back:', json.dumps(op('device_back'), ensure_ascii=False), flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
