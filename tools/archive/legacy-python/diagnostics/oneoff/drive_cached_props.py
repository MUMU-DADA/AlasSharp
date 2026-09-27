# -*- coding: utf-8 -*-
"""驱动 6 个 cached_property 识别规则：构造 UI 实例 → 取属性 → 调识别方法。

与上一轮的区别：上一轮只证明"能构造出对象"，这一轮真正**调它们的识别方法**。
main 参数就用 UI 实例自身（它本就是 ModuleBase，带 device.image）。
"""
import importlib
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import alas_vision as av  # noqa: E402

ADB = os.environ['STUB_ADB']
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')
subprocess.run([ADB, '-s', '127.0.0.1:16384', 'exec-out', 'screencap', '-p'],
               stdout=open(PROBE, 'wb'), check=False)
av.handle_line(json.dumps({'id': 1, 'op': 'screenshot_load', 'args': {'path': PROBE}}))

from module.config.config import AzurLaneConfig  # noqa: E402

TARGETS = [
    ('module.retire.dock', 'Dock', 'dock_filter'),
    ('module.storage.ui', 'StorageUI', 'storage_filter'),
    ('module.shop.ui', 'ShopUI', '_shop_bottom_navbar'),
    ('module.shop.ui', 'ShopUI', 'shop_nav_250814'),
    ('module.shop.ui', 'ShopUI', 'shop_tab_250814'),
]

image = av._state['image']
ok = fail = 0
for modname, clsname, attr in TARGETS:
    inst = None
    try:
        cls = getattr(importlib.import_module(modname), clsname)
        dev = av._make_main_shim(image).device
        inst = cls(AzurLaneConfig('alas'), dev)
        inst.device.image = image          # 用真实截图替换
        rule = getattr(inst, attr)
    except Exception as e:
        print('%-42s 构造失败: %s: %s' % (f'{clsname}.{attr}', type(e).__name__, str(e)[:90]))
        fail += 1
        continue

    results = {}
    for meth in ('appear', 'get', 'get_info', 'get_active', 'match_color', 'at_top'):
        fn = getattr(rule, meth, None)
        if fn is None or not callable(fn):
            continue
        try:
            v = fn(inst)
            results[meth] = v if isinstance(v, (bool, int, float, str, type(None))) else f'<{type(v).__name__}>'
        except Exception as e:
            results[meth] = f'{type(e).__name__}: {str(e)[:70]}'
    bad = any(isinstance(v, str) and ('Error' in v or 'Exception' in v) for v in results.values())
    ok += 0 if bad else 1
    fail += 1 if bad else 0
    print('%-42s %s%s' % (f'{clsname}.{attr}', json.dumps(results, ensure_ascii=False)[:100],
                          '   <<< 异常' if bad else ''))

print()
print('驱动成功 %d / 异常 %d / 共 %d' % (ok, fail, len(TARGETS)))
