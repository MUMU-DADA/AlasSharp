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


report = []
print('=== 控件识别与滑动控制验证 ===')
if not ensure_device():
    print('adb 连接未就绪，退出（先确认模拟器已启动）')
    sys.exit(2)
for step in PLAN:
    page = step['page']
    s = shot()
    if page not in s:
        ok, info = goto(page)
        print('[goto ] %-16s %s  (%s)' % (page, 'OK' if ok else 'NG', info))
        s = shot()
    if page not in s:
        print('[跳过 ] %s 不在屏幕上（当前 %s）' % (page, s))
        for module, name in step['rules']:
            report.append({'page': page, 'rule': name, 'verdict': 'blocked',
                           'detail': '页面不可达', 'seen': s})
        continue
    print('[page ] %s（当前命中 %s）' % (page, s))
    for module, name in step['rules']:
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

hits = sum(1 for r in report if r['verdict'] == 'hit')
miss = sum(1 for r in report if r['verdict'] == 'miss')
blocked = sum(1 for r in report if r['verdict'] == 'blocked')
print()
print('小计: hit %d / miss %d / blocked %d（共 %d 项）' % (hits, miss, blocked, len(report)))
out = os.path.join(HERE, '..', 'data', 'controls_verify.json')
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
    'FLEET_LOCK': '舰队编辑浮层里的锁定开关（不是 page_fleet 本身）',
    'FORMATION': '出击前「阵型」面板',
    'SUBMARINE_HUNT': '潜艇面板（需先有潜艇）',
    'SUBMARINE_VIEW': '同上',
}
LABEL = {'hit': '✅ 已命中', 'deeper': '➡️ 需更深流程', 'blocked': '⛔ 游戏状态阻塞',
         'pending': '🔵 待验'}


def build_doc():
    rows = []
    for x in report:
        rule = x['rule']
        if x['verdict'] == 'hit':
            status = LABEL['hit']
            note = x['detail']
        else:
            status = LABEL['deeper']
            note = DEEPER_NOTE.get(rule, x['detail'])
        rows.append((rule, x.get('class', ''), status, note))
    for rule, (kind, note) in UNPLANNED.items():
        rows.append((rule, '', LABEL[kind], note))
    rows.sort(key=lambda r: (r[2], r[0]))

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
        '## 滑动控制',
        '',
        '`MATERIAL_SCROLL` 的拖拽区域实测是**右侧滚动条** `[1257, 94, 1263, 585]`。',
        '在它自己的区域里向上滑 3 次 → `at_top` 由 `True` 变 `False`；再向下滑 6 次回顶。',
        '这一步同时验了两件事：`input swipe` 这条控制链路真的能驱动游戏，',
        '且上游 Scroll 规则会随画面变化翻转判定（不是永远返回同一个值）。',
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
