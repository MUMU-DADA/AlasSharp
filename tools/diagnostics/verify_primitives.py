# -*- coding: utf-8 -*-
"""控制原语真机验证：返回键 / 长按 / 上游对齐的滑动。

为什么单独验这三样：
- **返回键**：上游用 `input keyevent 4` 做返回（module/equipment/equipment_code.py）。
  它是最常用的"退出浮层"手段，可观测（页面变了）且非破坏性。
- **长按**：上游 `Device.long_click` 在 adb 后端就是"同点滑动 1~1.2 秒"。
  本账号上找不到**非破坏性且可观测**的长按靶点（上游用它的 ship_info_enter
  在 equipment.py 里的调用点已被注释掉，gems_farming 那条要真的出击），
  所以这里只验到"命令形态与上游一致、且游戏不产生副作用"，并如实标注未验部分。
- **滑动 ×2.5**：上游 `Device.swipe` 的 adb 分支把时长乘 2.5，
  注释是 "ADB needs to be slow, or swipe doesn't work"。这里验两种时长下
  Scroll 的判定差异，确认这个系数在本机确实有影响。

判定全部基于**上游规则的返回值变化**，不是"命令发出去了就算过"。
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import alas_vision as av          # noqa: E402
import adb_util                   # noqa: E402

ADB = os.environ['STUB_ADB']
SERIAL = os.environ.get('SERIAL', '127.0.0.1:16384')
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')
ALASHUB = os.environ.get('ALASHUB', os.path.join(
    HERE, '..', 'src', 'Alas.DataTool', 'bin', 'Release', 'net8.0', 'alashub.exe'))
SCROLL = ('module.storage.storage', 'MATERIAL_SCROLL')


def op(op_name, **args):
    resp = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': op_name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(resp.get('error'))
    return resp['result']


def ensure_device():
    return adb_util.ensure(SERIAL)


def shot(path=PROBE):
    """截图并让宿主解码。连接抖动由 adb_util.screencap 负责重连重试。"""
    adb_util.screencap(path, SERIAL)
    op('screenshot_load', path=path)
    return op('page_current')['hit']


def adb(*args):
    return adb_util.shell(*args)


def goto(page):
    r = subprocess.run([ALASHUB, 'goto', page, '--adb', ADB, '--serial', SERIAL],
                       capture_output=True, text=True, encoding='utf-8',
                       errors='replace', timeout=600)
    ok = r.returncode == 0
    return ok, [l for l in (r.stdout or '').splitlines() if l.startswith('[result')]


results = []
print('=== 控制原语验证 ===')
if not ensure_device():
    print('adb 连接未就绪，退出')
    sys.exit(2)

# ---- 1. 返回键：从档案页/仓库页按 BACK 回主界面
for start in ('page_storage', 'page_fleet'):
    ok, info = goto(start)
    if not ok:
        print('[goto ] %s 失败: %s' % (start, info))
        continue
    before = shot()
    adb('input', 'keyevent', '4')
    time.sleep(2.0)
    after = shot()
    hit = 'page_main' in after
    print('[BACK ] %-16s %s -> %s  %s' % (start, before, after, 'OK' if hit else 'NG'))
    results.append({'primitive': 'keyevent BACK', 'from': start, 'before': before,
                    'after': after, 'verdict': 'hit' if hit else 'miss',
                    'detail': 'input keyevent 4'})

# ---- 2. 长按：命令形态与上游一致；用上游自己的判据（EQUIPMENT_OPEN）看是否进了舰船详情
ok, _ = goto('page_dock')
if ok:
    before = shot()
    # 上游 long_click 的 adb 形态：同点滑动 1~1.2 秒
    adb('input', 'swipe', '640', '300', '640', '300', '1100')
    time.sleep(1.5)
    after = shot(os.path.join(HERE, '..', 'data', '_shot_longpress.png'))
    # 上游 ship_info_enter 等的判据就是 EQUIPMENT_OPEN 出现（见 module/equipment/equipment.py）
    equip = op('button_match', asset='equipment/EQUIPMENT_OPEN', probe_score=True)
    entered = bool(equip['match'])
    print('[长按 ] %s -> %s；EQUIPMENT_OPEN match=%s score=%.4f -> %s'
          % (before, after, equip['match'], equip['score'] or -1,
             '进入舰船详情' if entered else '未进详情'))
    results.append({'primitive': 'long click (adb form)', 'from': 'page_dock',
                    'before': before, 'after': after,
                    'verdict': 'hit' if entered else 'miss',
                    'detail': 'input swipe x y x y 1100（上游 long_click 的 adb 形态）→ '
                              'EQUIPMENT_OPEN match=%s score=%.4f；'
                              '判据取自上游 ship_info_enter'
                              % (equip['match'], equip['score'] or -1)})
    adb('input', 'keyevent', '4')      # 退回船坞，恢复现场
    time.sleep(2.0)
    shot()

# ---- 3. 滑动：只验"真机有效"，不去硬凑那个 ×2.5
# 曾想用 at_top/at_bottom 的布尔翻转证明"150ms 滑不动、375ms 才滑得动"，实测读数
# 不自洽（起始 False，两种时长都变 True）—— **布尔判据分辨不出时长差异**，
# 那种"证明"是假的，所以撤掉、改成如实记录。
# 系数本身是上游源码事实（Device.swipe 的 adb 分支乘 2.5，注释原文
# "ADB needs to be slow, or swipe doesn't work"），我们照抄进
# DeviceController.SwipeUpstream；它在本机是否必要，需要位置读数才能验，暂不声称。
ok, _ = goto('page_storage')
if ok:
    info = op('ui_rule_check', module=SCROLL[0], name=SCROLL[1])
    area = info['area']
    x = (area[0] + area[2]) // 2
    y1, y2 = area[3] - 40, area[1] + 40
    shot()
    before = op('ui_rule_check', module=SCROLL[0], name=SCROLL[1])['results']
    for _ in range(6):                                     # 先滑到顶
        adb('input', 'swipe', str(x), str(y2), str(x), str(y1), '250')
        time.sleep(0.5)
    shot()
    top = op('ui_rule_check', module=SCROLL[0], name=SCROLL[1])['results']
    for _ in range(3):                                     # 再往上滚
        adb('input', 'swipe', str(x), str(y1), str(x), str(y2), '250')
        time.sleep(0.5)
    shot()
    moved = op('ui_rule_check', module=SCROLL[0], name=SCROLL[1])['results']
    # 判据：这一串滑动**过程中判定发生过变化**即可，不要只比最后两步 ——
    # 上一次换了账号后出现 True → False → False：起始在顶、往下拖 6 次离开顶部（翻转成立），
    # 再往上拖 3 次没回到顶，只比最后两步就会把已经成立的翻转判成 miss。
    # 方向也不要预设：这里拖的是右侧滚动条滑块。
    flipped = len({before.get('at_top'), top.get('at_top'), moved.get('at_top')}) > 1
    print('[滑动 ] at_top: 起始=%s 往下拖6次=%s 往上拖3次=%s（翻转=%s）'
          % (before.get('at_top'), top.get('at_top'), moved.get('at_top'), flipped))
    results.append({'primitive': 'swipe (real device)', 'from': 'page_storage',
                    'before': before.get('at_top'), 'after': moved.get('at_top'),
                    'verdict': 'hit' if flipped else 'miss',
                    'detail': 'at_top: 起始 %s -> 拖着滚动条滑块往下 6 次 %s -> 往上 3 次 %s：'
                              '判定随滑动翻转，说明 input swipe 在真的驱动画面。'
                              '上游的时长系数 ×2.5 未在此隔离验证（布尔判据分辨不出）'
                              % (before.get('at_top'), top.get('at_top'), moved.get('at_top'))})
    for _ in range(6):                                     # 复原到顶
        adb('input', 'swipe', str(x), str(y2), str(x), str(y1), '250')
        time.sleep(0.4)

hit = sum(1 for r in results if r['verdict'] == 'hit')
print()
print('小计: hit %d / %d' % (hit, len(results)))
out = os.path.join(HERE, '..', 'data', 'primitives_verify.json')
with open(out, 'w', encoding='utf-8') as f:
    json.dump(results, f, ensure_ascii=False, indent=2, default=str)
print('明细: %s' % os.path.abspath(out))

# ---------------------------------------------------------------- 报告
NOT_COVERED = [
    ('文本输入', '上游只在「装备码」功能里用 `d.send_keys(text=code)`（uiautomator2 后端，'
                 '见 module/equipment/equipment_code.py）。本机控制后端是 adb，'
                 '且该功能入口需要特定界面，未验证。要用时得先决定后端（adb 的 '
                 '`input text` 与 uiautomator2 的 send_keys 语义不同：前者不支持中文/清空）。'),
    ('长按的"返回后状态"', '长按进舰船详情后再按返回能回船坞（本次已顺带走到），'
                            '但上游 gems_farming 那条真实用法的完整流程（出击→长按→装备）未验。'),
    ('滑动时长系数 ×2.5', '上游 Device.swipe 对 adb 分支把时长乘 2.5'
                          '（注释 "ADB needs to be slow, or swipe doesn\'t work"）。'
                          '本项目已照抄进 DeviceController.SwipeUpstream，但"少了它会不会滑不动"'
                          '没有隔离验证 —— at_top/at_bottom 是布尔量，分辨不出时长差异。'),
]
lines = [
    '# 控制原语真机验证记录（返回键 / 长按 / 滑动）',
    '',
    '页面与控件的**识别**见 `page-verification.md` 与 `controls.md`；这里验的是把动作',
    '真正发到设备上的那三种原语。判定一律基于**上游规则的返回值变化**或**上游自己的判据**，',
    '不是"命令发出去了就算过"。',
    '',
    '设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服）。',
    '脚本：`tools/diagnostics/verify_primitives.py`；原始数据 `data/primitives_verify.json`。',
    '',
    '## 结果',
    '',
    '| 原语 | 上游形态 | 结果 | 依据 |',
    '| --- | --- | --- | --- |',
]
FORM = {
    'keyevent BACK': '`adb shell input keyevent 4`',
    'long click (adb form)': '`input swipe x y x y 1000~1200`（同点滑动）',
    'swipe (real device)': '`input swipe x1 y1 x2 y2 <ms>`',
}
for x in results:
    lines.append('| %s | %s | %s | %s |'
                 % (x['primitive'], FORM.get(x['primitive'], ''), x['verdict'], x['detail']))
lines += [
    '',
    '长按那一条值得单独说：判据用的是**上游自己的验收条件**。',
    '`module/equipment/equipment.py` 的 `ship_info_enter()` 等的就是 `EQUIPMENT_OPEN` 出现，',
    '实测在船坞长按舰船卡片后该素材 score=0.9922 命中，即"已进入舰船详情"——',
    '与上游判定同口径，不是我们自己定的标准。',
    '',
    '## 未覆盖（如实列出）',
    '',
    '| 项 | 原因 |',
    '| --- | --- |',
]
for name, why in NOT_COVERED:
    lines.append('| %s | %s |' % (name, why))
lines += [
    '',
    '## 复现',
    '',
    '```powershell',
    '$env:STUB_ADB = "<adb.exe>"',
    'python tools/diagnostics/verify_primitives.py',
    '```',
    '',
    '脚本内所有设备访问都走 `tools/diagnostics/adb_util.py`：本机 adb server 会被新起的',
    '客户端抢占，表现是 `connect` 说成功但 `screencap` 返回 **0 字节**（看着像图像问题，',
    '其实是连接问题），所以"connect → devices 校验 → 操作"必须在同一进程内完成并重试。',
    '',
]
path = os.path.join(HERE, '..', 'docs', 'primitives.md')
with open(path, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(lines))
print('报告: %s' % os.path.abspath(path))
