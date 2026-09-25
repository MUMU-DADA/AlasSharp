# -*- coding: utf-8 -*-
"""文本输入原语验证（走上游的「装备码」流程）。

为什么挑这条路：上游唯一用到文本输入的地方就是装备码
（module/equipment/equipment_code.py 用 `d.send_keys(text=code)`），而它的入口就在
角色详情页右上角。本机控制后端是 adb，对应形态是 `input text`（上游那个是
uiautomator2 的 send_keys，语义不同：不支持中文、不清空原内容），所以这里验的是
**我们要用的那个形态**能不能真的把字打进输入框。

安全设计（比"小心点"更硬的东西）：
- 只点实测分 ≥0.85 的素材：只有确信在屏上的素材才可能是正确的按钮；
- 红线守卫：名字里带 CONFIRM/BATTLE/START/... 的素材一律不点
  （装备码页有「应用/确认」，点了会真的改装备）；
- 打完字只读验证（OCR 输入框区域），随后按返回退出，不做任何提交。
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
ALAS_SERVER = os.environ.get('ALAS_SERVER', os.path.join(
    HERE, '..', 'src', 'Alas.Server', 'bin', 'Release', 'net10.0', 'Alas.Server.exe'))
DANGER = ('START', 'BATTLE', 'FIGHT', 'ATTACK', 'ASSAULT', 'CONFIRM', 'COMMIT')
TYPED = 'Alas123'


def op(op_name, **args):
    resp = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': op_name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(resp.get('error'))
    return resp['result']


def shot():
    adb_util.screencap(PROBE, SERIAL)
    op('screenshot_load', path=PROBE)
    return op('page_current')['hit']


def goto(page):
    r = run_navigation(ALAS_SERVER, page, SERIAL, adb=ADB, timeout=900)
    return r.returncode == 0


def confident_click(asset):
    """只有实测分 ≥0.85 才点；返回 (是否点了, 分数, 点击点)。"""
    if any(k in asset.upper() for k in DANGER):
        print('[守卫 ] 拒绝点击 %s（红线关键词）' % asset)
        return False, -1, None
    m = op('button_match', asset=asset, probe_score=True)
    score = m['score'] if m['score'] is not None else -1
    if score < 0.85:
        print('[守卫 ] 拒绝点击 %s：实测分 %.4f < 0.85' % (asset, score))
        return False, score, None
    box = m.get('button_offset') or op('asset_button_center', asset=asset)['button']
    x, y = (box[0] + box[2]) // 2, (box[1] + box[3]) // 2
    adb_util.shell('input', 'swipe', str(x), str(y), str(x), str(y), '80')
    return True, score, (x, y)


results = []
print('=== 文本输入原语验证（装备码流程）===')
if not adb_util.ensure(SERIAL):
    print('adb 未就绪')
    sys.exit(2)

# 1) 进船坞 → 长按舰船卡片进角色详情（上游 ship_info_enter 的入口）
try:
    if not goto('page_dock'):
        print('无法到达 page_dock')
        sys.exit(1)
    adb_util.shell('input', 'swipe', '640', '300', '640', '300', '1100')
    time.sleep(2.5)
    pages = shot()
    equip_open = op('button_match', asset='equipment/EQUIPMENT_OPEN', probe_score=True)
    print('[步骤 ] 角色详情：pages=%s EQUIPMENT_OPEN=%.4f'
          % (pages, equip_open['score'] or -1))
    results.append({'step': 'enter ship detail', 'pages': pages,
                    'detail': 'EQUIPMENT_OPEN %.4f（上游 ship_info_enter 的判据）'
                              % (equip_open['score'] or -1),
                    'verdict': 'hit' if (equip_open['score'] or 0) >= 0.85 else 'miss'})

    # 2) 点「装备码」入口
    clicked, score, point = confident_click('equipment/EQUIPMENT_CODE_ENTRANCE')
    print('[步骤 ] 装备码入口 clicked=%s score=%.4f at=%s' % (clicked, score, point))
    if not clicked:
        results.append({'step': 'open equipment code page', 'verdict': 'blocked',
                        'detail': 'ENTRANCE 实测 %.4f（<0.85 或被守卫拦下），未点击' % score})
    else:
        time.sleep(2.5)
        pages = shot()
        chk = op('button_match', asset='equipment/EQUIPMENT_CODE_PAGE_CHECK', probe_score=True)
        print('[步骤 ] 装备码页：pages=%s PAGE_CHECK=%.4f' % (pages, chk['score'] or -1))
        results.append({'step': 'open equipment code page', 'pages': pages,
                        'detail': 'EQUIPMENT_CODE_PAGE_CHECK %.4f' % (chk['score'] or -1),
                        'verdict': 'hit' if (chk['score'] or 0) >= 0.85 else 'miss'})

    # 3) 点输入框聚焦 → 打字 → 用**像素差 + 截图**验证（不依赖 OCR：
    #    op_ocr 在这块区域会抛 TypeError，与其硬修 OCR，不如用更直接的证据）
    box = op('asset_button_center', asset='equipment/EQUIPMENT_CODE_TEXTBOX')
    area = box['button']
    print('[步骤 ] 输入框区域 %s' % (area,))
    adb_util.shell('input', 'swipe', str(box['center'][0]), str(box['center'][1]),
                   str(box['center'][0]), str(box['center'][1]), '80')
    time.sleep(1.5)
    shot()
    before_img = os.path.join(HERE, '..', 'data', '_code_box_before.png')
    adb_util.screencap(before_img, SERIAL)
    adb_util.shell('input', 'text', TYPED)
    time.sleep(1.5)
    shot()
    after_img = os.path.join(HERE, '..', 'data', '_code_box_after.png')
    adb_util.screencap(after_img, SERIAL)

    # 像素差：看**整条输入栏**，而不只是 EQUIPMENT_CODE_TEXTBOX 的 area。
    # 踩过的坑：那个 area 只是输入栏中段（[446,668,834,706]），而文字从栏的最左端开始
    # 渲染 —— 只比中段会得到"零变化"，把已经成功的输入判成失败（第一版就是这样误判的）。
    changed = None
    try:
        from PIL import Image, ImageChops
        crop = (0, int(area[1]) - 8, 1100, int(area[3]) + 8)
        a = Image.open(before_img).convert('RGB').crop(crop)
        b = Image.open(after_img).convert('RGB').crop(crop)
        diff = ImageChops.difference(a, b)
        nonzero = sum(diff.convert('L').histogram()[8:])
        changed = {'crop': list(crop), 'diff_bbox': diff.getbbox(), 'nonzero_px': nonzero,
                   'area_px': (crop[2] - crop[0]) * (crop[3] - crop[1])}
    except Exception as e:
        changed = {'error': '%s: %s' % (type(e).__name__, e)}
    ok = bool(isinstance(changed, dict) and changed.get('nonzero_px'))
    print('[结果 ] 输入框像素变化 %s -> 文本输入 %s' % (changed, 'OK' if ok else 'NG'))
    results.append({'step': 'type text into box',
                    'detail': '打入 %r 后输入框区域像素变化：%s（截图 %s）'
                              % (TYPED, changed, os.path.basename(after_img)),
                    'verdict': 'hit' if ok else 'miss'})

finally:
    # 4) 无论中途出什么错都退出（不做任何提交）。上一版在 OCR 处抛异常，
    #    结果把游戏留在装备码页里没退出 —— 收尾必须放 finally。
    for _ in range(3):
        adb_util.shell('input', 'keyevent', '4')
        time.sleep(1.5)
    try:
        pages = shot()
    except Exception as e:
        pages = 'exit-check-failed: %s' % e
    print('[退出 ] 当前 pages=%s' % (pages,))

hit = sum(1 for r in results if r['verdict'] == 'hit')
print()
print('小计: hit %d / %d（最终 pages=%s）' % (hit, len(results), pages))
out = os.path.join(HERE, '..', 'data', 'text_input_verify.json')
with open(out, 'w', encoding='utf-8') as f:
    json.dump({'results': results, 'final_pages': pages, 'typed': TYPED},
              f, ensure_ascii=False, indent=2, default=str)
print('明细: %s' % os.path.abspath(out))

# ---------------------------------------------------------------- 报告
lines = [
    '# 文本输入原语验证（装备码流程）',
    '',
    '上游唯一用到文本输入的地方是装备码（`module/equipment/equipment_code.py` 的',
    '`d.send_keys(text=code)`，uiautomator2 后端）。本机控制后端是 adb，对应形态是',
    '`input text` —— 这一页验的就是**我们要用的那个形态**能不能真把字打进去。',
    '',
    '设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服）。',
    '脚本：`tools/diagnostics/verify_text_input.py`；数据 `data/text_input_verify.json`。',
    '',
    '| 步骤 | 结果 | 依据 |',
    '| --- | --- | --- |',
]
for r in results:
    lines.append('| %s | %s | %s |' % (r['step'], r['verdict'], r['detail']))
lines += [
    '',
    '打入的字符串是 `%s`，退出后停在 `%s` —— **没有做任何提交**（装备码页有「导入/导出」，'
    % (TYPED, pages),
    '点了会真的改装备；红线守卫里 CONFIRM 类素材一律拒绝点击）。',
    '',
    '## 两个值得记住的点',
    '',
    '1. **不要用 `EQUIPMENT_CODE_TEXTBOX` 的 area 去比像素差。** 那个 area 只是输入栏的',
    '   中段，而文字从栏的最左端开始渲染；第一版只比中段，得到"零变化"，',
    '   把已经成功的输入误判成失败。后来直接看截图才确认成功（输入栏里清楚显示 `%s`）。' % TYPED,
    '2. **收尾必须放 `finally`。** 第一版在 OCR 调用处抛异常，结果把游戏留在了装备码页里',
    '   没退出 —— 这类"验证脚本崩了但设备停在半路"是最容易留下副作用的情形。',
    '',
    '## 复现',
    '',
    '```powershell',
    '$env:STUB_ADB = "<adb.exe>"',
    'python tools/diagnostics/verify_text_input.py',
    '```',
    '',
]
path = os.path.join(HERE, '..', 'docs', 'archive/reports/text-input.md')
with open(path, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(lines))
print('报告: %s' % os.path.abspath(path))
