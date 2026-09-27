# -*- coding: utf-8 -*-
"""试探 5 个 cached_property 规则实例：构造 UI 类 → 取属性 → 调识别方法。

目的不是一次成功，而是**拿到精确的失败点**——每一类缺什么，一次看清。
"""
import importlib
import json
import os
import subprocess
import sys
import traceback

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import alas_vision as av  # noqa: E402

ADB = os.environ['STUB_ADB']
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')
subprocess.run([ADB, '-s', '127.0.0.1:16384', 'exec-out', 'screencap', '-p'],
               stdout=open(PROBE, 'wb'), check=False)
av.handle_line(json.dumps({'id': 1, 'op': 'screenshot_load', 'args': {'path': PROBE}}))

TARGETS = [
    ('module.retire.dock', 'Dock', 'dock_filter'),
    ('module.storage.ui', 'StorageUI', 'storage_filter'),
    ('module.shop.ui', 'ShopUI', '_shop_bottom_navbar'),
    ('module.shop.ui', 'ShopUI', 'shop_nav_250814'),
    ('module.shop.ui', 'ShopUI', 'shop_tab_250814'),
    ('module.shop_event.ui', 'EventShopUI', 'event_shop_tab_count_and_navbar'),
]

from module.config.config import AzurLaneConfig  # noqa: E402
ManualConfig = lambda: AzurLaneConfig('alas')

out = []
for modname, clsname, attr in TARGETS:
    rec = {'module': modname, 'class': clsname, 'attr': attr}
    try:
        mod = importlib.import_module(modname)
    except Exception as e:
        rec['import_error'] = f'{type(e).__name__}: {e}'
        out.append(rec)
        continue
    cls = getattr(mod, clsname, None)
    if cls is None:
        rec['error'] = f'模块里没有 {clsname}'
        out.append(rec)
        continue
    # 试三种构造方式
    for how, args in (('cfg', None), ('cfg+device', 'dev')):
        try:
            cfg = ManualConfig()
            if args == 'dev':
                shim = av._make_main_shim(av._state['image'])
                inst = cls(cfg, shim.device)
            else:
                inst = cls(cfg)
            v = getattr(inst, attr)
            rec[how] = {'ok': True, 'type': type(v).__name__,
                        'repr': repr(v)[:120]}
            break
        except Exception as e:
            rec[how] = f'{type(e).__name__}: {str(e)[:160]}'
    out.append(rec)

print(json.dumps(out, ensure_ascii=False, indent=1))
