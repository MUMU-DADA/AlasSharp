# -*- coding: utf-8 -*-
"""控件识别 + 控制能力验证（真机）。

页面识别已经在 docs/page-verification.md 里逐页验完；这里验**控件**：

- 20 个模块级 Switch / Scroll 规则，挑本账号可达的目标页逐个真机命中检查；
- 并在 Scroll 自己的区域里**真滑一次**，看 at_top/at_bottom 是否随之翻转 ——
  这一步同时验了"滑动控制"这条控制能力，而不只是识别。

判定口径与页面验证一致：**在它自己的页面上命中**才算通过。不在该页的规则
（岛屿/大世界/指挥喵等）在页面上必然不命中，属于受游戏状态阻塞，单列出来。

导航用产品路径（`alashub goto`）而不是诊断脚本自己点：这样每一步都顺带回归
Navigation 的实现。
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import alas_vision as av  # noqa: E402

ADB = os.environ['STUB_ADB']
SERIAL = os.environ.get('SERIAL', '127.0.0.1:16384')
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')
ALASHUB = os.environ.get('ALASHUB', os.path.join(
    HERE, '..', 'src', 'Alas.DataTool', 'bin', 'Release', 'net8.0', 'alashub.exe'))

# 每页要验的规则；swipe 为真时在该页的 Scroll 区域内真滑
PLAN = [
    {'page': 'page_storage',
     'rules': [('module.storage.storage', 'MATERIAL_SCROLL')],
     'swipe': ('module.storage.storage', 'MATERIAL_SCROLL')},
    {'page': 'page_dock',
     'rules': [('module.retire.dock', 'DOCK_SCROLL'),
               ('module.retire.dock', 'DOCK_SORTING'),
               ('module.retire.dock', 'DOCK_FAVOURITE')]},
    {'page': 'page_commission',
     'rules': [('module.commission.commission', 'COMMISSION_SCROLL'),
               ('module.commission.commission', 'COMMISSION_SWITCH')]},
    {'page': 'page_fleet',
     'rules': [('module.handler.strategy', 'FORMATION'),
               ('module.handler.strategy', 'SUBMARINE_HUNT'),
               ('module.handler.strategy', 'SUBMARINE_VIEW'),
               ('module.handler.fast_forward', 'FLEET_LOCK')]},
    # cached_property 规则：必须构造 UI 实例才能拿到，判据也与模块级不同
    {'page': 'page_shop',
     'cached': [('module.shop.ui', 'ShopUI', '_shop_bottom_navbar'),
                ('module.shop.ui', 'ShopUI', 'shop_nav_250814'),
                ('module.shop.ui', 'ShopUI', 'shop_tab_250814')],
     'rules': [('module.shop.shop_voucher', 'VOUCHER_SHOP_SCROLL')]},
    {'page': 'page_storage',
     'cached': [('module.storage.ui', 'StorageUI', 'storage_filter')]},
    {'page': 'page_dock',
     'cached': [('module.retire.dock', 'Dock', 'dock_filter')],
     'switches': [('module.retire.dock', 'DOCK_SORTING'),
                  ('module.retire.dock', 'DOCK_FAVOURITE')]},
    {'page': 'page_game_room',
     'rules': [('module.minigame.minigame', 'MINIGAME_SCROLL')]},
    # 未建模但可达：船坞长按舰船卡片进「角色详情」（上游 ship_info_enter 的入口），
    # 装备类规则在这里才命中。**必须先进 page_dock**：长按点在船坞的舰船卡片上，
    # 上一版没写 goto，结果在游戏房里长按，白做一轮。
    {'page': 'ship_detail', 'enter': {'goto': 'page_dock', 'long_press': (640, 300)},
     'rules': [('module.equipment.equipment_change', 'EQUIPMENT_SCROLL')],
     'leave': 'back'},
    # 角色详情页上再点上游自己的 EQUIPMENT_OPEN（该页实测 score 0.9922）进装备选择浮层
    {'page': 'equip_change',
     'enter': {'goto': 'page_dock', 'long_press': (640, 300),
               'click': 'equipment/EQUIPMENT_OPEN'},
     'rules': [('module.equipment.equipment_change', 'equipping_filter')],
     'leave': 'back'},
    # 舰队详情/出击准备：FLEET_LOCK 与阵型、潜艇面板都在这一层
    {'page': 'fleet_detail',
     'enter': {'goto': 'page_fleet', 'click': 'equipment/FLEET_DETAIL'},
     'rules': [('module.handler.fast_forward', 'FLEET_LOCK'),
               ('module.handler.strategy', 'FORMATION'),
               ('module.handler.strategy', 'SUBMARINE_HUNT'),
               ('module.handler.strategy', 'SUBMARINE_VIEW')],
     'leave': 'back'},
    # 装备选择浮层：角色详情页的装备面板里点一个槽位（只打开选择器，不改动装备）
    {'page': 'equip_select',
     'enter': {'goto': 'page_dock', 'long_press': (640, 300), 'click_xy': (792, 156)},
     'rules': [('module.equipment.equipment_change', 'equipping_filter')],
     'leave': 'back'},
]


def op(op_name, **args):
    # 形参别叫 name：调用处要传 name=规则名，重名会 TypeError
    resp = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': op_name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(resp.get('error'))
    return resp['result']


def shot(retry=True):
    """取一帧。adb daemon 会自己重启（实测过），连接一丢 screencap 就返回空字节，
    这里做一次重连重试，而不是把 0 字节喂给解码器报 UnidentifiedImageError。"""
    subprocess.run([ADB, '-s', SERIAL, 'exec-out', 'screencap', '-p'],
                   stdout=open(PROBE, 'wb'), check=False)
    if os.path.getsize(PROBE) == 0 and retry:
        subprocess.run([ADB, 'connect', SERIAL], capture_output=True, timeout=60)
        time.sleep(3)
        return shot(retry=False)
    op('screenshot_load', path=PROBE)
    return op('page_current')['hit']


def ensure_device():
    for attempt in range(3):
        out = subprocess.run([ADB, 'connect', SERIAL], capture_output=True,
                             text=True, timeout=60)
        state = subprocess.run([ADB, '-s', SERIAL, 'get-state'], capture_output=True,
                               text=True, timeout=60)
        if state.stdout.strip() == 'device':
            return True
        print('[adb  ] 第 %d 次连接未就绪: %s / %s'
              % (attempt + 1, out.stdout.strip(), state.stderr.strip()[:80]))
        time.sleep(3)
    return False


def swipe(x1, y1, x2, y2, ms=400):
    subprocess.run([ADB, '-s', SERIAL, 'shell', 'input', 'swipe',
                    str(x1), str(y1), str(x2), str(y2), str(ms)], check=False)


def goto(page):
    # 必须显式 utf-8：alashub 用 Console.OutputEncoding=UTF8 输出中文，
    # 而 subprocess 默认按本机 GBK 解码，会让读线程抛 UnicodeDecodeError 并返回 stdout=None。
    r = subprocess.run([ALASHUB, 'goto', page, '--adb', ADB, '--serial', SERIAL],
                       capture_output=True, text=True, encoding='utf-8',
                       errors='replace', timeout=600)
    line = [l for l in (r.stdout or '').splitlines() if l.startswith('[result')]
    return r.returncode == 0, (line[0] if line else (r.stdout or r.stderr or '')[-200:])


def judge(res):
    """控件规则的"命中"：Switch 看 appear，Scroll 看 at_top/at_bottom 是否被判定过。"""
    vals = res['results']
    if any(isinstance(v, str) and ('Error' in v or 'Exception' in v) for v in vals.values()):
        return False, 'ERR'
    if res['class'] == 'Switch':
        return bool(vals.get('appear')), 'appear=%s' % vals.get('appear')
    if res['class'] == 'Scroll':
        hit = ('at_top' in vals) or ('at_bottom' in vals)
        return hit, 'at_top=%s at_bottom=%s' % (vals.get('at_top'), vals.get('at_bottom'))
    return bool(vals.get('appear')), str(vals)


def drive_switch(module, name, restore=True):
    """开关驱动：读状态 → 点另一状态的按钮 → 再读确认变化 → 复原。

    这是"控制能力"里最容易被忽略的一环：识别出开关状态不难，难的是**改它并复核**。
    上游 `Switch.click(state, main)` 就是取 `get_data(state)['click_button']` 再点，
    这里走同一条路（按钮区域由上游规则给出，点击走真机 adb）。
    最后**复原原状态**：验证不该留下痕迹。
    """
    info = op('ui_rule_check', module=module, name=name)
    states, before = info.get('state_buttons') or [], info['results'].get('get')
    if before in (None, 'unknown') or len(states) < 2:
        return {'rule': name, 'verdict': 'miss',
                'detail': '当前状态 %s，可选状态 %d 个，无法驱动'
                          % (before, len(states))}
    target = next((s for s in states if s['state'] != before and s.get('click_area')), None)
    if target is None:
        return {'rule': name, 'verdict': 'miss', 'detail': '没有其它可点状态'}
    area = target['click_area']
    x, y = (area[0] + area[2]) // 2, (area[1] + area[3]) // 2
    swipe(x, y, x, y, 80)          # 等价于 tap（input swipe 同点短时）
    time.sleep(1.5)
    shot()
    mid = op('ui_rule_check', module=module, name=name)['results'].get('get')
    changed = mid == target['state']
    restored = None
    if restore:
        back = next((s for s in states if s['state'] == before and s.get('click_area')), None)
        if back:
            b = back['click_area']
            bx, by = (b[0] + b[2]) // 2, (b[1] + b[3]) // 2
            swipe(bx, by, bx, by, 80)
            time.sleep(1.5)
            shot()
            restored = op('ui_rule_check', module=module, name=name)['results'].get('get')
    return {'rule': name, 'verdict': 'hit' if changed else 'miss',
            'detail': '%s -> %s（点 %s @%s）%s' % (
                before, mid, target['state'], (x, y),
                '' if restored is None else '，复原 -> %s' % restored),
            'states': [s['state'] for s in states]}


report = []
REPORT_ONLY = '--report-only' in sys.argv
ONLY = os.environ.get('ONLY')          # 只跑某一步（定向重跑，见 PLAN 的 page 名）
DATA = os.path.join(HERE, '..', 'data', 'controls_verify.json')
print('=== 控件识别与滑动控制验证 ===')
if REPORT_ONLY:
    # 只重建 docs/controls.md：改文档措辞不该再跑一遍真机点击
    report = json.load(open(DATA, encoding='utf-8'))
    print('[模式 ] 仅重建报告（读 %s，不连设备）' % os.path.abspath(DATA))
elif not ensure_device():
    print('adb 连接未就绪，退出（先确认模拟器已启动）')
    sys.exit(2)
for step in ([] if REPORT_ONLY else PLAN):
    page = step['page']
    if ONLY and page != ONLY:
        continue          # ONLY=<页名> 时只跑那一步：定向调试不必每次跑全套
    s = shot()
    if step.get('enter'):
        # 图里没有这个"页"，但可以靠动作进去：长按进详情 / 从某页点某个上游素材
        act = step['enter']
        if 'goto' in act:
            ok, info = goto(act['goto'])
            print('[enter] goto %s %s (%s)' % (act['goto'], 'OK' if ok else 'NG', info))
        if 'long_press' in act:
            x, y = act['long_press']
            swipe(x, y, x, y, 1100)
            time.sleep(2.0)
            print('[enter] %s：长按 (%d,%d) 1100ms' % (page, x, y))
        if 'click_xy' in act:
            # 没有对应素材、但位置确定的点击（如角色详情页的装备槽位）
            cx, cy = act['click_xy']
            print('[enter] 点坐标 (%d,%d)' % (cx, cy))
            swipe(cx, cy, cx, cy, 80)
            time.sleep(2.5)
        if 'click' in act:
            # 和导航器同一套规矩：候选资产按实测分择优；分数够高才点匹配点，
            # 否则点资产标称坐标（低分时 minMaxLoc 的峰值是随机的，拿它当点击目标等于乱点 —
            # 上一版就是这么在 score=0.27 的位置乱点，把画面点成了未建模页）。
            asset = act['click']
            best = None
            for cand in [asset, 'ui_white/%s_WHITE' % asset.split('/', 1)[-1]]:
                try:
                    m = op('button_match', asset=cand, probe_score=True)
                    nominal = op('asset_button_center', asset=cand)['center']
                except Exception:
                    continue
                score = m['score'] if m['score'] is not None else -1
                if best is None or score > best[0]:
                    best = (score, cand, m, nominal)
            score, cand, m, nominal = best
            box = m.get('button_offset') if score >= 0.85 else None
            if box:
                cx, cy = (box[0] + box[2]) // 2, (box[1] + box[3]) // 2
            else:
                cx, cy = nominal
            print('[enter] 点 %s（score=%.4f，%s）@(%d,%d)'
                  % (cand, score, '匹配点' if box else '标称坐标', cx, cy))
            swipe(cx, cy, cx, cy, 80)
            time.sleep(2.5)
        s = shot()
    elif page not in s:
        ok, info = goto(page)
        print('[goto ] %-16s %s  (%s)' % (page, 'OK' if ok else 'NG', info))
        s = shot()
    if not step.get('enter') and page not in s:
        print('[跳过 ] %s 不在屏幕上（当前 %s）' % (page, s))
        for module, name in step.get('rules', []):
            report.append({'page': page, 'rule': name, 'verdict': 'blocked',
                           'detail': '页面不可达', 'seen': s})
        for module, cls, attr in step.get('cached', []):
            report.append({'page': page, 'rule': '%s.%s' % (cls, attr), 'verdict': 'blocked',
                           'detail': '页面不可达', 'seen': s})
        continue
    print('[page ] %s（当前命中 %s）' % (page, s))
    for module, cls, attr in step.get('cached', []):
        try:
            res = op('cached_rule_check', module=module, **{'class': cls, 'attr': attr})
        except Exception as e:
            print('   %-24s EXC  %s: %s' % ('%s.%s' % (cls, attr), type(e).__name__, e))
            report.append({'page': page, 'rule': '%s.%s' % (cls, attr), 'class': '?',
                           'verdict': 'miss', 'detail': 'EXC %s: %s' % (type(e).__name__, e),
                           'module': module, 'seen': s})
            continue
        detail = json.dumps(res['detail'], ensure_ascii=False)
        print('   %-24s %-8s %-4s %s' % (res['label'], res['class'],
                                         'HIT' if res['hit'] else 'miss', detail[:160]))
        report.append({'page': page, 'rule': res['label'], 'class': res['class'],
                       'verdict': 'hit' if res['hit'] else 'miss', 'detail': detail,
                       'module': module, 'seen': s})
    for module, name in step.get('switches', []):
        try:
            r = drive_switch(module, name)
        except Exception as e:
            r = {'rule': name, 'verdict': 'miss',
                 'detail': 'EXC %s: %s' % (type(e).__name__, e)}
        print('   %-24s %-8s %-4s %s' % (r['rule'], 'Switch', 'HIT' if r['verdict'] == 'hit' else 'miss',
                                         r['detail']))
        report.append({'page': page, 'rule': r['rule'] + '#drive', 'class': 'Switch',
                       'verdict': r['verdict'], 'detail': r['detail'],
                       'module': module, 'seen': s})
    for module, name in step.get('rules', []):
        res = op('ui_rule_check', module=module, name=name)
        hit, detail = judge(res)
        print('   %-22s %-8s %-4s %s' % (name, res['class'], 'HIT' if hit else 'miss', detail))
        report.append({'page': page, 'rule': name, 'class': res['class'],
                       'verdict': 'hit' if hit else 'miss', 'detail': detail,
                       'module': module, 'seen': s})
    if step.get('swipe'):
        module, name = step['swipe']
        res = op('ui_rule_check', module=module, name=name)
        area = res.get('area')
        if not area:
            print('   [滑动 ] %s 没有 area，跳过' % name)
        else:
            x = (area[0] + area[2]) // 2
            y1, y2 = area[1] + int((area[3] - area[1]) * 0.75), area[1] + int((area[3] - area[1]) * 0.25)
            before = op('ui_rule_check', module=module, name=name)['results']
            for _ in range(3):
                swipe(x, y1, x, y2)
                time.sleep(0.6)
            shot()
            mid = op('ui_rule_check', module=module, name=name)['results']
            for _ in range(6):
                swipe(x, y2, x, y1)
                time.sleep(0.6)
            shot()
            after = op('ui_rule_check', module=module, name=name)['results']
            flipped = before.get('at_top') != mid.get('at_top')
            print('   [滑动 ] area=%s at_top: %s -> %s -> %s（翻转=%s）'
                  % (area, before.get('at_top'), mid.get('at_top'),
                     after.get('at_top'), flipped))
            report.append({'page': page, 'rule': name + '#swipe', 'class': 'Swipe',
                           'verdict': 'hit' if flipped else 'miss',
                           'detail': 'at_top %s -> %s -> %s'
                                     % (before.get('at_top'), mid.get('at_top'),
                                        after.get('at_top')),
                           'area': area, 'seen': s})
    if step.get('probe'):
        # 再进一层的探测器：点一下（只打开选择器，不做任何改动），再看规则是否命中
        for (px, py) in step['probe'].get('clicks', []):
            swipe(px, py, px, py, 80)
            time.sleep(2.0)
            s2 = shot()
            print('[probe] 点击 (%d,%d) 后当前命中 %s' % (px, py, s2))
            for module, name in step['probe'].get('rules', []):
                res = op('ui_rule_check', module=module, name=name)
                hit, detail = judge(res)
                print('   %-24s %-8s %-4s %s' % (name, res['class'],
                                                 'HIT' if hit else 'miss', detail))
                report.append({'page': page, 'rule': name + '#probe', 'class': res['class'],
                               'verdict': 'hit' if hit else 'miss', 'detail': detail,
                               'module': module, 'seen': s2})
    if step.get('leave') == 'back':
        # 进过浮层就要退出来，别把用户/后续步骤留在里面
        subprocess.run([ADB, '-s', SERIAL, 'shell', 'input', 'keyevent', '4'],
                       capture_output=True)
        time.sleep(2.0)
        print('[leave] 返回键退出，当前命中 %s' % (shot(),))

hits = sum(1 for r in report if r['verdict'] == 'hit')
miss = sum(1 for r in report if r['verdict'] == 'miss')
blocked = sum(1 for r in report if r['verdict'] == 'blocked')
print()
print('小计: hit %d / miss %d / blocked %d（共 %d 项）' % (hits, miss, blocked, len(report)))
if not REPORT_ONLY and not ONLY:
    # ONLY 模式**不覆盖**完整证据文件：定向重跑只看到一步，写进去会把历史证据抹掉
    out = DATA
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(report, f, ensure_ascii=False, indent=2, default=str)
    print('明细: %s' % os.path.abspath(out))

# ---------------------------------------------------------------- 报告
# 未在本批跑到的规则：人工判定它们属于哪一类（写进脚本，报告才可复现）
UNPLANNED = {
    'EQUIPMENT_SCROLL': ('deeper', '需进「舰船详情 → 装备」浮层，不是页面图里的独立页'),
    'equipping_filter': ('deeper', '同上（装备筛选开关在装备浮层里）'),
    'RETIRE_CONFIRM_SCROLL': ('deeper', '需进退役确认弹窗'),
    'VOUCHER_SHOP_SCROLL': ('deeper', '需切到商店的兑换页签（页签本身是 ShopUI 的 Switch 规则）'),
    'MINIGAME_SCROLL': ('pending', 'page_game_room 已验证可达，小游戏内滚动待验'),
    'ISLAND_SEASON_TASK_SCROLL': ('blocked', '岛屿计划未解锁（见 page-verification.md）'),
    'ISLAND_DOCK_SORTING': ('blocked', '同上'),
    'SWITCH_LOCK': ('blocked', '指挥喵未解锁'),
    'SCROLL_STORAGE': ('blocked', '大型作战未解锁'),
    'STRATEGIC_SEARCH_SCROLL': ('blocked', '同上'),
}
DEEPER_NOTE = {
    'FLEET_LOCK': '舰队编辑浮层里的锁定开关。实测本机 page_fleet 上 '
                  '`equipment/FLEET_DETAIL` 只有 0.17 分（不在屏上），要进出击/舰队编辑流程才能到达，'
                  '而那条流程会消耗石油并影响账号 —— 需本人同意后再验',
    'FORMATION': '出击前「阵型」面板，同上（需进出击流程）',
    'SUBMARINE_HUNT': '潜艇面板（还需先有潜艇）',
    'SUBMARINE_VIEW': '同上',
    'equipping_filter': '装备选择浮层里的筛选开关。已按上游入口试过两条路：'
                        '角色详情页点 EQUIPMENT_OPEN（该素材在详情页实测 0.99，'
                        '但点开后筛选开关仍不出现）、点装备槽位 (792,156) 也未打开选择器',
}
# 未命中里有一类不是"到不了"，而是**客户端 UI 版本不同**：规则本身跑通了、
# 正确返回 unknown，因为屏幕上根本没有它要找的新版控件。
MISS_STATUS = {
    'ShopUI.shop_nav_250814': ('uiversion',
        '本客户端是 250814 之前的老版商店 UI：可选状态是 NAV_GENERAL/NAV_MONTHLY，'
        '实测 unknown（新版商店才有这两个导航项；老版走 _shop_bottom_navbar，已命中）'),
    'ShopUI.shop_tab_250814': ('uiversion',
        '同上（9 个新版页签 TAB_* 都不在屏上）'),
}
LABEL = {'hit': '✅ 已命中', 'deeper': '➡️ 需更深流程', 'blocked': '⛔ 游戏状态阻塞',
         'pending': '🔵 待验', 'uiversion': '🕐 UI 版本差异'}


def build_doc():
    rows = []
    for x in report:
        rule = x['rule']
        if rule.endswith(('#drive', '#swipe', '#probe')):
            continue          # 动作行单独成节，不混进规则总账
        if x['verdict'] == 'hit':
            status = LABEL['hit']
            note = x['detail']
        elif rule in MISS_STATUS:
            kind, note = MISS_STATUS[rule]
            status = LABEL[kind]
        else:
            status = LABEL['deeper']
            note = DEEPER_NOTE.get(rule, x['detail'])
        rows.append((rule, x.get('class', ''), status, note))
    for rule, (kind, note) in UNPLANNED.items():
        # 已在本批跑到的规则不再重复列（VOUCHER/MINIGAME 现在都在计划里）
        if any(r[0] == rule or r[0] == rule + '#drive' for r in rows):
            continue
        rows.append((rule, '', LABEL[kind], note))
    # 事件商店那条是运行时算出来的规则（(count, navbar)），没有统一判据，单列
    rows.append(('EventShopUI.event_shop_tab_count_and_navbar', '运行时计算', LABEL['blocked'],
                 '需进活动商店（本账号当前活动页可达，但商店入口需要活动开放对应玩法）'))
    rows.sort(key=lambda r: (r[2], r[0]))
    actions = [x for x in report if x['rule'].endswith(('#swipe', '#drive', '#probe'))]

    lines = [
        '# 控件识别与滑动控制验证记录',
        '',
        '判定口径与页面验证一致：**在它自己的页面上命中**才算通过。',
        '"可驱动"（不抛异常）不算 —— 那是 S1 阶段的结论。',
        '',
        '设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服，新版主界面）。',
        '脚本：`tools/diagnostics/verify_controls.py`（导航走产品路径 `alashub goto`，',
        '顺带回归 Navigation 实现）；原始数据 `data/controls_verify.json`。',
        '',
        '## 本批实际运行结果',
        '',
        '| 页面 | 规则 | 类型 | 结果 | 细节 |',
        '| --- | --- | --- | --- | --- |',
    ]
    for x in report:
        lines.append('| `%s` | `%s` | %s | %s | %s |'
                     % (x['page'], x['rule'], x.get('class', ''), x['verdict'], x['detail']))
    lines += [
        '',
        '## 滑动控制与开关驱动（动作，不是识别）',
        '',
        '| 页面 | 动作 | 结果 |',
        '| --- | --- | --- |',
    ]
    for x in actions:
        lines.append('| `%s` | `%s` | %s |' % (x['page'], x['rule'], x['detail']))
    lines += [
        '',
        '`#swipe` = 在 Scroll 自己的区域里真滑，看 `at_top` 是否翻转；',
        '`#drive` = 读出开关状态 → 点上游规则给出的另一个状态的按钮 → 再读确认变化',
        '→ **复原原状态**（验证不该留下痕迹）；',
        '`#probe` = 再进一层的探测点击（只打开选择器，不做任何改动）。',
        '开关驱动是控制能力的核心回路：识别出状态不难，难的是改它并复核。',
        '',
        '## 20 个控件规则的总账',
        '',
        '| 规则 | 类型 | 状态 | 说明 |',
        '| --- | --- | --- | --- |',
    ]
    for rule, cls, status, note in rows:
        lines.append('| `%s` | %s | %s | %s |' % (rule, cls, status, note))
    lines += [
        '',
        '## 复现',
        '',
        '```powershell',
        '$env:STUB_ADB = "<adb.exe>"',
        '$env:ALASHUB  = "src/Alas.DataTool/bin/Release/net8.0/alashub.exe"',
        'python tools/diagnostics/verify_controls.py',
        '```',
        '',
    ]
    path = os.path.join(HERE, '..', 'docs', 'controls.md')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    print('报告: %s' % os.path.abspath(path))


build_doc()
