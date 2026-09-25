#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
识图引擎（库）—— 路线甲的核心约定：**识图不重写**。

本模块只做三件事：
  1) 复用上游的 `Button` / `Template` 对象（直接 import `module.<x>.assets`，不重建）；
  2) 直接调用上游的方法（`appear_on` / `match` / `match_result` / `Ocr`），
     底层就是上游那套 cv2 调用序列；
  3) 把结果序列化成 JSON。

C# 侧一行图像算法都不实现。手工移植 cv2 已被实测证伪：OpenCV 会按模板/搜索区的
尺寸比切换相关算法，同一块内容在不同尺寸下得分不同（实测同位置 0.7487 vs 1.0000）。

两种宿主共用本模块：
  - 进程内：C# 通过 CPython C API 调 `handle_line()`（见 Alas.Core/Vision/PythonHost.cs）
  - 进程外：`vision_worker.py` 用 stdio/TCP 跑同一份 `handle_line()`
因此两种宿主的语义完全一致，换宿主不影响上层。

**导入本模块无副作用**（只装配 sys.path），协议流分离之类的事由宿主负责。
"""
from __future__ import annotations

import importlib
import json
import logging
import os
import sys
import time
import traceback
from contextlib import contextmanager, nullcontext
from pathlib import Path

FORK = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                     '..', '.runtime', 'engine'))
if FORK not in sys.path:
    sys.path.insert(0, FORK)
os.chdir(FORK)

import module.device.pkg_resources  # noqa: F401  桩必须最先导入（见上游 AGENTS.local.md）
import module.config.server as server_module  # noqa: E402

_state = {
    'image': None,
    'image_path': None,
    'assets': {},      # asset_id -> Button/Template 对象
    'imported': set(),
}


def _resolve(asset_id: str):
    """`ui/CAMPAIGN_CHECK` -> module.ui.assets.CAMPAIGN_CHECK（上游的真实对象）。"""
    if asset_id in _state['assets']:
        return _state['assets'][asset_id]
    if '/' not in asset_id:
        raise KeyError(f'素材 id 需要 `模块/名字` 形式，收到: {asset_id}')
    mod_name, name = asset_id.rsplit('/', 1)
    mod_name = mod_name.replace('/', '.')
    if not name.isidentifier() or not all(part.isidentifier() for part in mod_name.split('.')):
        raise KeyError(f'素材 id 包含无效的模块或名字: {asset_id}')
    py_mod = f'module.{mod_name}.assets'
    if py_mod not in _state['imported']:
        importlib.import_module(py_mod)
        _state['imported'].add(py_mod)
    mod = sys.modules[py_mod]
    if not hasattr(mod, name):
        raise KeyError(f'{py_mod} 里没有 {name}')
    obj = getattr(mod, name)
    _state['assets'][asset_id] = obj
    return obj


def _require_image():
    if _state['image'] is None:
        raise RuntimeError('尚未加载截图，先调用 screenshot_load')
    return _state['image']


def _color_of(button):
    try:
        c = button.color
        return [float(v) for v in c] if c is not None else None
    except Exception:
        return None


# --------------------------------------------------------------------- ops
def op_ping(args):
    import cv2
    import numpy as np
    return {
        'python': sys.version.split()[0],
        'executable': sys.executable,
        'numpy': np.__version__,
        'cv2': cv2.__version__,
        'fork': FORK,
        'server': server_module.server,
        'host': 'in-process' if not getattr(sys, 'argv', []) or '--stdio' not in sys.argv else 'worker',
    }


def op_set_server(args):
    s = args['server']
    if s not in server_module.VALID_SERVER:
        raise ValueError(f'非法服务器: {s}')
    # Native pages and tasks import resources outside _resolve's local cache.
    # The upstream setter releases all registered assets, OCR and map caches.
    server_module.set_server(s)
    return {'server': s}


def op_screenshot_load(args):
    from module.base.utils import load_image
    path = args['path']
    _state['image'] = load_image(path)
    _state['image_path'] = path
    return {'shape': list(_state['image'].shape), 'path': path}


def op_screenshot_set(args):
    """
    用**字节流**设置当前截图（真实设备路径：adb 回来的就是 PNG 字节）。

    这里刻意逐行复现上游 module/device/method/adb.py 的 screenshot_adb 解码序列：
        image = np.frombuffer(screenshot, np.uint8)
        image = cv2.imdecode(image, cv2.IMREAD_COLOR)
        cv2.cvtColor(image, cv2.COLOR_BGR2RGB, dst=image)      # BGR → RGB，ALAS 内部约定是 RGB

    架构含义：**像素不跨语言边界**。C# 只传字节，识图始终在 Python 侧，
    这样上游那套（含 OpenCV 的算法路径切换、定点精度等）就是唯一真值来源。
    """
    import base64
    import cv2
    import numpy as np
    raw = base64.b64decode(args['png_base64'])
    buf = np.frombuffer(raw, np.uint8)
    image = cv2.imdecode(buf, cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError('cv2.imdecode 返回空（字节流不是有效图片？）')
    cv2.cvtColor(image, cv2.COLOR_BGR2RGB, dst=image)
    _state['image'] = image
    _state['image_path'] = args.get('label')
    return {'shape': list(image.shape), 'bytes': len(raw),
            'label': args.get('label')}


def op_screenshot_scale(args):
    """
    把当前截图按给定因子重采样（用于**识别层的缩放适配**）。

    语义对齐上游 `Template.match(image, scaling=...)`：上游是 `scaling = 1/scaling` 后
    对**图像**做 cv2.resize。这里的 factor 就是那个最终作用于图像的 fx/fy。
    适配的用途：模拟器 DPI 与素材采集时不一致会让所有 UI 元素错位，
    用缩放把坐标系对齐，而不是要求用户改模拟器。
    """
    import cv2
    image = _require_image()
    f = float(args['factor'])
    if f <= 0:
        raise ValueError('factor 必须为正')
    before = list(image.shape)
    _state['image'] = cv2.resize(image, None, fx=f, fy=f)
    return {'factor': f, 'before': before, 'after': list(_state['image'].shape)}

def op_asset_button_center(args):
    """
    取上游 Button 的**点击坐标**（`button` 区域中心）。

    分层原则：坐标由上游的素材/规则给出，**C# 只负责把它点下去**。
    这样"点哪里"这件事始终只有一个真值来源，不会在移植中漂移。
    """
    btn = _resolve(args['asset'])
    area = getattr(btn, 'button', None) or getattr(btn, 'area', None)
    if area is None:
        raise ValueError(f'{args["asset"]} 没有 button/area')
    x1, y1, x2, y2 = [float(v) for v in area]
    return {
        'asset': args['asset'],
        'button': [x1, y1, x2, y2],
        'center': [int(round((x1 + x2) / 2)), int(round((y1 + y2) / 2))],
        'name': getattr(btn, 'name', None),
    }

def op_ui_rule_inventory(args):
    """
    枚举上游 UI 层的**全部识别规则实体**（页面 + 导航栏 + 开关 + 滚动区 + 设置项 + ui_white）。

    这些都是模块级实例（Navbar/Switch/Scroll/Setting），不是常量按钮，
    因此要按类型扫模块属性把它们捞出来。这是"全部识别跑通"的前提清单。
    """
    import importlib
    out = {'modules': {}, 'errors': []}

    def scan(modname):
        try:
            mod = importlib.import_module(modname)
        except Exception as e:
            out['errors'].append(f'{modname}: {type(e).__name__}: {e}')
            return {}
        found = {}
        for name in dir(mod):
            if name.startswith('_'):
                continue
            obj = getattr(mod, name)
            cls = type(obj).__name__
            if cls != 'Page' and _ui_rule_kind(obj) not in ('Navbar', 'Switch', 'Scroll', 'Setting'):
                continue
            entry = {'name': name, 'class': cls}
            # 尽量抽出内部引用的按钮名
            btns = []
            for attr in ('check_button', 'click_button', 'button', 'grids', 'buttons',
                         'states', 'options', 'area'):
                try:
                    v = getattr(obj, attr)
                except Exception:
                    continue
                if v is None:
                    continue
                if hasattr(v, 'name'):
                    btns.append(getattr(v, 'name'))
                elif isinstance(v, (list, tuple, set)):
                    for it in v:
                        if hasattr(it, 'name'):
                            btns.append(getattr(it, 'name'))
                elif isinstance(v, dict):
                    for k, it in v.items():
                        if hasattr(it, 'name'):
                            btns.append(getattr(it, 'name'))
                        elif hasattr(k, 'name'):
                            btns.append(getattr(k, 'name'))
                elif attr == 'area':
                    entry['area'] = [float(x) for x in v] if not isinstance(v, (int, float)) else v
            entry['buttons'] = sorted({b for b in btns if b})
            found[name] = entry
        return found

    for modname in ('module.ui.navbar', 'module.ui.switch', 'module.ui.scroll',
                    'module.ui.setting', 'module.ui.page'):
        out['modules'][modname] = scan(modname)

    inventory = op_ui_rule_list({})
    out['errors'].extend(inventory['errors'])
    out['declarations'] = inventory['declarations']
    for declaration in inventory['declarations']:
        modname = declaration['module']
        if declaration['scope'] == 'module' and modname not in out['modules']:
            out['modules'][modname] = scan(modname)

    # 各模块assets.py 里的按钮/模板总数（界面识别的素材面）
    asset_counts = {}
    for modname in ('module.ui.assets', 'module.ui_white.assets'):
        try:
            mod = importlib.import_module(modname)
            n = sum(1 for k in dir(mod)
                    if type(getattr(mod, k)).__name__ in ('Button', 'Template', 'Mask'))
            asset_counts[modname] = n
        except Exception as e:
            out['errors'].append(f'{modname}: {type(e).__name__}: {e}')
    out['asset_counts'] = asset_counts
    out['summary'] = {k: len(v) for k, v in out['modules'].items()}
    return out

def _make_main_shim(image):
    """
    造一个最小 `main` 替身，把**上游 ModuleBase 的方法**挂上去。

    Navbar/Switch/Scroll 的识别方法签名都是 `(self, ..., main)`，
    main 只需提供 `device.image` 与 `image_color_count` 等少数成员。
    这里刻意不做"等价重写"，而是把上游的实现原样绑上去 —— 识图逻辑只有一个真值来源。
    """
    import types
    from module.base.base import ModuleBase

    class _Stuck:
        def stuck_record_add(self, *a, **k):
            return None

        def stuck_record_clear(self, *a, **k):
            return None

    class _Device:
        pass

    class _Config:
        BUTTON_OFFSET = 30
        SERVER = server_module.server

    class _Main:
        interval_timer = {}
        interval_timer_reached = {}

    main = _Main()
    dev = _Device()
    dev.image = image
    dev.stuck_record_add = _Stuck().stuck_record_add
    dev.stuck_record_clear = _Stuck().stuck_record_clear
    main.device = dev
    main.config = _Config()
    # 把上游 ModuleBase 的**全部方法**绑到替身上，而不是逐个补需要的名字 ——
    # 逐个补会不断漏（实测：先只绑 image_color_count，Switch 缺 appear、Scroll 缺 image_crop）。
    # 绑全部才是"不自己实现"：行为完全来自上游，替身只是省掉了 __init__ 需要的 config。
    for name, impl in ModuleBase.__dict__.items():
        if isinstance(impl, types.FunctionType):
            setattr(_Main, name, impl)
    return main


def op_navbar_info(args):
    """按上游 Navbar.get_info 判定底部/页面导航栏的选中项（实例在 module/shop_event/ui.py）。"""
    import module.shop_event.ui as se_ui
    image = _require_image()
    navbar = getattr(se_ui, args.get('attr') or 'navbar')
    shim = _make_main_shim(image)
    active, left, right = navbar.get_info(shim)
    return {
        'name': getattr(navbar, 'name', None),
        'active': active, 'left': left, 'right': right,
        'total_buttons': len(navbar.grids.buttons),
        'buttons': [getattr(b, 'name', str(b)) for b in navbar.grids.buttons],
        'active_color': list(navbar.active_color),
        'inactive_color': list(navbar.inactive_color),
    }

def _ui_rule_kind(obj):
    from module.ui.switch import Switch
    from module.ui.scroll import Scroll
    from module.ui.navbar import Navbar
    from module.ui.setting import Setting
    return next((base.__name__ for base in (Switch, Scroll, Navbar, Setting)
                 if isinstance(obj, base)), type(obj).__name__)


def op_ui_rule_list(args):
    """Discover native controls, including subclasses and lazy declarations.

    Only module objects are loaded here. Properties/factories retain their source
    location and are constructed by the original task when its state allows it.
    No copied control table or per-page constructor is maintained by this host.
    """
    from ui_rule_catalog import discover_controls
    inventory = discover_controls(FORK)
    rules, errors = [], list(inventory['errors'])
    for declaration in inventory['declarations']:
        if declaration['scope'] != 'module':
            continue
        module, name = declaration['module'], declaration['name']
        try:
            obj = getattr(importlib.import_module(module), name)
            kind = _ui_rule_kind(obj)
            if kind != declaration['kind']:
                raise TypeError(f'Expected {declaration["kind"]}, got {type(obj).__name__}')
            entry = {'module': module, 'name': name, 'attr': name,
                     'class': kind, 'native_class': type(obj).__name__}
            if kind == 'Switch':
                entry['states'] = [data['state'] for data in obj.state_list]
                offset = obj.offset
                entry['offset'] = list(offset) if isinstance(offset, tuple) else offset
            if kind == 'Scroll':
                entry['area'] = [int(x) for x in obj.area]
            rules.append(entry)
        except Exception as error:
            errors.append({'module': module, 'name': name,
                           'error': f'{type(error).__name__}: {error}'})
    return {'rules': rules, 'count': len(rules), 'errors': errors,
            'declarations': inventory['declarations'],
            'by_class': {kind: sum(r['class'] == kind for r in rules)
                         for kind in ('Switch', 'Scroll', 'Navbar', 'Setting')}}


def op_ui_rule_check(args):
    """
    对单个 UI 规则实例**调用上游的识别方法**（appear / at_top / at_bottom / get）。

    这是"跑通"的判定方式：不是看它返回 True/False（当前页面不匹配当然是 False），
    而是看**上游代码能不能被正确驱动、不抛异常**，以及结果是否自洽。
    """
    import importlib
    m = importlib.import_module(args['module'])
    obj = getattr(m, args['name'])
    shim = _make_main_shim(_require_image())
    kind = _ui_rule_kind(obj)
    results, errors = {}, []
    for meth in ('appear', 'match_color', 'at_top', 'at_bottom', 'get', 'offset'):
        fn = getattr(obj, meth, None)
        if fn is None or not callable(fn):
            continue
        try:
            v = fn(shim)
            # numpy 标量（np.bool_/np.float64…）不是 Python 内建类型，若不先 .item()
            # 会被下面的分支转成字符串 "<bool>"，在调用方 `if res.get('appear')` 里**恒为真值**，
            # 导致命中统计虚高（实测：module_level 20 个报了 10 个"命中"）。
            if hasattr(v, 'item') and callable(getattr(v, 'item')):
                try:
                    v = v.item()
                except Exception:
                    pass
            if isinstance(v, (bool, int, float, str)) or v is None:
                results[meth] = v
            else:
                results[meth] = f'<{type(v).__name__}>'
        except Exception as e:
            errors.append({'method': meth, 'error': f'{type(e).__name__}: {e}'})
    # Switch 的状态清单与每个状态对应的**可点按钮**：有了它，C#/诊断就能像上游
    # Switch.click(state, main) 那样改状态（上游正是取 get_data(state)['click_button'] 再点），
    # 从而把"识别"升级成"能驱动并复核"。
    state_buttons = []
    for data in getattr(obj, 'state_list', []) or []:
        chk, clk = data.get('check_button'), data.get('click_button')
        state_buttons.append({
            'state': data.get('state'),
            'check_area': [int(v) for v in getattr(chk, 'area', [])] if chk is not None else None,
            'click_area': [int(v) for v in getattr(clk, 'button', [])] if clk is not None else None,
        })
    return {'module': args['module'], 'name': args['name'], 'class': kind,
            # Scroll 的拖拽区域与方向：控件验证要在**它自己的区域**里拖，
            # 在别处滑动等于测了个寂寞（命中率与 at_top/at_bottom 都不作数）
            'area': [int(v) for v in obj.area] if hasattr(obj, 'area') else None,
            'is_vertical': bool(getattr(obj, 'is_vertical', False)),
            'state_buttons': state_buttons,
            'results': results, 'errors': errors}

def op_page_list(args):
    """列出上游 module/ui/page.py 定义的页面及其 check_button（识图规则的入口）。"""
    import module.ui.page as page_mod
    pages = []
    for name in sorted(dir(page_mod)):
        if not name.startswith('page_'):
            continue
        obj = getattr(page_mod, name)
        if type(obj).__name__ != 'Page':
            continue
        cb = getattr(obj, 'check_button', None)
        pages.append({
            'page': name,
            'check_button': getattr(cb, 'name', None),
            'check_file': getattr(cb, 'file', None),
            'is_main': name == 'page_main',
        })
    return {'pages': pages, 'count': len(pages)}


def op_page_appear(args):
    """Recognize a saved frame with the native UI predicate and ModuleBase.appear.

    The frame-only receiver has no device actions. All page/server differences,
    offset semantics and short-circuit decisions remain in upstream UI code.
    Resource failures propagate instead of becoming ordinary negative matches.
    """
    from module.ui.page import Page
    from module.ui.ui import UI

    page_name = args['page']
    page = Page.all_pages[page_name]
    if page.check_button is None:
        return {'page': page_name, 'appear': False, 'tried': [], 'verifiable': False}
    offset = args.get('offset', (30, 30))
    if isinstance(offset, list):
        offset = tuple(offset)
    main = _make_main_shim(_require_image())
    native_appear = main.appear
    tried = []

    def traced_appear(button, **kwargs):
        attempt = {'button': getattr(button, 'name', None),
                   'offset': kwargs.get('offset', 0)}
        tried.append(attempt)
        try:
            hit = bool(native_appear(button, **kwargs))
        except Exception as e:
            attempt['error'] = f'{type(e).__name__}: {e}'
            raise
        attempt['appear'] = hit
        return hit

    main.appear = traced_appear
    hit = UI.ui_page_appear(main, page, offset=offset)
    return {'page': page_name, 'appear': bool(hit), 'tried': tried}

def op_asset_info(args):
    obj = _resolve(args['asset'])
    info = {
        'id': args['asset'],
        'type': type(obj).__name__,
        'name': getattr(obj, 'name', None),
        'is_gif': bool(getattr(obj, 'is_gif', False)),
    }
    for attr in ('area', 'color', 'button', 'file'):
        v = getattr(obj, attr, None)
        if isinstance(v, tuple):
            v = list(v)
        info[attr] = v
    try:
        # Button initializes image explicitly; Template loads through its native
        # image property. Both keep their original GIF/preprocessing behavior.
        ensure = getattr(obj, 'ensure_template', None)
        if ensure is not None:
            ensure()
        template = obj.image
        if info['is_gif']:
            info['frame_shapes'] = [list(f.shape) for f in template]
            info['image_count'] = len(template)
        else:
            info['image_shape'] = list(template.shape)
            info['image_count'] = 1
    except Exception as e:
        info['image_error'] = f'{type(e).__name__}: {e}'
    return info


def op_appear_on(args):
    """直接调用上游 Button.appear_on（= color_similar(get_color(...), color)）。

    detail=True 时返回分段耗时，用于把「上游真正在算的时间」与「封装/协议开销」切开。
    """
    detail = bool(args.get('detail'))
    marks = {}
    t0 = time.perf_counter()

    image = _require_image()
    marks['require_image'] = time.perf_counter()

    button = _resolve(args['asset'])
    marks['resolve'] = time.perf_counter()

    threshold = args.get('threshold', 10)
    appear = bool(button.appear_on(image, threshold=threshold))
    marks['appear_on'] = time.perf_counter()

    from module.base.utils import color_similarity, get_color
    got = [float(v) for v in get_color(image, button.area)]
    marks['get_color'] = time.perf_counter()

    expected = _color_of(button)
    marks['color_of'] = time.perf_counter()

    result = {
        'appear': appear,
        'threshold': threshold,
        'color': got,
        'expected': expected,
        'tolerance': float(color_similarity(got, expected)) if expected else None,
        'elapsed_ms': round((marks['appear_on'] - marks['resolve']) * 1000, 4),
    }
    if detail:
        result['detail'] = {k: round((v - t0) * 1000, 4) for k, v in marks.items()}
    return result


def op_appear_on_batch(args):
    """批量 appear_on：避免每帧几十次往返。"""
    image = _require_image()
    from module.base.utils import color_similarity, get_color
    threshold = args.get('threshold', 10)
    t0 = time.perf_counter()
    out = []
    for asset_id in args['assets']:
        try:
            button = _resolve(asset_id)
            appear = bool(button.appear_on(image, threshold=threshold))
            expected = _color_of(button)
            tol = float(color_similarity(get_color(image, button.area), expected)) if expected else None
            out.append({'asset': asset_id, 'appear': appear, 'tolerance': tol})
        except Exception as e:
            out.append({'asset': asset_id, 'error': f'{type(e).__name__}: {e}'})
    return {'results': out, 'count': len(out),
            'elapsed_ms': round((time.perf_counter() - t0) * 1000, 4)}


def op_button_match(args):
    """直接调用上游 Button.match；可选地把实测相似度二分反解出来。

    `similarity` 是**阈值**（上游默认 0.85），`score` 才是实测相似度。
    上游 Button.match 只回 bool（内部算出的 sim 不外传，Button 也没有 match_result），
    所以这里**只用上游的 match 本身**做二分：match 的语义是 `sim > similarity`，
    单调，二分 20 次即可把 score 逼到 1e-6。这样不复制任何算法，
    也不会出现"诊断分数与真实判定"两套实现漂移的问题。
    """
    image = _require_image()
    button = _resolve(args['asset'])
    offset = args.get('offset', 30)
    similarity = args.get('similarity', 0.85)
    appear = bool(button.match(image, offset=offset, similarity=similarity))
    result = {
        'match': appear,
        'offset': offset,
        'similarity': similarity,
        'score': None,
        'score_error': None,
        'button_offset': list(button.button) if button._button_offset is not None else None,
    }
    if not args.get('probe_score'):
        return result
    try:
        if not button.match(image, offset=offset, similarity=0.0):
            # 连阈值 0 都不匹配：说明 sim 为 NaN 或走的是 gif 分支的异常路径
            result['score_error'] = '低于 0.0，无法二分（sim 非正或 NaN）'
        elif button.match(image, offset=offset, similarity=1.0):
            result['score'] = 1.0
        else:
            lo, hi = 0.0, 1.0
            for _ in range(20):
                mid = (lo + hi) / 2
                if button.match(image, offset=offset, similarity=mid):
                    lo = mid
                else:
                    hi = mid
            result['score'] = round(lo, 6)
    except Exception as e:
        result['score_error'] = f'{type(e).__name__}: {e}'
    return result


def op_template_match(args):
    """直接调用上游 Template.match_result，返回相似度与匹配到的按钮。"""
    image = _require_image()
    template = _resolve(args['asset'])
    sim, button = template.match_result(image, name=args.get('name'))
    return {'similarity': float(sim), 'button_area': list(button.area)}


def op_ocr(args):
    """走上游的 OCR 门面（ONNX 后端），识图同样不重写。"""
    image = _require_image()
    from module.base.button import Button
    from module.ocr.ocr import Ocr
    a = args['area']
    btn = Button(area=tuple(a), color=(), button=tuple(a), name=args.get('name', 'probe'))
    # Ocr.letter 是 RGB 字色，alphabet 才是字符白名单。缺省/null 不覆盖
    # 上游构造器默认值；空字符串白名单等显式值必须原样传递。
    options = {key: args[key] for key in ('lang', 'letter', 'threshold', 'alphabet', 'name')
               if args.get(key) is not None}
    if 'letter' in options:
        letter = options['letter']
        if (not isinstance(letter, (list, tuple)) or len(letter) != 3
                or any(type(channel) is not int or not 0 <= channel <= 255 for channel in letter)):
            raise ValueError('letter must be an RGB array of three integers from 0 to 255; use alphabet for characters')
        options['letter'] = tuple(letter)
    ocr = Ocr(btn, **options)
    return {'text': ocr.ocr(image)}


def op_page_current(args):
    """当前画面命中的页面集合（只跑页面判定，不带 Switch/Scroll/cached_property）。

    产品路径用这个而不是 ui_rules_sweep：导航只需要"我在哪一页"。
    """
    hits = []
    errors = []
    for pg in op_page_list({})['pages']:
        try:
            if op_page_appear({'page': pg['page']})['appear']:
                hits.append(pg['page'])
        except Exception as e:
            errors.append(f"{pg['page']}: {type(e).__name__}: {e}")
    return {'hit': hits, 'errors': errors}


def op_account_state(args):
    """账号/环境状态的**只读**快照：当前页面、是否在图内、服务器、章节与关键配置。

    为什么需要它（R2 第二个任务域）：批量任务开跑前必须能回答"现在是什么状态" ——
    在不在图里（上一局有没有残留）、停在哪一页、跑的是哪个服务器与哪份配置。
    这些判断**不需要点任何东西**，所以它能在 dry-run 里安全地跑，
    也能在没有设备时用存盘帧验收（`screenshot` 参数直接给帧路径）。

    只读纪律：不点击、不导航；只有显式 `capture=true` 才让设备抓一帧。
    device_cached 只读已存在设备的最后原生帧，不创建设备，不回退到离线宿主帧。
    判据全部交给上游：页面用 `Page.check_button`，在图内用 `handler/IN_MAP` 的
    **同一个 `appear` 判定**（`ModuleBase.appear` → `Button.appear_on`，颜色比对），
    不在 C# 侧另写一套。
    """
    for name in ('capture', 'device_cached'):
        if name in args and type(args[name]) is not bool:
            raise ValueError(f'{name} 必须是布尔值')
    capture, cached = args.get('capture') is True, args.get('device_cached') is True
    screenshot = args.get('screenshot')
    if sum((capture, cached, bool(screenshot))) > 1:
        raise ValueError('capture、device_cached 和 screenshot 只能选择一个画面来源')
    source = 'device_capture' if capture else 'device_cached' if cached else 'host_frame'
    out = {'server': getattr(server_module, 'server', None), 'capture': capture,
           'frame': {'available': False, 'path': None, 'shape': None, 'source': source}}
    dev = None
    try:
        if screenshot or capture or cached:
            # Any failed attempt at a different source invalidates old evidence.
            _state['image'] = None
            _state['image_path'] = None
        if screenshot:
            out['loaded'] = op_screenshot_load({'path': screenshot})
        elif capture or cached:
            if cached:
                dev = _DEVICE_OBJ
                if dev is None or not getattr(dev, 'has_cached_image', False):
                    out['error'] = '常驻设备没有缓存画面；未创建设备或抓帧'
                    return out
            else:
                dev = _device_engine()
                dev.screenshot()
            image = getattr(dev, 'image', None)
            if image is None:
                out['error'] = '设备抓帧失败：device.image 为空'
                return out
            _state['image'] = image
            _state['image_path'] = None
    except Exception as e:
        label = '设备抓帧失败' if capture else '取当前画面失败'
        out['error'] = f'{label}: {type(e).__name__}: {e}'
        return out

    image = _state.get('image')
    out['frame'] = {
        'available': image is not None,
        'path': _state.get('image_path'),
        'shape': list(image.shape) if image is not None else None,
        'source': source,
    }
    if image is None:
        out['error'] = ('宿主还没有当前画面：先 screenshot_load / screenshot_set / '
                        'device_capture_set，或传 capture=true 从设备抓一帧')
        return out

    current = op_page_current({})
    out['pages'] = current['hit']
    out['page_errors'] = current['errors']
    try:
        appear = op_appear_on({'asset': 'handler/IN_MAP'})
        out['in_map'] = bool(appear['appear'])
        out['in_map_evidence'] = {'color': appear['color'], 'expected': appear['expected'],
                                  'tolerance': appear['tolerance']}
    except Exception as e:
        out['in_map'] = None
        out['in_map_error'] = f'{type(e).__name__}: {e}'

    inst = _CAMPAIGN.get('obj')
    out['campaign'] = None if inst is None else {
        'chapter': _CAMPAIGN.get('chapter'),
        'stage': getattr(_CAMPAIGN.get('loader'), 'stage', None),
        'instantiated': True,
    }

    try:
        cfg = dev.config if dev is not None else _map_config()
        keys = ('Campaign_Name', 'Campaign_Mode', 'Campaign_UseClearMode', 'Campaign_UseAutoSearch',
                'Fleet_Fleet1', 'Fleet_Fleet2', 'Submarine_Fleet', 'Emotion_Mode',
                'Emulator_ScreenshotMethod', 'Emulator_ControlMethod')
        config = {}
        for key in keys:
            value = getattr(cfg, key, None)
            config[key] = value if isinstance(value, (str, int, float, bool)) or value is None \
                else str(value)
        out['config'] = config
        out['config_name'] = getattr(cfg, 'config_name', None)
    except Exception as e:
        out['config'] = None
        out['config_error'] = f'{type(e).__name__}: {e}'
    return out


def op_task_catalog(args):
    """上游**任务目录**（只读）：有哪些任务、各自属于哪些组、调度入口是什么。

    为什么要有它（周期任务域的数据源）：科研/建造/委托/每日这类周期任务的清单与调度
    定义在上游 `module/config/argument/task.yaml` 里，并且由上游生成器变成
    `args.json` / `menu.json`。本项目**不在 C# 侧另维护一份任务表** —— 与素材、地图规则
    同一条纪律：要用就先问宿主（宿主才有上游代码），C# 只消费结果。

    只读：不写配置、不生成产物、不碰游戏。
    """
    from module.config.utils import read_file
    source = os.path.join(FORK, 'module', 'config', 'argument', 'task.yaml')
    try:
        # Native read_file returns {} for a missing file. Missing input must not
        # look like a valid empty catalog or fall back to a different checkout.
        if not os.path.isfile(source):
            raise FileNotFoundError(source)
        data = read_file(source)
    except Exception as error:
        return {'source': source, 'error': f'读不到上游 task.yaml: {type(error).__name__}: {error}'}

    names, groups, problems = [], {}, []
    if isinstance(data, dict):
        names = sorted(data.keys())
        for name, value in data.items():
            tasks = value.get('tasks') if isinstance(value, dict) else None
            if not isinstance(tasks, dict) or not all(isinstance(task, str) for task in tasks):
                problems.append(f'上游分组 {name} 缺少有效 tasks 对象')
            else:
                groups[name] = list(tasks)
    else:
        problems.append('上游 task.yaml 必须是分组对象')

    # **两个来源要分清楚**（实测踩过）：`task.yaml` 的顶层键是**分组**（本机 9 个），
    # 而"有哪些任务"的扁平表是上游生成器产出的 `args.json`（本机 68 个）。
    # 直接把前者当任务清单会得出错误结论，所以两个都报，并标明各自是什么。
    generated, generated_error = [], None
    try:
        with open(os.path.join(FORK, 'module', 'config', 'argument', 'args.json'),
                  encoding='utf-8') as stream:
            generated_data = json.load(stream)
            if not isinstance(generated_data, dict):
                raise ValueError('args.json 必须是任务对象')
            generated = sorted(generated_data)
    except Exception as e:
        generated_error = f'{type(e).__name__}: {e}'
        problems.append(f'读不到生成任务表: {generated_error}')
    if generated_error is None:
        members = {task for tasks in groups.values() for task in tasks}
        if members != set(generated):
            problems.append(f'分组与生成任务表不一致: 仅分组={sorted(members - set(generated))}, '
                            f'仅生成表={sorted(set(generated) - members)}')
    return {
        'source': source,
        'loader': 'module.config.utils.read_file',
        'raw_type': type(data).__name__,
        'source_groups': names,
        'source_group_count': len(names),
        'generated_tasks': generated,
        'generated_task_count': len(generated),
        'generated_source': os.path.join(FORK, 'module', 'config', 'argument', 'args.json'),
        'generated_error': generated_error,
        'groups': groups,
        'error': '; '.join(problems) if problems else None,
    }


def op_task_schedule(args):
    """周期任务的存盘 Scheduler 快照；不是原生有效配置或实际运行计划。

    数据来源是两个**别混为一谈**的东西（第七节已查实）：
      * `module/config/argument/args.json` —— 任务表（扁平清单，以它为准）；
      * 账号配置 `config/alas.json` 每个任务下的 `Scheduler` 段（`Enable` / `NextRun` …）。

    **只读纪律**：不写配置、不触发任务、**不重算 NextRun**（那是上游调度器的逻辑，
    含 `ServerUpdate` 语义；自己实现一版等于养第二份真相）。
    """
    only_enabled = args.get('only_enabled', True)
    limit = args.get('limit', 30)
    out = {'semantics': 'stored_config', 'only_enabled': only_enabled}
    if type(only_enabled) is not bool:
        return {**out, 'error': 'only_enabled 必须是 JSON 布尔值'}
    if (isinstance(limit, bool) or not isinstance(limit, (int, float))
            or not 1 <= limit <= 2147483647 or limit != int(limit)):
        return {**out, 'error': 'limit 必须是 1 到 2147483647 的整数数值'}
    limit = int(limit)
    requested_path = args.get('config_path')
    if requested_path is not None and (not isinstance(requested_path, str) or not requested_path.strip()):
        return {**out, 'error': 'config_path 必须是非空字符串或 null'}
    args_json = os.path.join(FORK, 'module', 'config', 'argument', 'args.json')
    try:
        with open(args_json, encoding='utf-8') as stream:
            task_names = sorted(json.load(stream).keys())
        out['task_source'] = args_json
        out['task_count'] = len(task_names)
    except Exception as e:
        out['error'] = f'读不到任务表: {type(e).__name__}: {e}'
        return out

    config_path = None
    # 允许显式指定配置路径：既支持多份账号配置，也让**边界验收**能用构造的假配置
    # （全禁用 / 全启用 / 缺 Scheduler 段）去跑同一条代码路径，而不是只测真配置。
    candidates = ([args['config_path']] if args.get('config_path')
                  else [os.path.join(FORK, 'config', 'alas.json'), './config/alas.json'])
    for candidate in candidates:
        if os.path.exists(candidate):
            config_path = candidate
            break
    if config_path is None:
        out['error'] = ('读不到账号配置 config/alas.json —— 前置条件不满足；'
                        '这属于环境问题（Failed），不是"没跑"（skipped）')
        return out
    try:
        with open(config_path, encoding='utf-8') as stream:
            config = json.load(stream)
        out['config_source'] = config_path
    except Exception as e:
        out['error'] = f'账号配置读不出来: {type(e).__name__}: {e}'
        return out

    if not isinstance(config, dict):
        return {**out, 'error': '账号配置根节点必须是 JSON 对象'}
    entries, missing_scheduler = [], []
    for name in task_names:
        section = config.get(name, {})
        if not isinstance(section, dict):
            return {**out, 'error': f'{name} 必须是 JSON 对象'}
        if 'Scheduler' not in section:
            missing_scheduler.append(name)
            entries.append({'task': name, 'enable': None, 'next_run': None,
                            'scheduler_present': False})
            continue
        scheduler = section['Scheduler']
        if not isinstance(scheduler, dict):
            return {**out, 'error': f'{name}.Scheduler 必须是 JSON 对象'}
        if 'Enable' in scheduler and type(scheduler['Enable']) is not bool:
            return {**out, 'error': f'{name}.Scheduler.Enable 必须是 JSON 布尔值'}
        next_run = scheduler.get('NextRun')
        if next_run is not None and not isinstance(next_run, str):
            return {**out, 'error': f'{name}.Scheduler.NextRun 必须是字符串或 null'}
        entries.append({
            'task': name,
            'enable': scheduler.get('Enable'),
            'next_run': next_run,
            'scheduler_present': True,
        })
    enabled = [e for e in entries if e['enable'] is True]
    out['enabled_count'] = len(enabled)
    out['no_scheduler_count'] = len(missing_scheduler)
    listed = enabled if only_enabled else entries
    out['listed_count'] = len(listed)
    out['tasks'] = listed[:limit]
    out['no_scheduler_sample'] = missing_scheduler[:5]
    return out


def _resolve_periodic_target(task):
    """从上游任务目录解析 scheduler command 及其原生调度方法。"""
    import ast
    import inflection

    alas_py = os.path.join(FORK, 'alas.py')
    args_json = os.path.join(FORK, 'module', 'config', 'argument', 'args.json')
    if not os.path.exists(alas_py):
        return {'task': task, 'found': False, 'error': f'找不到上游 alas.py: {alas_py}'}
    try:
        with open(alas_py, encoding='utf-8') as stream:
            tree = ast.parse(stream.read())
        with open(args_json, encoding='utf-8') as stream:
            task_catalog = json.load(stream)
    except Exception as error:
        return {'task': task, 'found': False,
                'error': f'读取上游任务目录失败: {type(error).__name__}: {error}'}

    script = next((node for node in tree.body
                   if isinstance(node, ast.ClassDef) and node.name == 'AzurLaneAutoScript'), None)
    if script is None:
        return {'task': task, 'found': False, 'error': 'alas.py 缺少 AzurLaneAutoScript'}
    methods = {node.name: node for node in script.body if isinstance(node, ast.FunctionDef)}

    matches = []
    for section, groups in task_catalog.items():
        if not isinstance(groups, dict):
            continue
        scheduler = groups.get('Scheduler')
        command_arg = scheduler.get('Command') if isinstance(scheduler, dict) else None
        command = command_arg.get('value') if isinstance(command_arg, dict) else None
        if not isinstance(command, str) or not command:
            continue
        method = inflection.underscore(command)
        if task in (method, command) and method in methods:
            matches.append((str(section), command, method, methods[method]))

    if not matches:
        return {
            'task': task,
            'found': False,
            'error': f'上游任务目录没有可调度方法 {task}（只允许 Scheduler.Command 对应的入口）',
        }
    if len(matches) > 1:
        commands = sorted({item[1] for item in matches})
        return {'task': task, 'found': False,
                'error': f'上游任务目录对 {task} 的映射不唯一: {commands}'}
    section, command, method, method_node = matches[0]
    return {
        'task': task,
        'found': True,
        'section': section,
        'scheduler_command': command,
        'method': method,
        'source': alas_py,
        'task_source': args_json,
        '_method_node': method_node,
    }


def op_periodic_plan(args):
    """周期任务的**执行前勘察**（只读）：上游调度器会调用哪个原生方法。

    为什么先做这个（`docs/tasks.md` 的周期任务授权边界）：周期任务的动作要真机，而且
    科研/建造/委托这类会**消耗账号资源**，不能无人值守乱跑。但"跑 X 会发生什么"是可查的 ——
    上游把 Scheduler.Command 写在生成的 `args.json`，再由调度器用
    `inflection.underscore()` 映射到 `alas.py` 的方法：

        def commission(self):
            from module.commission.commission import Commission
            Commission(config=self.config, device=self.device).run()

    本 op 同时消费这两个上游来源，用 **AST 读方法体**（不 import、不实例化、不碰设备），
    把 command、方法和内部调用如实报出来。辅助方法不会因为在 `alas.py` 里存在就成为可执行任务。

    **只读纪律**：不 import 目标模块、不构造对象、不调用 run —— 真正的执行属于另一个 op，
    且必须带显式授权。
    """
    task = str(args.get('task') or '').strip()
    if not task:
        return {'error': '缺少 task（上游任务名，如 commission / research）'}
    resolved = _resolve_periodic_target(task)
    if resolved.get('found') is not True:
        return resolved
    import ast
    method = resolved.pop('_method_node')
    imports, constructed = [], []
    for node in ast.walk(method):
        if isinstance(node, ast.ImportFrom):
            module = node.module or ''
            for alias in node.names:
                imports.append(f'from {module} import {alias.name}')
        if isinstance(node, ast.Call):
            func = node.func
            if isinstance(func, ast.Name) and func.id[:1].isupper():
                constructed.append(func.id)
            elif isinstance(func, ast.Attribute):
                constructed.append(func.attr)
    return {
        **resolved,
        'lineno': method.lineno,
        'imports': imports,
        'calls': sorted(set(constructed)),
        'calls_run': any(isinstance(n, ast.Attribute) and n.attr == 'run'
                         for n in ast.walk(method)),
        'note': '只报上游 Scheduler.Command 与原生方法，不 import、不实例化、不执行',
    }


def op_periodic_preflight(args):
    """周期任务执行的**放行判定**（两道闸），**不执行任何游戏动作**。

    为什么先做闸门而不是执行（`docs/tasks.md` 的周期任务授权边界）：查代码发现连"收委托"
    都有花费路径（油满买食物），所以执行入口必须是**按任务显式授权**，不能"授权一次全能跑"。
    闸门可以先做好、先测好，执行留到有真实授权时再接 —— 风险面先被钉住。

    两道闸（缺一不放行）：
      1. `allow_actions=true` —— 运行时的动作总开关；
      2. `confirm` **与 `task` 完全一致** —— 防手滑、防脚本误传（"要跑 reward" 就得把
         reward 写两遍，而不是点一下"同意"）。

    返回 `decision`（allowed / denied）、`reason`，以及**勘察结果**（会去跑哪个类、哪一行）——
    即"放行了的话将要去跑什么"必须随放行一起被看见。
    **本 op 永不执行**：`executes` 恒为 false；真正的执行是另一个 op 的职责。
    """
    task = str(args.get('task') or '').strip()
    allow_actions = args.get('allow_actions') is True
    confirm = str(args.get('confirm') or '').strip()

    out = {'task': task, 'allow_actions': allow_actions, 'confirm_matches': confirm == task,
           'executes': False,
           'note': '本 op 只做放行判定与勘察，不执行任何游戏动作；执行由单独的 op 负责'}
    if not task:
        out['decision'] = 'denied'
        out['reason'] = '缺少 task（上游任务名，如 reward）'
        return out
    if not allow_actions:
        out['decision'] = 'denied'
        out['reason'] = '未授权：需要显式 allow_actions=true（周期任务可能消耗账号资源）'
        return out
    if confirm != task:
        out['decision'] = 'denied'
        out['reason'] = '二次确认不匹配：confirm 必须与 task 完全一致（收到 confirm=%r，task=%r）' % (
            confirm, task)
        return out

    plan = op_periodic_plan({'task': task})
    out['plan'] = plan
    if plan.get('found') is not True:
        out['decision'] = 'denied'
        out['reason'] = f'任务名在上游 alas.py 里找不到：{task}'
        return out
    out['decision'] = 'allowed'
    out['reason'] = ('两道闸都通过；放行后将会执行：' + '; '.join(plan.get('imports') or [])
                     + f"（alas.py 第 {plan.get('lineno')} 行）")
    return out


def op_config_get(args):
    """按**点分路径**读账号配置里的值（只读）：args = {"keys": ["Dorm.BuyFurniture.Enable", ...]}。

    为什么需要它：R4 的"配置"面里最要紧的一类值是**决定任务会不会花资源的开关**
    （docs/archive/history/tasks-20260924.md 花费路径表：dorm 看 BuyFurniture、meowfficer 看 BuyAmount …）。
    判"能不能无人值守跑某个周期任务"时，第一步就是把这些开关的值报出来。

    **通用实现，不写任何具体键名**：调用方给什么路径就读什么路径；读不到时如实区分
    "配置里没这一项"与"配置读不出来" —— **空值不等于 false**（缺省走上游默认值）。
    """
    keys = args.get('keys') or []
    if not isinstance(keys, list) or not keys:
        return {'error': '缺少 keys（点分路径列表，如 ["Dorm.BuyFurniture.Enable"]）'}
    path = os.path.join(FORK, 'config', 'alas.json')
    if not os.path.exists(path):
        return {'error': f'读不到账号配置: {path}',
                'note': '这属于环境问题（Failed），不是"没跑"（skipped）'}
    try:
        with open(path, encoding='utf-8') as stream:
            config = json.load(stream)
    except Exception as e:
        return {'error': f'账号配置读不出来: {type(e).__name__}: {e}'}

    def walk(node, parts):
        for part in parts:
            if not isinstance(node, dict) or part not in node:
                return None
            node = node[part]
        return node

    values = {str(key): walk(config, str(key).split('.')) for key in keys}
    return {
        'config_source': path,
        'values': values,
        'missing': [k for k, v in values.items() if v is None],
        'note': 'missing 表示**配置里没显式设置**（缺省走上游默认值），不等于 false/0',
        'checked': len(keys),
    }


def _api_config_service():
    """Create the upstream data service for non-device API reads.

    The service only reads the selected configuration and statistics files;
    device operations remain in the registered task queue and are never
    reached by these API operations.
    """
    from module.api.config_service import ConfigService
    return ConfigService(Path(FORK))


def op_statistics_report(args):
    from module.api.statistics_service import report
    category = str(args.get('category') or 'resources')
    if category not in ('resources', 'action', 'opsi', 'commission', 'ships', 'loot'):
        raise ValueError('未知统计分类')
    if category == 'loot':
        _require_loot_statistics()
    service = _api_config_service()
    instance = str(args.get('instance') or '')
    month = args.get('month')
    days = int(args.get('days') or 7)
    period = str(args.get('period') or 'month')
    if not 1 <= days <= 365 or period not in ('day', 'week', 'month'):
        raise ValueError('统计时间范围无效')
    return report(service, instance, category, month, days, period)


def op_statistics_refresh_loot(args):
    from module.api.statistics_service import refresh_loot
    _require_loot_statistics()
    service = _api_config_service()
    return refresh_loot(service, str(args.get('instance') or ''))


def _require_loot_statistics():
    from module.statistics.azurstats import AzurStats
    required = ('load_meowofficer_farming', 'get_meowofficer_farming', 'meowofficer_farming_labels')
    if any(not hasattr(AzurStats, name) for name in required):
        raise RuntimeError('当前上游运行时尚未接入本地掉落统计；不能读取或刷新掉落报告')


def op_meowfficer_report(args):
    from module.api.meowfficer_service import report
    service = _api_config_service()
    limit = int(args.get('limit') or 100)
    if not 1 <= limit <= 500:
        raise ValueError('评分报告数量必须在 1 至 500 之间')
    return report(service, str(args.get('instance') or ''), limit)


def op_meowfficer_clear(args):
    from module.api.meowfficer_service import clear
    service = _api_config_service()
    return clear(service, str(args.get('instance') or ''))


def op_shop_strategy_validate(args):
    from module.shop_strategy import validate_strategy
    script = args.get('script')
    if not isinstance(script, str) or len(script) > 20000:
        raise ValueError('策略脚本必须是长度不超过 20000 的字符串')
    return validate_strategy(script)

class _LoggedNativeFailure(logging.Handler):
    def __init__(self):
        super().__init__(logging.WARNING)
        self.kind = None
        self.traceback_tail = []
        self.error_directory = None

    def emit(self, record):
        if isinstance(record.msg, str) and record.msg.startswith('Saving error: '):
            candidate = (Path(FORK) / record.msg.removeprefix('Saving error: ')).resolve()
            if candidate.is_relative_to((Path(FORK) / 'log' / 'error').resolve()):
                self.error_directory = candidate
        error = record.msg if isinstance(record.msg, Exception) else sys.exc_info()[1]
        if not isinstance(error, Exception):
            return
        self.kind = type(error).__name__
        self.traceback_tail = [
            f'{os.path.basename(frame.filename)}:{frame.lineno} {frame.name}'
            for frame in traceback.extract_tb(error.__traceback__)[-8:]
        ]


def op_periodic_run(args):
    """周期任务的**执行**入口：复用上游任务目录和 AzurLaneAutoScript 调度。

    为什么要两道闸（`docs/tasks.md` 的周期任务授权边界）：查代码发现连"收委托"
    都有花费路径（油满买食物），所以执行入口必须**按任务显式授权**，不能"授权一次全能跑"。

      1. `allow_actions=true` —— 运行时的动作总开关；
      2. `confirm` **与 `task` 完全一致** —— 防手滑、防脚本误传。

    通过之后：从上游任务目录解析 Scheduler.Command，绑定该任务的完整配置，再调用
    `AzurLaneAutoScript.run(method)`。这样各任务在 `alas.py` 里的方法、参数、TaskEnd 与失败
    语义都由上游负责；适配层不维护任务特例表。

    **不是**读一眼就完事：这一步会真的驱动游戏 —— 所以调用方必须明确知道自己在跑什么。
    """
    import time
    task = str(args.get('task') or '').strip()
    allow = args.get('allow_actions') is True
    confirm = str(args.get('confirm') or '').strip()
    out = {'task': task, 'allow_actions': allow, 'confirm_matches': confirm == task}
    if not task:
        out.update(decision='denied', reason='缺少 task（上游任务名，如 dorm）')
        return out
    if not allow:
        out.update(decision='denied',
                   reason='未授权：需要显式 allow_actions=true（周期任务可能消耗账号资源）')
        return out
    if confirm != task:
        out.update(decision='denied',
                   reason='二次确认不匹配：confirm 必须与 task 完全一致（收到 confirm=%r）' % confirm)
        return out

    # An explicit instance must never silently become the default account.
    # Validate before constructing native config (its constructor may write).
    instance = args.get('instance')
    if instance is None:
        instance = 'alas'
    else:
        from module.api.config_service import validate_name
        try:
            normalized = validate_name(instance)
        except Exception:
            out.update(decision='denied', reason='实例名无效')
            return out
        if normalized != instance:
            out.update(decision='denied', reason='实例名必须使用规范名称')
            return out
        config_root = (Path(FORK) / 'config').resolve()
        config_file = config_root / (instance + '.json')
        if (not config_file.is_file() or config_file.is_symlink()
                or config_file.resolve().parent != config_root):
            out.update(decision='denied', reason='找不到有效的配置实例')
            return out
    out['instance'] = instance

    plan = op_periodic_plan({'task': task})
    out['plan'] = plan
    if plan.get('found') is not True:
        out.update(decision='denied', reason='任务名在上游 alas.py 里找不到：%s' % task)
        return out
    expected_method = args.get('expected_method')
    if expected_method is not None and expected_method != plan['method']:
        out.update(decision='denied', reason='上游调度目标与放行时核对的方法不一致')
        return out
    expected_command = args.get('expected_scheduler_command')
    if expected_command is not None and expected_command != plan['scheduler_command']:
        out.update(decision='denied', reason='上游调度目标与放行时核对的 Scheduler.Command 不一致')
        return out

    command = plan['scheduler_command']
    method_name = plan['method']
    out['target'] = {
        'module': 'alas',
        'class': 'AzurLaneAutoScript',
        'scheduler_command': command,
        'method': method_name,
    }
    overrides = args.get('overrides')
    if overrides is None:
        overrides = {}
    if not isinstance(overrides, dict):
        out.update(decision='denied', reason='overrides 必须是 JSON 对象')
        return out

    started = time.time()
    device = None
    old_device_config = None
    device_had_config = False
    failure = None
    try:
        from alas import AzurLaneAutoScript
        from module.config.config import AzurLaneConfig
        config = AzurLaneConfig(instance, task=command)
        unknown = sorted(str(key) for key in overrides if key not in config.bound)
        if unknown:
            out.update(decision='denied',
                       reason=f'overrides 含当前任务未绑定的字段: {unknown}')
            return out
        if overrides:
            from native_task_overrides import validate_task_overrides
            from module.api.protocol import ApiError
            try:
                overrides = validate_task_overrides(config, overrides)
            except ApiError as error:
                out.update(decision='denied', reason=f'overrides 无效：{error}')
                return out
        # 上游 override 会登记 overridden 并绕过 __setattr__ 的持久化路径；任务自身通过
        # task_delay/task_call 写调度状态仍保留，这是原生调度语义。
        if overrides:
            config.override(**overrides)

        device = _device_engine(config=config)
        device_had_config = hasattr(device, 'config')
        if device_had_config:
            old_device_config = device.config
        device.config = config
        runner = AzurLaneAutoScript(instance)
        runner.__dict__['config'] = config
        runner.__dict__['device'] = device
        out['constructed'] = True
        device.stuck_record_clear()
        device.click_record_clear()
        out['ran'] = True
        from module.logger import logger
        failure = _LoggedNativeFailure()
        logger.addHandler(failure)
        try:
            if method_name.startswith('opsi_'):
                apply_os_combat_reentry_compat()
            with native_task_runtime(device=device):
                native_success = runner.run(method_name)
        finally:
            logger.removeHandler(failure)
            failure.close()
        out['native_success'] = native_success is True
        if native_success is True:
            out['decision'] = 'ran'
        else:
            out['decision'] = 'failed'
            out['error'] = '上游原生调度器未确认成功'
    except (Exception, SystemExit) as error:
        out['ran'] = bool(out.get('ran'))
        out['native_success'] = False
        out['decision'] = 'error'
        if isinstance(error, SystemExit):
            out['exit_code'] = None if error.code is None else str(error.code)
        out['error'] = f'{type(error).__name__}: {error}'
        out['traceback_tail'] = [
            f'{os.path.basename(frame.filename)}:{frame.lineno} {frame.name}'
            for frame in traceback.extract_tb(error.__traceback__)[-8:]
        ]
    finally:
        if device is not None:
            try:
                if device_had_config:
                    device.config = old_device_config
                else:
                    delattr(device, 'config')
            except Exception as restore_error:
                out['decision'] = 'error'
                out['native_success'] = False
                message = f'恢复设备配置失败: {type(restore_error).__name__}: {restore_error}'
                out['error'] = f"{out['error']}; {message}" if out.get('error') else message
        # Native run may log the real cause and save frames before exiting. Collect
        # them for every exit path, after the exception handler has set the outcome.
        if failure is not None:
            if failure.kind and out.get('error'):
                out['error'] += f'（已记录 {failure.kind}）'
            if failure.traceback_tail:
                out['traceback_tail'] = failure.traceback_tail
            if failure.error_directory is not None and failure.error_directory.is_dir():
                directory = failure.error_directory
                out['native_error_dir'] = directory.relative_to(FORK).as_posix()
                out['failure_frames'] = [
                    frame.relative_to(FORK).as_posix()
                    for frame in sorted(directory.glob('*.png')) if frame.is_file()
                ]
                log_file = directory / 'log.txt'
                if log_file.is_file():
                    out['native_error_log'] = log_file.relative_to(FORK).as_posix()
        if out.get('error') and out.get('traceback_tail'):
            out['error'] += '\n' + '\n'.join(out['traceback_tail'])
        out['elapsed_s'] = round(time.time() - started, 1)
    return out

def op_tool_plan(args):
    """Discover standalone tools from the native registry without constructing tasks."""
    import ast
    import inflection
    from module.submodule.utils import get_available_func

    task = args.get('task')
    registered = list(get_available_func())
    out = {'task': task, 'registered': registered, 'found': False}
    if not isinstance(task, str) or task not in registered:
        out['reason'] = '当前上游未注册此独立工具'
        return out
    method = inflection.underscore(task)
    tree = ast.parse((Path(FORK) / 'alas.py').read_text(encoding='utf-8'))
    methods = {node.name for cls in tree.body if isinstance(cls, ast.ClassDef)
               and cls.name == 'AzurLaneAutoScript' for node in cls.body
               if isinstance(node, ast.FunctionDef)}
    if method not in methods:
        out['reason'] = '上游工具注册表与 alas.py 不一致'
        return out
    out.update(found=True, method=method, skip_first_screenshot=True)
    return out


def op_scheduler_run(args):
    from native_scheduler import run_scheduler
    return run_scheduler(args, sys.modules[__name__])


def op_tool_run(args):
    """Use the upstream webui tool dispatch, preserving lazy config/device access.

    Tool methods bind their own task config. They must not be made into periodic
    tasks, or receive the periodic entry's eager screenshot/device construction.
    """
    import time
    task = args.get('task')
    allow = args.get('allow_actions') is True
    confirm = args.get('confirm')
    out = {'task': task, 'allow_actions': allow, 'confirm_matches': confirm == task,
           'constructed': False, 'ran': False, 'native_success': False}
    if not allow:
        out.update(decision='denied', reason='未授权：需要显式 allow_actions=true')
        return out
    if not isinstance(task, str) or not task or confirm != task:
        out.update(decision='denied', reason='confirm 必须与上游工具名称完全一致')
        return out
    plan = op_tool_plan({'task': task})
    out['plan'] = plan
    if plan.get('found') is not True:
        out.update(decision='denied', reason=plan['reason'])
        return out
    from module.api.config_service import validate_name
    instance = args.get('instance')
    try:
        normalized = validate_name(instance)
    except Exception:
        normalized = None
    config_root = (Path(FORK) / 'config').resolve()
    if not isinstance(instance, str) or normalized != instance:
        out.update(decision='denied', reason='实例名无效或不是规范名称')
        return out
    config_file = config_root / (instance + '.json')
    if (not config_file.is_file() or config_file.is_symlink()
            or config_file.resolve().parent != config_root):
        out.update(decision='denied', reason='找不到有效的配置实例')
        return out
    out['instance'] = instance
    out['target'] = {'module': 'alas', 'class': 'AzurLaneAutoScript',
                     'method': plan['method']}
    started = time.monotonic()
    device = None
    previous_config = None
    failure = _LoggedNativeFailure()
    from module.logger import logger
    logger.addHandler(failure)
    try:
        from alas import AzurLaneAutoScript

        def acquire_device(config):
            nonlocal device, previous_config
            if args.get('device_configured') is not True or not _DEVICE_ARGS.get('serial'):
                raise RuntimeError('工具需要设备，但会话未配置实例串号')
            current = _device_engine(config=config)
            if device is None:
                device = current
                previous_config = current.config
                device.config = config
                device.stuck_record_clear()
                device.click_record_clear()
            else:
                if current is not device:
                    raise RuntimeError('原生工具尝试切换常驻设备')
                device.config = config
            return device

        class ToolRunner(AzurLaneAutoScript):
            @property
            def device(self):
                return scoped_acquire(self.config)

        runner = ToolRunner(config_name=instance)
        out['constructed'] = True
        out['ran'] = True
        with native_task_runtime(acquire_device=acquire_device) as scoped_acquire:
            native_success = runner.run(plan['method'], skip_first_screenshot=True)
        out['native_success'] = native_success is True
        out['decision'] = 'ran' if native_success is True else 'failed'
        if native_success is not True:
            out['error'] = '上游原生工具未确认成功'
    except (Exception, SystemExit) as error:
        out.update(decision='error', error=f'{type(error).__name__}: {error}',
                   traceback_tail=[f'{os.path.basename(frame.filename)}:{frame.lineno} {frame.name}'
                                   for frame in traceback.extract_tb(error.__traceback__)[-8:]])
        if isinstance(error, SystemExit):
            out['exit_code'] = None if error.code is None else str(error.code)
    finally:
        logger.removeHandler(failure)
        failure.close()
        if failure.traceback_tail:
            out['traceback_tail'] = failure.traceback_tail
            if out.get('error'):
                out['error'] += f'（已记录 {failure.kind}）'
        if failure.error_directory is not None and failure.error_directory.is_dir():
            directory = failure.error_directory
            out['native_error_dir'] = directory.relative_to(FORK).as_posix()
            out['failure_frames'] = [frame.relative_to(FORK).as_posix()
                                     for frame in sorted(directory.glob('*.png')) if frame.is_file()]
            if (directory / 'log.txt').is_file():
                out['native_error_log'] = (directory / 'log.txt').relative_to(FORK).as_posix()
        if device is not None:
            try:
                device.config = previous_config
            except Exception as restore_error:
                previous_error = out.get('error')
                message = f'恢复设备配置失败: {type(restore_error).__name__}: {restore_error}'
                out.update(decision='error', native_success=False,
                           error=f'{previous_error}; {message}' if previous_error else message)
                restore_tail = [f'{os.path.basename(frame.filename)}:{frame.lineno} {frame.name}'
                                for frame in traceback.extract_tb(restore_error.__traceback__)[-8:]]
                out['traceback_tail'] = out.get('traceback_tail', []) + restore_tail
        if out.get('error') and out.get('traceback_tail'):
            out['error'] += '\n' + '\n'.join(out['traceback_tail'])
        out['elapsed_s'] = round(time.monotonic() - started, 3)
    return out


def _asset_id_map():
    """id(Button 对象) -> '子模块/资产名'，实时扫描已导入的 module.*.assets。

    上游 Button 不自带来源路径，页面图要用资产 id 表达边（C# 侧靠 id 反查对象），
    所以只能反查。只扫 `module.*.assets`：page.py 的按钮全部来自这些模块。
    """
    mapping = {}
    for modname, mod in list(sys.modules.items()):
        if not modname.startswith('module.') or not modname.endswith('.assets'):
            continue
        if mod is None:
            continue
        sub = modname[len('module.'):-len('.assets')].replace('.', '/')
        for attr in dir(mod):
            if attr.startswith('__'):
                continue
            try:
                obj = getattr(mod, attr)
            except Exception:
                continue
            if type(obj).__name__ in ('Button', 'ButtonGrid', 'Template'):
                mapping.setdefault(id(obj), '%s/%s' % (sub, attr))
    return mapping


def op_ui_page_graph(args):
    """上游页面导航图：节点（页面 + check 资产）+ 边（按钮资产 → 目标页）。

    在**运行时**向上游要图，不导出、不重写：这样上游改了 page.py 的连线，
    C# 侧立刻跟着变，不存在产物漂移。返回前做 id 往返自检
    （用资产 id 反查必须拿回同一个对象），否则 C# 会点到错的按钮。
    """
    import module.ui.page as page_mod
    ids = _asset_id_map()

    def id_of(obj):
        return ids.get(id(obj)) if obj is not None else None

    nodes, edges, unmapped = [], 0, []
    pairs = []  # (资产 id, 原对象)，用于往返自检
    for name, page in sorted(page_mod.Page.all_pages.items()):
        check = id_of(page.check_button)
        if page.check_button is not None and check is None:
            unmapped.append('%s.check' % name)
        elif check:
            pairs.append((check, page.check_button))
        node = {'name': name, 'check': check, 'links': []}
        for dest, button in page.links.items():
            bid = id_of(button)
            if bid is None:
                unmapped.append('%s -> %s' % (name, dest.name))
                continue
            pairs.append((bid, button))
            # Each native Page already owns its exact link button. Do not infer
            # extra edges or button alternatives from asset naming conventions.
            node['links'].append({'to': dest.name, 'button': bid, 'variants': [bid]})
            edges += 1
        nodes.append(node)

    # 往返自检：资产 id 反查必须拿回**同一个对象**，否则 C# 会点到别的按钮
    bad = [aid for aid, obj in pairs if _resolve(aid) is not obj]
    return {'nodes': nodes, 'node_count': len(nodes), 'edge_count': edges,
            'unmapped': unmapped, 'roundtrip_bad': bad,
            'roundtrip_checked': len(pairs)}


def _contained_ui_rules(value, path='$', seen=None):
    """Find actual native controls in property results, without rebuilding them."""
    seen = set() if seen is None else seen
    if id(value) in seen:
        return
    seen.add(id(value))
    if _ui_rule_kind(value) in ('Navbar', 'Switch', 'Scroll', 'Setting'):
        yield path, value
    elif isinstance(value, (list, tuple)):
        for index, child in enumerate(value):
            yield from _contained_ui_rules(child, f'{path}[{index}]', seen)
    elif isinstance(value, dict):
        for key, child in value.items():
            yield from _contained_ui_rules(child, f'{path}[{key!r}]', seen)


def _observe_ui_rule(rule, inst, path):
    """Read native predicates only; construction and metadata are not hits."""
    kind = _ui_rule_kind(rule)
    detail, errors = {}, []
    hit = False
    try:
        if kind == 'Navbar':
            buttons = [getattr(b, 'name', str(b)) for b in rule.grids.buttons]
            active = rule.get_active(inst)
            total = rule.get_total(inst)
            info = rule.get_info(inst)
            detail = {'active': active, 'total': total, 'info': list(info),
                      'buttons': buttons, 'active_color': list(rule.active_color),
                      'inactive_color': list(rule.inactive_color)}
            hit = active is not None and info[0] is not None
        elif kind == 'Switch':
            state = rule.get(inst)
            detail = {'state': state, 'appear': state != 'unknown',
                      'states': [d.get('state') for d in rule.state_list],
                      'offset': rule.offset}
            hit = state != 'unknown'
        elif kind == 'Scroll':
            hit = bool(rule.appear(inst))
            detail = {'appear': hit, 'area': [int(v) for v in rule.area],
                      'is_vertical': bool(rule.is_vertical), 'position': None,
                      'at_top': None, 'at_bottom': None}
            # cal_position divides by the track's detected thumb geometry.
            # An absent thumb has no meaningful position; retain the native miss.
            if hit:
                detail.update(position=float(rule.cal_position(inst)),
                              at_top=bool(rule.at_top(inst)), at_bottom=bool(rule.at_bottom(inst)))
        elif kind == 'Setting':
            settings = rule.settings
            observed = []
            for (setting, option_name), button in settings.items():
                try:
                    if rule.is_option_active(button):
                        observed.append(f'{setting}/{option_name}')
                except Exception as error:
                    errors.append(f'{path}.{setting}/{option_name}: {type(error).__name__}: {error}')
            detail = {'observed_active': observed, 'option_count': len(settings),
                      'settings': sorted({key[0] for key in settings})}
            hit = bool(observed)
    except Exception as error:
        errors.append(f'{path}: {type(error).__name__}: {error}')
    return {'path': path, 'class': kind, 'hit': bool(hit) and not errors,
            'detail': detail, 'errors': errors}


def op_cached_rule_check(args):
    """Construct native lazy rules, including containers, and read their predicates.

    A tuple such as (count, navbar) keeps its original object identity. Scroll,
    Switch, Navbar and Setting all use native methods; no task actions run here.
    """
    from module.config.config import AzurLaneConfig
    image = _require_image()
    cls = getattr(importlib.import_module(args['module']), args['class'])
    inst = cls(AzurLaneConfig('template'), _make_main_shim(image).device)
    rule = getattr(inst, args['attr'])
    controls = [_observe_ui_rule(child, inst, path) for path, child in _contained_ui_rules(rule)]
    errors = [error for control in controls for error in control['errors']]
    detail = (controls[0]['detail'] if len(controls) == 1 and controls[0]['path'] == '$'
              else {'control_count': len(controls)})
    return {'label': '%s.%s' % (args['class'], args['attr']), 'class': _ui_rule_kind(rule),
            'hit': any(control['hit'] for control in controls) and not errors,
            'detail': detail, 'errors': errors, 'controls': controls}


def op_page_positive_control(args):
    """合成正对照：把每条页面规则的 check 素材贴到**它自己的区域**，看规则返回真。

    口径必须说清楚：这**不等于真机命中**。它只证明"规则本身是活的"——
    素材文件能加载、区域与模板配对正确、判定方向没写反（不是恒假）。
    用途是把「页面到不了」（账号/活动/客户端版本所限）与「规则坏了」分开：
    受阻塞的 24 个页面若连正对照都过不了，那就是实现问题而非可达性问题。

    做法：黑底画布 → 把 check 素材自己的模板图贴到它的 area → 跑 ui_page_appear。
    注意贴的是 `area` 而不是 `button`：上游 Button.match 裁的就是 area 那块，
    模板图本身也是从 area 截出来的。
    """
    import numpy as np
    from module.ui.page import Page

    saved = _state['image']
    results = []
    try:
        for name, page in sorted(Page.all_pages.items()):
            cb = page.check_button
            if cb is None:
                results.append({'page': name, 'verdict': 'skip',
                                'detail': 'Page(None)：没有 check 素材（合成实体）'})
                continue
            try:
                cb.ensure_template()
                template = cb.image
                if isinstance(template, list):      # gif 多帧
                    template = template[0]
                if template is None:
                    results.append({'page': name, 'verdict': 'fail',
                                    'detail': '素材没有模板图（ensure_template 后仍为空）'})
                    continue
                h, w = template.shape[:2]
                x1, y1, x2, y2 = [int(v) for v in cb.area]
                canvas = np.zeros((720, 1280, 3), dtype=np.uint8)
                # 画布要够大：素材区域可能超出 1280x720（不同分辨率素材）
                if y1 + h > 720 or x1 + w > 1280:
                    results.append({'page': name, 'verdict': 'skip',
                                    'detail': '素材区域 %s 超出 1280x720，合成画布放不下'
                                              % list(cb.area)})
                    continue
                canvas[y1:y1 + h, x1:x1 + w] = template
                _state['image'] = canvas
                appear = bool(op_page_appear({'page': name})['appear'])
                results.append({'page': name, 'verdict': 'pass' if appear else 'fail',
                                'detail': '素材 %s 贴到 area=%s（%dx%d）后 appear=%s'
                                          % (getattr(cb, 'name', None), list(cb.area),
                                             w, h, appear)})
            except Exception as e:
                results.append({'page': name, 'verdict': 'error',
                                'detail': '%s: %s' % (type(e).__name__, e)})
    finally:
        _state['image'] = saved      # 一定要还原，否则后续 op 会拿着合成图判定

    passed = sum(1 for r in results if r['verdict'] == 'pass')
    skipped = sum(1 for r in results if r['verdict'] == 'skip')
    failed = [r for r in results if r['verdict'] in ('fail', 'error')]
    return {'results': results, 'total': len(results), 'passed': passed,
            'skipped': skipped, 'failed': len(failed),
            'failed_pages': [r['page'] for r in failed]}


def op_rule_positive_control(args):
    """模块级控件规则的合成正对照（目前覆盖 Switch）。

    对每个开关的每个状态：单独把**该状态的 check 素材**贴到它自己的区域，
    然后调上游 `Switch.get()` —— 应当正好返回那个状态名。
    这证明开关的 state_list 是活的（素材能加载、状态与素材配对正确、get 的遍历有效），
    与"页面上到不了"是两件事。

    Scroll / Navbar / Setting 的判定依赖颜色与掩码，不是"贴模板图"能构造的，
    这里如实标为 skipped，不假装验过。
    """
    import importlib
    import numpy as np

    saved = _state['image']
    results = []
    try:
        inventory = op_ui_rule_list({})
        for error in inventory['errors']:
            results.append({'rule': 'discovery', 'kind': 'unknown', 'verdict': 'fail',
                            'detail': str(error)})
        rules = inventory['rules']
        for r in rules:
            mod = importlib.import_module(r['module'])
            obj = getattr(mod, r['name'])
            kind = _ui_rule_kind(obj)
            from module.ui.switch import Switch
            if kind == 'Switch' and type(obj).get is not Switch.get:
                results.append({'rule': r['name'], 'kind': kind, 'verdict': 'skip',
                                'detail': '原生子类有独立识别流程，模板贴图不构成该流程的正样本'})
                continue
            if kind != 'Switch':
                results.append({'rule': r['name'], 'kind': kind, 'verdict': 'skip',
                                'detail': '判定依赖颜色/掩码，贴模板图构造不出来'})
                continue
            states = getattr(obj, 'state_list', []) or []
            if not states:
                results.append({'rule': r['name'], 'kind': kind, 'verdict': 'fail',
                                'detail': 'state_list 为空：这个开关没有任何状态'})
                continue
            per_state, errors = [], []
            for data in states:
                cb = data.get('check_button')
                state = data.get('state')
                if cb is None:
                    errors.append('%s: 没有 check_button' % state)
                    continue
                try:
                    cb.ensure_template()
                    template = cb.image
                    if isinstance(template, list):
                        template = template[0]
                    h, w = template.shape[:2]
                    x1, y1 = int(cb.area[0]), int(cb.area[1])
                    if y1 + h > 720 or x1 + w > 1280:
                        errors.append('%s: 区域超出 1280x720' % state)
                        continue
                    canvas = np.zeros((720, 1280, 3), dtype=np.uint8)
                    canvas[y1:y1 + h, x1:x1 + w] = template
                    _state['image'] = canvas
                    got = obj.get(_make_main_shim(canvas))
                    per_state.append({'state': state, 'got': got, 'ok': got == state})
                except Exception as e:
                    errors.append('%s: %s: %s' % (state, type(e).__name__, e))
            ok = bool(per_state) and not errors and all(s['ok'] for s in per_state)
            results.append({'rule': r['name'], 'kind': kind,
                            'verdict': 'pass' if ok else 'fail',
                            'detail': '；'.join('%s→%s' % (s['state'], s['got'])
                                                for s in per_state)
                                      + ('；错误: %s' % '; '.join(errors) if errors else ''),
                            'states': per_state})
    finally:
        _state['image'] = saved

    passed = sum(1 for x in results if x['verdict'] == 'pass')
    skipped = sum(1 for x in results if x['verdict'] == 'skip')
    failed = [x for x in results if x['verdict'] == 'fail']
    return {'results': results, 'total': len(results), 'passed': passed,
            'skipped': skipped, 'failed': len(failed),
            'failed_rules': [x['rule'] for x in failed]}


_NUMPY2_COMPAT_DONE = False


def prepare_native_runtime():
    """Install numerical compatibility before any native action dispatch.

    A fresh scheduler/tool process must not depend on a previous map probe or
    campaign initialization. These fixes only preserve native geometry types;
    they do not select maps, change recognition rules, or construct a device.
    """
    apply_numpy2_compat()
    apply_points_empty_compat()


@contextmanager
def native_task_runtime(*, acquire_device=None, device=None):
    """Scope shared runtime fixes without inheriting an explicit sortie option."""
    from native_campaign_runtime import native_campaign_scope
    from native_tool_device import native_tool_device_scope

    if acquire_device is None and device is not None:
        def acquire_device(config):
            device.config = config
            return device

    prepare_native_runtime()
    previous = _CLEAR_ALL_OVERRIDE['enabled']
    _CLEAR_ALL_OVERRIDE['enabled'] = False
    try:
        device_scope = (native_tool_device_scope(acquire_device, device=device)
                        if acquire_device is not None else nullcontext())
        with native_campaign_scope(sys.modules[__name__]), device_scope as scoped_acquire:
            yield scoped_acquire
    finally:
        _CLEAR_ALL_OVERRIDE['enabled'] = previous


def apply_numpy2_compat():
    """上游与 numpy 2 的兼容垫片（**上游代码一行不改**）。

    上游 `module/map_detection/utils.py` 的 `Lines.cross` 写的是：

        points = np.vstack(self.cross_two_lines(self, other))

    而 `cross_two_lines` 是个**生成器**。numpy 2 不再接受把生成器直接交给 `np.vstack`，
    实测报：`TypeError: arrays to stack must be passed as a "sequence" type such as list or tuple.`

    本环境是 Python 3.14（只能配 numpy 2.4.6），所以地图识别会卡在这一步 ——
    表象很像"识别不到地图"，实际与客户端 UI 毫无关系。

    垫片只把生成器具体化成 list，检测算法本身仍然全部是上游的。
    """
    global _NUMPY2_COMPAT_DONE
    if _NUMPY2_COMPAT_DONE:
        return
    _NUMPY2_COMPAT_DONE = True
    try:
        import numpy as _np
        from module.map_detection import utils as _md_utils
        major = int(str(_np.__version__).split('.')[0])
        if major >= 2 and hasattr(_md_utils.Lines, 'cross_two_lines'):
            _orig = _md_utils.Lines.cross_two_lines
            _md_utils.Lines.cross_two_lines = staticmethod(lambda l1, l2: list(_orig(l1, l2)))
    except Exception:
        # 垫片失败不该让 op 直接崩：真有问题会在调用处以原始异常暴露
        pass


# ---------------------------------------------------------------------------
# 客户端垫片：OS（作业海域）画面上"找地图四角"这一步要用 OS 遮罩
# ---------------------------------------------------------------------------
# 上游 `Perspective.load_image` 里写死了战役遮罩：
#     cv2.bitwise_and(image, ASSETS.ui_mask, dst=image)      # perspective.py:172
# 而 homography.py:67-72 的 warp 后遮罩却按 Scheduler_Command 切 OS/战役 —— 两处不对称。
#
# 实测（真机海域画面 os_live_2.png，同一套上游代码）：
#   用战役遮罩：MapDetectionError: Failed to find a free tile
#   改用 OS 遮罩：OK grids=49 shape=[8,5]（9x6）
# 原因：本客户端 OS 画面底部那条 UI 栏（第一舰队/储物舱/情报/作战总览）**不在战役遮罩的
# UI 区域内**，其边界被当成地图下边（实测底部两角 y=701.6、x=13.6/1500.2，超出屏宽 1280），
# 单应性因此算错。
#
# 只在 _OS_MASK_MODE 为真时改行为，战役路径一行不变。
_OS_MASK_MODE = False
_OS_MASK_COMPAT_DONE = False


def apply_os_mask_compat():
    """把 Perspective.load_image 包一层：OS 模式下换用 OS 遮罩（上游代码不改）。"""
    global _OS_MASK_COMPAT_DONE
    if _OS_MASK_COMPAT_DONE:
        return
    _OS_MASK_COMPAT_DONE = True
    try:
        from module.base.utils import rgb2gray, crop
        from module.map_detection import perspective as _persp
        from module.map_detection.utils_assets import Assets as _Assets
        import cv2 as _cv2
        _orig = _persp.Perspective.load_image

        def _load_image(self, image):
            if not _OS_MASK_MODE:
                return _orig(self, image)
            g = rgb2gray(crop(image, self.config.DETECTING_AREA, copy=False))
            _cv2.bitwise_and(g, _Assets().ui_mask_os, dst=g)
            _cv2.bitwise_not(g, dst=g)
            return g

        _persp.Perspective.load_image = _load_image
    except Exception:
        pass


def set_os_mask_mode(on):
    """由 op 在调用前后开关（默认关，避免影响战役路径）。"""
    global _OS_MASK_MODE
    _OS_MASK_MODE = bool(on)


def apply_points_empty_compat():
    """上游 `Points` 的**空集没有定义**（潜在缺陷，与 numpy 无关）。

    `module/map_detection/utils.py` 里：

        class Points:
            def __init__(self, points):
                if points is None or len(points) == 0:
                    self._bool = False
                    self.points = None          # ← 空集分支**不设** x / y
                else:
                    ...
                    self.x, self.y = self.points.T

    于是空集一旦被用到（`.x` / `.y` / `to_lines` 等）就抛
    `AttributeError: 'Points' object has no attribute 'x'`。
    上游调用方通常先判空所以平时不炸；我们在**非地图画面上反复调用**时踩到了
    （实测：战役菜单画面连续调用后，第 3 次起连续报此错）。

    垫片：空集也给出**空数组**，让下游自然退化成"没有点/没有线"，而不是崩溃。
    """
    try:
        from module.map_detection import utils as _md_utils
        if getattr(_md_utils.Points, '_alas_empty_compat', False):
            return
        _orig_init = _md_utils.Points.__init__

        def _init(self, points):
            _orig_init(self, points)
            if not hasattr(self, 'x'):
                import numpy as _np
                self.x = _np.array([])
                self.y = _np.array([])

        _md_utils.Points.__init__ = _init
        _md_utils.Points._alas_empty_compat = True
    except Exception:
        pass


# ---------------------------------------------------------------------------
# S3：在宿主里驱动上游的章节 Campaign（战斗动作交给上游实现，C# 只管流程）
# ---------------------------------------------------------------------------
_CAMPAIGN = {'obj': None, 'chapter': None}

# 会**驱动作战/改变游戏状态**的方法名前缀：默认一律拒调，必须显式 allow_actions=True。
# 这条联锁是硬性的 —— 项目早期误开自律寻敌、把一场战斗打完的教训还记着。
_DANGER_PREFIX = ('battle', 'clear', 'enter_map', 'run', 'mob_move', 'fleet',
                  'goto', 'map_', 'ambush', 'siren', 'submarine', 'auto_search',
                  'combat', 'withdraw', 'retreat',
                  # **补漏**：`execute_a_battle` 是上游真正的"打一步"入口（campaign_base.run()
                  # 的循环体），但它不以 'battle' 开头，此前**绕过了 allow_actions 安全锁** ✗
                  'execute', 'full_scan')





def apply_withdraw_trace_compat():
    """把上游的 `withdraw()` 包一层：**打印调用栈**，用于查明"谁在主动撤退"。

    背景：用户多次观察到"打一半自己点撤退了"。已知 `execute_a_battle` 在
    "10 次都打不出战果"时会 `self.withdraw()`（campaign_base.py:113），
    但可能还有别的路径（任务收尾 / 异常处理 / 我方流程结束时的清理）。
    只有拿到**调用栈**才能确定，靠读代码推断已被证明不可靠 ✗。
    """
    try:
        from module.map import map_operation as _mo
    except Exception:
        return
    for _name, _cls in list(vars(_mo).items()):
        if not isinstance(_cls, type) or getattr(_cls, '_alas_withdraw_trace', False):
            continue
        if 'withdraw' not in _cls.__dict__:
            continue
        _orig = _cls.__dict__['withdraw']

        def _withdraw(self, *a, __orig=_orig, **kw):
            import traceback as _tb
            from module.logger import logger as _lg
            _lg.warning('=== WITHDRAW CALLED ===')
            for _ln in _tb.format_stack()[-6:-1]:
                _lg.warning('  ' + _ln.strip().replace('\n', ' | ')[:160])
            return __orig(self, *a, **kw)

        setattr(_cls, 'withdraw', _withdraw)
        _cls._alas_withdraw_trace = True


def apply_auto_search_skip_compat():
    """客户端适配：`handle_auto_search()` 的**开关状态判定**在本客户端不可靠。

    历史现场记录见归档 `docs/archive/history/s3-entry-sequence.md`：进 3-1 时 `enter_map` 在该处理器上反复点击
    `AUTO_SEA`（**被截断的按钮名**；真实按钮是 `AUTO_SEARCH_MAP_OPTION_ON/OFF`，
    位于 (1205,549,1275,566)），19.8s 后 `GameTooManyClickError`。
    上游判定是"双重 appear"（`module/handler/auto_search.py:179`：offset 窗口内一次 + 精确一次），
    这种判定在 UI 差异下最容易失败。

    定义处是 `module/handler/fast_forward.py:300`（不是 auto_search.py —— 我第一次找错了，
    垫片按"在模块里找哪个类的 __dict__ 定义了它"来定位，不依赖类名）。

    我们的配置本就要求**关闭**自律寻敌（`Campaign_UseAutoSearch=False`，已读回确认），
    所以 `map_is_auto_search` 为假时**直接跳过**该处理 —— 符合配置，且绕开不可靠的 UI 判定。
    """
    try:
        from module.handler import fast_forward as _ff
    except Exception:
        return
    for name, cls in list(vars(_ff).items()):
        if not isinstance(cls, type) or getattr(cls, '_alas_autosearch_compat', False):
            continue
        if 'handle_auto_search' not in cls.__dict__:
            continue
        orig = cls.__dict__['handle_auto_search']

        def _handle_auto_search(self, *a, __orig=orig, **kw):
            if not getattr(self, 'map_is_auto_search', False):
                return False          # 配置要求关闭 → 不做任何点击
            return __orig(self, *a, **kw)

        setattr(cls, 'handle_auto_search', _handle_auto_search)
        cls._alas_autosearch_compat = True


def apply_os_combat_reentry_compat():
    """Restore AzurPilot's OS auto-search handoff when combat starts between screenshots.

    The ALAS host checks map exclusion, loading and preparation; AzurPilot
    commit 257bef255d checks is_combat_executing() before preparation handlers.
    Keep the original preparation checks after that detector so an executing frame
    cannot trigger preparation-overlay confirmation.
    """
    from module.os_combat.combat import Combat as OSCombat

    if getattr(OSCombat, '_alas_combat_reentry_compat', False):
        return
    original = OSCombat.combat_appear

    def combat_appear(self):
        if self.is_in_map():
            return False
        if self.is_combat_loading():
            return True
        if self.is_combat_executing():
            return True
        return original(self)

    OSCombat.combat_appear = combat_appear
    OSCombat._alas_combat_reentry_compat = True


def apply_fleet_bar_compat():
    """客户端适配：`FleetOperator.bar_opened()` 的亮度阈值（垫片，不改上游文件）。

    上游判据（module/map/map_fleet_preparation.py）：

        luma = rgb2gray(main.image_crop(self._bar.button))[:, -1]
        return np.sum(luma > 168) / luma.size > 0.5

    本客户端实测（离线复算现场帧，用上游对象算的几何）：
        下拉关闭 0.000 / 下拉展开 **0.285** —— 区域确实响应状态，但**永远跨不过 0.5**。
    原因：本客户端下拉只有约 **84px 高**（上游参照 y 269..515 共 246px），亮边占不满整列。
    后果：`open()` 里 `if bar_opened(): break` 永不成立 → 反复点『选择』→
          `Timer(3, count=6)` 点满 → `GameTooManyClickError: FLEET_1_CHOOSE`（实测）。
    垫片：阈值 0.5 → **0.10**（展开 0.15+ / 关闭 0.000，余量充足）。
    """
    try:
        import numpy as _np
        from module.base.utils import rgb2gray as _rgb2gray
        from module.map.map_fleet_preparation import FleetOperator as _FO
        if getattr(_FO, '_alas_bar_compat', False):
            return
        _orig = _FO.bar_opened

        def _bar_opened(self):
            try:
                luma = _rgb2gray(self.main.image_crop(self._bar.button, copy=False))[:, -1]
                return float(_np.sum(luma > 168)) / luma.size > 0.10
            except Exception:
                return _orig(self)

        _FO.bar_opened = _bar_opened
        _FO._alas_bar_compat = True
    except Exception:
        pass


#: `IN_MAP`（地图内判据）在本客户端实测需要的相似度上限。
#: 上游默认 10，而本客户端真实地图帧的相似度落在 3.33 ~ 10.33（跨过 10），
#: 非地图帧最近也在 83 以上 —— 判别间隔极大，放宽到 20 仍离非地图帧很远。
_IN_MAP_THRESHOLD = 20.0


def apply_in_map_threshold_compat():
    """只放宽 `IN_MAP` 这一个素材的判据阈值 —— 本客户端「撤退」按钮的颜色落在上游阈值外侧。

    **证据（2026-09-23 真机窗口，根因实测）**：

      * 真机 1-1 卡死时存下的现场帧 `data/_live_enter_map_stall.png` 里，游戏**已经在地图内**
        （舰队已就位、右下角「撤退」按钮在屏），但 `IN_MAP` 颜色比对相似度 = **10.19**，
        上游 `appear(button, threshold=10)` 要求 < 10 → 判"不在图内"。
      * 上游 `enter_map()` 的等待集里就含 `IN_MAP`，于是它一直等到 `stuck_record_check`
        抛 `GameStuckError: Wait too long`（实测 62s，见 `docs/archive/history/device-stall-in-map.md`）。
      * 同一客户端的地图帧上，该按钮相似度分布为 3.33 / 10.06 / 10.19 / 10.33；
        非地图帧最近的在 83 以上（本批 92.07，另一批 83.11）。**阈值 10 恰好卡在真实取值带里。**
      * 为什么不改颜色常量：另一个真实取值 (210,124,124)（相似度 3.33 那类帧）换个常量后
        会反过来失败 —— 只有放宽阈值能同时覆盖两类真实帧。

    只动这一个素材的 `appear_on` 阈值，**不改全局 `appear` 语义**，也不放宽其它按钮。
    复现：`python tools/diagnostics/verify_account_state.py`（含逐帧相似度留证）。
    """
    if _state.get('in_map_compat'):
        return
    try:
        button = _resolve('handler/IN_MAP')
    except Exception:
        return
    original = button.appear_on

    def appear_on(image, threshold=10):
        # 只放宽、不收紧：调用方传了更大的阈值时用调用方的。
        return original(image, threshold=max(float(threshold), _IN_MAP_THRESHOLD))

    button.appear_on = appear_on
    _state['in_map_compat'] = True


@contextmanager
def campaign_button_color_compat():
    """During native sorties, confirm near-miss colors with the asset's fixed-area template."""
    from module.base.button import Button
    from module.base.utils import color_similarity, get_color

    original = Button.appear_on

    def appear_on(button, image, threshold=10):
        if original(button, image, threshold=threshold):
            return True
        if not button.file or float(threshold) != 10.0:
            return False
        try:
            tolerance = float(color_similarity(get_color(image, button.area), button.color))
            return (tolerance < float(threshold) * 2
                    and button.match(image, offset=(0, 0), similarity=0.85))
        except Exception:
            return False

    Button.appear_on = appear_on
    try:
        yield
    finally:
        Button.appear_on = original


def apply_boss_icon_color_compat(enabled=False):
    """**可选兜底**：给 BOSS 判据补一条"蓝色眼睛"判据。**默认关闭**。

    ⚠️ 2026-09-22 深夜更正 —— 这条垫片的**前提是我自己的诊断搞错了**，所以默认关掉：

    我最初的依据是"存盘的 PNG 里 BOSS 眼睛是蓝的，而上游红色判据恒不匹配"。
    但那张 PNG 是 `cv2.imwrite(path, E)` 写的，cv2 把数组当 BGR，**文件色相对真实屏幕是
    R/B 互换的**；`load_image()` 又走 PIL 忠实读文件 —— 两次叠加，我在文件上量到的"蓝"
    恰好是引擎图 E 上的"红"。在 E（引擎真正交给上游的图）上复测：

        python tools/diagnostics/oneoff/probe_boss_channel.py data/_map_now3.png
        === 文件色 ===  上游原版 predict_boss 认出的 BOSS 格: []
        === 引擎 E ===  上游原版 predict_boss 认出的 BOSS 格: [((3, 2), 'BO')]   ← 原版就能认出来

    即：**本客户端的 BOSS 图标本来就是上游期望的红色，`predict_boss()` 一直能工作**。
    "只剩 BOSS 却撤退"的真正原因在别处 —— BOSS 所在格没进过相机视野（上游 `full_scan()` 只走
    `MAP.camera_data`，而 BOSS 可能刷在别的 `MB` 格），加上扫描时机早于 BOSS 刷新；
    执行器每轮调上游自带的 `full_scan_find_boss()`（上游自己没调用它）正好补上这一环。

    保留本函数是为了"万一某章 BOSS 图标真不是红色"时能一键打开对比；
    **没有被证实需要就不要开** —— 多一条判据就多一类误判。
    """
    if not enabled:
        return
    try:
        import module.map_detection.grid_predictor as _gp
        from module.base.utils import color_similarity_2d as _sim
        if getattr(_gp.GridPredictor, '_alas_boss_color_compat', False):
            return
        _orig = _gp.GridPredictor.predict_boss

        def _predict_boss(self, __orig=_orig):
            # 上游判据（红色 + 小图标红色 hue）先跑，命中就直接返回 —— 保持上游行为优先
            if __orig(self):
                return True
            try:
                if self.enemy_genre == 'Siren_Siren':
                    return False
                image = self.relative_crop((-0.55, -0.2, 0.45, 0.2), shape=(50, 20))
                image = _sim(image, color=(82, 77, 255))
                return bool(_gp.TEMPLATE_ENEMY_BOSS.match(image, similarity=0.75))
            except Exception:
                return False

        _gp.GridPredictor.predict_boss = _predict_boss
        _gp.GridPredictor._alas_boss_color_compat = True
    except Exception:
        pass


_CLEAR_ALL_OVERRIDE = {'enabled': False}


def apply_clear_all_override(enabled=True):
    """显式选择"**先清光小怪、再打 BOSS**"这条上游分支（`MAP_CLEAR_ALL_THIS_TIME=True`）。

    上游其实有**两套完全不同的战斗流程**（`module/campaign/campaign_base.py`）：

    | 分支 | 选择条件 | 行为 |
    | --- | --- | --- |
    | `MAP_CLEAR_ALL_THIS_TIME=False`（默认） | — | `battle_{battle_count}`：BOSS 一刷出来（`spawn_data` 里那个回合）就打 BOSS，小怪可能还剩 |
    | `MAP_CLEAR_ALL_THIS_TIME=True` | 见下 | 每轮先清 `enemy+siren+fortress`（`.delete(is_boss)`），**清光才** `battle_boss()` |

    而 True 这个开关是上游自己算的（`fast_forward.py:199-201`）：

        MAP_CLEAR_ALL_THIS_TIME = STAR_REQUIRE_3
            and not 已拿到第 STAR_REQUIRE_3 颗星
            and StopCondition_MapAchievement in ['map_3_stars', 'threat_safe']

    也就是说：**只有"还缺星"的图才会自动全清**；已经 3 星的图（本账号大部分图）
    永远走 `battle_{battle_count}`，"全清"这条永远进不去 —— 用户实测反馈的
    "全清小怪后再打 BOSS 的场景依旧没有跑通"，根因就是这个开关恒为 False
    （启动日志里能直接看到 `[MAP_CLEAR_ALL_THIS_TIME] False` /
    `[StopCondition_MapAchievement] non_stop`）。

    这里给一个**显式开关**（不改上游文件）：在 `handle_fast_forward()` 前后各强制一次配置值。
    之所以前后都设：原方法内部会按上面的公式把它重算成 False，后面那次是兜底。

    两个场景都要能用，所以这个是**按次开关**：调用方显式传 `clear_all=true` 才生效。
    """
    _CLEAR_ALL_OVERRIDE['enabled'] = bool(enabled)
    try:
        from module.handler import fast_forward as _ff
    except Exception:
        return
    for name, cls in list(vars(_ff).items()):
        if not isinstance(cls, type) or getattr(cls, '_alas_clear_all_compat', False):
            continue
        if 'handle_fast_forward' not in cls.__dict__:
            continue
        orig = cls.__dict__['handle_fast_forward']

        def _handle_fast_forward(self, *a, __orig=orig, **kw):
            from module.logger import logger as _lg
            forced = False
            if _CLEAR_ALL_OVERRIDE['enabled']:
                try:
                    if not getattr(self.config, 'MAP_CLEAR_ALL_THIS_TIME', False):
                        forced = True
                    self.config.MAP_CLEAR_ALL_THIS_TIME = True
                except Exception:
                    pass
            result = __orig(self, *a, **kw)
            if _CLEAR_ALL_OVERRIDE['enabled']:
                try:
                    if not getattr(self.config, 'MAP_CLEAR_ALL_THIS_TIME', False):
                        self.config.MAP_CLEAR_ALL_THIS_TIME = True
                        forced = True
                except Exception:
                    pass
                if forced:
                    _lg.attr('MAP_CLEAR_ALL_THIS_TIME', True)
                    _lg.info('全清分支（上游 MAP_CLEAR_ALL_THIS_TIME=True）：'
                             '先清光小怪与塞壬，清完才打 BOSS')
            return result

        setattr(cls, 'handle_fast_forward', _handle_fast_forward)
        cls._alas_clear_all_compat = True


def _campaign_mode(args):
    mode = args.get('mode')
    if mode is not None and mode not in ('normal', 'hard'):
        raise ValueError('mode 必须是 normal、hard 或 null（沿用账号配置）')
    return mode


def op_s3_campaign_init(args):
    """实例化上游章节的 `Campaign`，初始化设备并抓取首帧，不点击。

    S3 要执行的 tier A 调用（`battle_default` / `clear_siren` / …）是 ALAS 的 Campaign 方法，
    按铁律不能重写成 C#。探针已验证它在宿主里可实例化
    （`tools/diagnostics/s3_probe_campaign.py`）；本 op 把它接到协议上。
    """
    # An init attempt supersedes the previous chapter even if validation,
    # loading or frame seeding fails. Publish the new instance only at the end.
    _CAMPAIGN.clear()
    mode = _campaign_mode(args)
    chapter = str(args.get('chapter') or 'campaign.campaign_main.campaign_2_1')
    apply_numpy2_compat()
    apply_points_empty_compat()
    from s3_camera_compat import apply_camera_previous_view_compat
    apply_camera_previous_view_compat()
    apply_fleet_bar_compat()
    apply_auto_search_skip_compat()
    apply_boss_icon_color_compat()
    apply_in_map_threshold_compat()
    # 两个战斗场景的选择：默认（False）= BOSS 一刷出来就打 BOSS；
    # clear_all=True = 先清光小怪再打 BOSS。按次开关，每次 init 都要显式设回来。
    apply_clear_all_override(bool(args.get('clear_all', False)))
    apply_withdraw_trace_compat()
    try:
        from campaign_rules import resolve_native_campaign
        resolve_native_campaign(chapter)
    except Exception as error:
        return {'chapter': chapter, 'instantiated': False,
                'error': f'上游章节加载失败: {type(error).__name__}: {error}',
                'error_code': getattr(error, 'code', 'native_dependency_error'),
                'traceback_tail': traceback.format_exc().strip().splitlines()[-8:]}
    for k in ('serial', 'screenshot', 'control'):
        if args.get(k):
            _DEVICE_ARGS[k] = args[k]
    cfg = _map_config()
    try:
        cfg.bind('Campaign')
    except Exception as e:
        return {'error': f'配置绑定 Campaign 失败: {type(e).__name__}: {e}',
                'traceback_tail': traceback.format_exc().strip().splitlines()[-8:]}
    # Validate the fresh account's connection identity before loading or taking
    # a frame. Temporary transport overrides must survive native rebinds without
    # persisting session backends or inheriting a previous task's overrides.
    dev = _device_engine(config=cfg)
    # **配置必须跟着章节走**，否则 Campaign 会按错误关卡取参数（实测踩过：
    # 实例化 2-1，而 config.Campaign_Name 还是上一次跑过的 '12-4'）。
    import re as _re
    stage = ''
    m = _re.search(r'campaign_(\d+)_(\d+)$', chapter)
    if m:
        stage = '%s-%s' % (m.group(1), m.group(2))
    try:
        with cfg.multi_set():
            if stage:
                cfg.Campaign_Name = stage
            # **默认关掉"周回模式(ClearMode)"与"自律寻敌(AutoSearch)"** —— 上游自己的开关，
            # 比点 UI 可靠得多（本项目曾因误开自律把一场战斗打完）。要开就显式传 True。
            cfg.Campaign_UseClearMode = bool(args.get('clear_mode', False))
            cfg.Campaign_UseAutoSearch = bool(args.get('auto_search', False))
            # **心情模式**：这是"低心情强制出击弹窗"的唯一开关（上游自己的配置）。
            #
            # 实测（2026-09-22 深夜，7-1）：连打多场后客户端弹
            #   「信息：第N舰队中「…」处于低心情状态，强制出击将降低好感且获得经验减半」[取消][确定]
            # 上游**有**这个弹窗的处理器 `handle_combat_low_emotion()`（info_handler.py:187），
            # 但它第一行就是 `if not self.emotion.is_ignore: return False`
            # （`is_ignore` = `'ignore' in config.Emotion_Mode`，默认是 'calculate' ✗）。
            # 结果是 `combat_preparation()` 一直等战斗 UI，180s 后 `GameStuckError: Wait too long`，
            # 出击被中断（日志：`Wait too long / Waiting for {GAME_TIPS4, PAUSE, ...}`）。
            # 所以真跑必须把它设成含 ignore 的档位：'calculate_ignore' = 照常计算心情 + 忽略弹窗。
            # 代价要讲清楚：强制出击确实会扣（实测战果页三艘船 `EXP -588`），
            # 因此这里只改"别卡死"，心情本身由上游的 Emotion_FleetXControl 继续管。
            cfg.Emotion_Mode = str(args.get('emotion_mode') or 'calculate_ignore')
            # **舰队选择也是配置项**（不是从地图推出来的）：
            #   map_fleet_preparation.fleet_preparation() 读
            #   [Fleet_Fleet1, Fleet_Fleet2, Submarine_Fleet]，0 表示"不用"。
            # 实测踩过：默认 [1,2,0] 会去"清空第二舰队"，而本账号第二舰队是空的
            # （清空按钮不存在）→ 等一组永不出现的按钮 → GameStuckError。
            # 默认只用第一舰队、不带潜艇；要改就显式传 fleet1/fleet2/submarine。
            cfg.Fleet_Fleet1 = int(args.get('fleet1', 1))
            cfg.Fleet_Fleet2 = int(args.get('fleet2', 0))
            cfg.Submarine_Fleet = int(args.get('submarine_fleet', 0))
    except Exception as e:
        return {'error': f'配置章节绑定失败: {type(e).__name__}: {e}', 'stage': stage,
                'traceback_tail': traceback.format_exc().strip().splitlines()[-8:]}
    try:
        from module.campaign.run import CampaignRun
        package, folder, name = chapter.split('.')
        if package != 'campaign':
            raise ValueError('章节必须是 campaign.<folder>.<module>')
        # 复用上游加载器：它先 deepcopy 账号配置，再合并该模块的 Config
        # （包括继承的 Config），最后构造 Campaign。直接构造会丢失地图规则。
        cfg.override(Campaign_Name=name, Campaign_Event=folder)
        if mode is not None:
            cfg.override(Campaign_Mode=mode)
        loader = CampaignRun(config=cfg, device=dev)
        loader.load_campaign(name, folder=folder)
        inst = loader.campaign
    except Exception as e:
        return {'error': f'实例化失败: {type(e).__name__}: {e}', 'chapter': chapter,
                'traceback_tail': traceback.format_exc().strip().splitlines()[-8:]}
    # **种一帧**：ALAS 的方法假定 `device.image` 已存在，而它只在 screenshot() 之后才有。
    # 少了这一步，第一个动作就会死在 `AttributeError: 'Device' object has no attribute 'image'`
    # （实测踩过）。顺带也预热了截图后端。
    seeded = None
    try:
        import time as _t
        t0 = _t.time()
        from s3_campaign_entry import clear_campaign_device_records
        clear_campaign_device_records(inst)
        dev.screenshot()
        seeded = round((_t.time() - t0) * 1000, 1)
    except Exception as e:
        return {'error': f'初始化截图失败: {type(e).__name__}: {e}', 'chapter': chapter,
                'traceback_tail': traceback.format_exc().strip().splitlines()[-8:]}
    _CAMPAIGN['obj'] = inst
    _CAMPAIGN['chapter'] = chapter
    _CAMPAIGN['loader'] = loader
    return {'chapter': chapter, 'instantiated': True, 'frame_seeded_ms': seeded,
            'campaign_mode': inst.config.Campaign_Mode,
            'mro': [c.__name__ for c in type(inst).__mro__[:8]]}


def op_s3_campaign_info(args):
    """报告当前 Campaign 实例的状态（只读，不碰游戏状态）。"""
    inst = _CAMPAIGN.get('obj')
    if inst is None:
        return {'initialized': False}
    out = {'initialized': True, 'chapter': _CAMPAIGN.get('chapter')}
    try:
        mp = getattr(inst, 'MAP', None)
        if mp is not None:
            shape = getattr(mp, 'shape', None)
            out['map_shape'] = [int(v) for v in shape] if shape is not None else None
            md = getattr(mp, 'map_data', None)
            if isinstance(md, str):
                out['map_rows'] = len([x for x in md.strip().split('\n') if x.strip()])
        out['battle_methods'] = sorted(m for m in dir(inst)
                                       if m.startswith('battle_') or m.startswith('clear_'))
        cfg = getattr(inst, 'config', None)
        if cfg is not None:
            out['config_task'] = str(getattr(cfg, 'task', ''))
            out['campaign_name'] = str(getattr(cfg, 'Campaign_Name', ''))
            out['screenshot_method'] = str(getattr(cfg, 'Emulator_ScreenshotMethod', ''))
        dev = getattr(inst, 'device', None)
        if dev is not None:
            out['device'] = str(getattr(dev, 'serial', ''))
        # **关卡进度**：这是"这张图到底清了没有"的**唯一可信判据** —— 它来自游戏自己在
        # 关卡信息面板上写的字（威胁排除 % + 三个星级条件），由上游 `map_get_info()` 读入。
        # 相对地：`campaign_end` / 我自己数过的 `enemies_left` 都被实测证伪过（前者连
        # withdraw 路径也返回 True ✗）。字段只有在上游读过面板之后才有意义。
        prog = {}
        for k in ('map_clear_percentage', 'map_achieved_star_1', 'map_achieved_star_2',
                  'map_achieved_star_3', 'map_is_100_percent_clear', 'map_is_3_stars',
                  'map_is_threat_safe', 'map_has_clear_mode', 'map_clear_percentage_prev'):
            try:
                v = getattr(inst, k, None)
                if isinstance(v, float):
                    v = round(v, 4)
                prog[k] = v
            except Exception:
                pass
        try:
            prog['map_clear_pct'] = round(float(inst.map_clear_percentage) * 100, 1)
        except Exception:
            pass
        cfg = getattr(inst, 'config', None)
        if cfg is not None:
            for k in ('MAP_CLEAR_ALL_THIS_TIME', 'POOR_MAP_DATA',
                      'StopCondition_MapAchievement', 'STAR_REQUIRE_3'):
                try:
                    prog[k] = getattr(cfg, k, None)
                except Exception:
                    pass
        out['map_progress'] = prog
    except Exception as e:
        out['info_error'] = f'{type(e).__name__}: {e}'
    return out


def op_s3_campaign_call(args):
    """调用 Campaign 实例上的方法（支持点号路径，如 `device.screenshot`）。

    **安全联锁**：方法名以 `_DANGER_PREFIX` 里任一前缀开头时，必须显式传
    `allow_actions=true` 才会执行；否则返回拒绝理由。默认只允许只读/观察类调用。
    """
    inst = _CAMPAIGN.get('obj')
    if inst is None:
        return {'error': '尚未初始化，先调 s3_campaign_init'}
    name = str(args.get('name') or '')
    if not name:
        return {'error': '缺少 name'}
    leaf = name.split('.')[-1]
    if leaf.startswith(_DANGER_PREFIX) and not args.get('allow_actions'):
        return {'refused': True, 'name': name,
                'reason': f'`{leaf}` 属于会驱动作战/改游戏状态的方法；'
                          '确需执行请显式传 allow_actions=true'}
    import time
    target = inst
    try:
        parts = name.split('.')
        for p in parts[:-1]:
            target = getattr(target, p)
        fn = getattr(target, parts[-1])
    except Exception as e:
        return {'error': f'取不到 {name}: {type(e).__name__}: {e}'}
    if not callable(fn):
        return {'name': name, 'callable': False, 'value': json_default(fn)}
    # `@name` 形式的参数解析成实例属性（例如 enter_map 需要 self.ENTRANCE 这种对象，
    # JSON 传不过来）。这是给"调用上游方法"留的必要通道，不做任何游戏状态改写。
    raw_args = args.get('args') or []
    call_args = []
    for a in raw_args:
        if isinstance(a, str) and a.startswith('@'):
            call_args.append(getattr(inst, a[1:]))
        else:
            call_args.append(a)
    t0 = time.time()
    from s3_campaign_outcome import observe_battle_result, classify_campaign_end
    try:
        with observe_battle_result(inst) as _result_evidence:
            value = fn(*call_args)
    except Exception as e:
        # CampaignEnd also comes from withdraw() -> handle_in_stage(). Preserve
        # its execution source; returning to the stage page alone is not a win.
        try:
            from module.exception import CampaignEnd as _CE
            _is_end = isinstance(e, _CE)
        except Exception:
            _is_end = type(e).__name__ == 'CampaignEnd'
        if _is_end:
            end = classify_campaign_end(e, _result_evidence)
            inst._s3_last_end = end
            return {'name': name, 'ms': round((time.time() - t0) * 1000, 1), **end}
        # **带上调用栈尾部**：上游内部抛错时，只回 `类型: 消息` 会丢掉定位信息
        # （实测 `execute_a_battle` 报 KeyError: () 时，栈是唯一线索 ✗）。
        return {'name': name, 'ms': round((time.time() - t0) * 1000, 1),
                'error': f'{type(e).__name__}: {e}',
                'traceback_tail': [ln.strip()[:110] for ln in
                                   traceback.format_exc().strip().splitlines()[-8:]]}
    out = {'name': name, 'ms': round((time.time() - t0) * 1000, 1)}
    # `store='ATTR'`：把返回值写回实例属性。上游很多方法**靠返回值**传递对象
    # （例如 `campaign_get_entrance('1-1')` 返回的 Button 要赋给 `self.ENTRANCE`，
    #  类默认的 ENTRANCE 是空 Button → 直接传它会报 `not enough values to unpack`，实测踩过）。
    if args.get('store'):
        try:
            setattr(inst, str(args['store']), value)
            out['stored'] = str(args['store'])
        except Exception as e:
            out['store_error'] = f'{type(e).__name__}: {e}'
    try:
        out['value'] = json_default(value)
    except Exception:
        out['value_repr'] = str(value)[:200]
    return out




def op_s3_run_plan(args):
    """按完整模块名读取规则元数据，并用上游 Campaign 调度战斗。

    IR 的 calls 是 AST 语义轨迹，不是可重放的调用序列。真实运行复用
    Campaign.MAP、Config、继承的钩子和 execute_a_battle 的计数分派。
    plan_complete=false 仅表示 JSON 导出不完整，原生 Campaign 仍可执行。

    安全设计：
      - `dry_run` **默认 true**：只读本章 IR，不初始化 Campaign/设备；
      - 真跑必须 `allow_actions=true`（与 s3_campaign_call 同一把锁）；
      - `max_seconds` 在上游操作边界检查；任一步报错立即停；
      - 缺少本章 IR 或来源不匹配时，在初始化设备前报错；不按同名章节兜底。

    注意：必须在**同一个进程**里完成 init → enter_map → map_init → 各调用，
    否则 `self.map` 等状态会丢（实测：换进程调用报 `'Campaign' object has no attribute 'map'`）。
    """
    import time as _t
    mode = _campaign_mode(args)
    chapter = str(args.get('chapter') or 'campaign.campaign_main.campaign_2_1')
    dry = bool(args.get('dry_run', True))
    from sortie_contract import stamp
    if not dry and not args.get('allow_actions'):
        return stamp({'refused': True, 'dry_run': dry, 'chapter': chapter,
                      'outcome': 'refused',
                      'reason': '真跑需要 allow_actions=true（dry_run 默认可离线校验）'})
    from campaign_rules import CampaignRuleError, load_campaign_rules
    try:
        out = load_campaign_rules(chapter)
    except CampaignRuleError as exc:
        return stamp({'chapter': chapter, 'dry_run': dry, 'stage': 'rules',
                      'outcome': 'error', 'error': str(exc), 'error_code': exc.code,
                      'failure': {'step': 'load_campaign_rules', 'error': str(exc),
                                  'traceback_tail': traceback.format_exc()
                                  .strip().splitlines()[-8:], 'frame': None}})
    stage = out['stage']
    out['dry_run'] = dry
    out['requested_mode'] = mode
    if dry:
        out['note'] = ('dry_run：只读取规则，未初始化 Campaign 或设备。'
                       'plan_steps 是已导出的战斗方法，calls 是语义轨迹；'
                       '真跑由上游 execute_a_battle 结合战斗计数和 MAP 规则分派，'
                       '不重放 JSON，IR 不完整不代表原生 Campaign 不可执行。')
        return stamp(out)

    # 舰队选择也要能由调用方指定（不同账号/关卡要用不同舰队；此前只走 init 的默认值）。
    init = op_s3_campaign_init({'chapter': chapter,
                                'mode': mode,
                                'serial': args.get('serial'),
                                'screenshot': args.get('screenshot'),
                                'control': args.get('control'),
                                'fleet1': args.get('fleet1', 1),
                                'fleet2': args.get('fleet2', 0),
                                'submarine_fleet': args.get('submarine_fleet', 0),
                                # 战斗流程二选一：False=BOSS 一刷出来就打（默认）；
                                # True=先清光小怪再打 BOSS（上游 MAP_CLEAR_ALL_THIS_TIME 分支）
                                'clear_all': args.get('clear_all', False),
                                # 心情模式（低心情强制出击弹窗的开关，默认 calculate_ignore）
                                'emotion_mode': args.get('emotion_mode')})
    if init.get('error'):
        # 初始化失败也是结果：按合同给出 `error` + 调用栈尾部，别让它只剩一句话。
        out.update({'error': init['error'], 'stage': 'init', 'outcome': 'error',
                    'failure': {'step': 's3_campaign_init', 'error': init['error'],
                                'traceback_tail': list(init.get('traceback_tail') or []),
                                'frame': None}})
        return stamp(out)
    inst = _CAMPAIGN.get('obj')
    out['campaign_mode'] = inst.config.Campaign_Mode
    stage = _CAMPAIGN['loader'].stage
    out['stage'] = stage

    # ---- 真跑 ----
    t_start = _t.time()
    max_s = float(args.get('max_seconds') or 1500)
    steps = []
    from s3_campaign_entry import clear_campaign_device_records, prepare_campaign_navigation
    from s3_campaign_outcome import finalize_sortie_result

    def preparation_failed(name, error):
        steps.append({'step': name, 'error': f'{type(error).__name__}: {error}',
                      'traceback_tail': traceback.format_exc().strip().splitlines()[-8:]})
        out['steps'] = steps
        out['elapsed_s'] = round(_t.time() - t_start, 1)
        return finalize_sortie_result(out, steps)

    # CampaignRun.run() resets the reused device before navigation, and leaves
    # any previous map first. A withdrawal's CampaignEnd is cleanup, not victory.
    try:
        prepared = prepare_campaign_navigation(inst)
        steps.append({'step': 'prepare_campaign_navigation', **prepared})
    except Exception as error:
        return preparation_failed('prepare_campaign_navigation', error)
    # 上游负责主线/困难/活动的页面、章节和模式选择，并设置 ENTRANCE。
    # 手工拆 ensure_chapter/get_entrance 会绕过活动基类的导航钩子。
    r = op_s3_campaign_call({'name': 'ensure_campaign_ui',
                             'args': [stage, inst.config.Campaign_Mode],
                             'allow_actions': True})
    ui_step = {'step': 'ensure_campaign_ui', 'ms': r.get('ms'), 'error': r.get('error')}
    if r.get('campaign_end'):
        # 导航期间上游自己调了 `withdraw()`（客户端状态残留时会发生）：这是"上一局/客户端
        # 状态"的清理，不是本局结论，但**必须留在证据里**。归档日志里 2026-09-23 02:10:37
        # 那次就被静默吞掉了 —— 事后只能靠原始日志猜（见 docs/archive/reports/result-evidence.md）。
        ui_step['navigation_end'] = r.get('outcome')
        ui_step['navigation_withdrawn'] = bool((r.get('end_evidence') or {}).get('withdrawn'))
    steps.append(ui_step)
    out['campaign_mode'] = inst.config.Campaign_Mode
    if r.get('error'):
        out['steps'] = steps
        out['elapsed_s'] = round(_t.time() - t_start, 1)
        return finalize_sortie_result(out, steps)

    # Match the second reset immediately before CampaignRun calls campaign.run.
    try:
        clear_campaign_device_records(inst)
        steps.append({'step': 'prepare_campaign_run'})
    except Exception as error:
        return preparation_failed('prepare_campaign_run', error)

    # The original enter_map and native popup handlers own all game actions.
    # Do not race that flow with an independent screenshot/tap worker.
    from s3_campaign_execution import run_native_campaign
    native_kwargs = dict(
        max_rounds=int(args.get('max_rounds') or 20)
        if args.get('repeat_until_cleared', True) else 1,
        max_seconds=max_s, stop_after=args.get('stop_after'), withdraw_file=args.get('withdraw_file'),
        battle_count=args.get('battle_count'))
    if args.get('artifact_dir'):
        native_kwargs['artifact_dir'] = args['artifact_dir']
    with campaign_button_color_compat():
        result = run_native_campaign(inst, **native_kwargs)
    out.update(result)
    out['campaign_mode'] = inst.config.Campaign_Mode
    out['steps'] = steps + result['steps']
    out['elapsed_s'] = round(_t.time() - t_start, 1)
    return out



def _map_config(chapter=None):
    """S2 需要上游配置（`DETECTION_BACKEND` 等决定用 Homography 还是 Perspective 后端）。
    做法与 cached_rule_check 一致：用上游自己的 AzurLaneConfig，不自己造配置层。"""
    from module.config.config import AzurLaneConfig
    # Follow the account that owns the session connection, but reload its native
    # config so one-shot overrides from earlier tasks cannot leak into this map.
    instance = _DEVICE_OBJ.config.config_name if _DEVICE_OBJ is not None else 'alas'
    cfg = AzurLaneConfig(instance)
    if chapter:
        import copy
        module = importlib.import_module(chapter)
        cfg = copy.deepcopy(cfg).merge(module.Config())
    return cfg


def op_map_detection_assets(args):
    """S2 的静态素材链：UI 遮罩 / 瓦片模板 / 检测区域。

    只加载、不算图。用途是**把"素材缺失/路径不对"提前暴露**：地图识别依赖
    MASK_UI、MASK_OS_MAP_UI、TILE_CENTER、TILE_CORNER 这几份素材，它们读不出来时，
    真机跑地图会以很难懂的方式失败（Hough/匹配全空）。
    """
    from module.map_detection.utils_assets import Assets, DETECTING_AREA

    def shape_of(v):
        sh = getattr(v, 'shape', None)
        if sh is not None:
            return [int(x) for x in sh]
        if isinstance(v, (list, tuple)):
            return [len(v)]
        return str(type(v).__name__)

    a = Assets()
    out = {'detecting_area': [int(v) for v in DETECTING_AREA]}
    for name in ('ui_mask', 'ui_mask_os', 'ui_mask_stroke', 'ui_mask_in_map',
                 'ui_mask_os_in_map', 'tile_center_image', 'tile_corner_image'):
        try:
            out[name] = shape_of(getattr(a, name))
        except Exception as e:
            out[name] = f'{type(e).__name__}: {e}'
    return out


def op_map_detect(args):
    """战役地图识别（S2 主路径）：`View.load(image)` → `predict()`。

    返回检测出的网格规模与四边标志。非地图画面是有效负样本。
    """
    import module.map_detection.view as view_mod
    image = _require_image()
    apply_numpy2_compat()
    apply_points_empty_compat()
    mode = str(args.get('mode') or 'main')
    cfg = _map_config(args.get('chapter'))
    if mode == 'os':
        from module.os.config import OSConfig
        cfg = cfg.merge(OSConfig())
    # 上游有两个检测后端（Homography / Perspective），由 config.DETECTION_BACKEND 选。
    # 允许显式指定：真机上出现过 homography 后端"找不到水平线/垂直线"而画面明明有网格，
    # 这时要能立刻对比另一个后端，而不是猜。
    if args.get('backend'):
        cfg.DETECTION_BACKEND = args['backend']
    out = {'backend': str(getattr(cfg, 'DETECTION_BACKEND', ''))}
    # 作业海域（OS）的地图要用另一套遮罩：View(config, mode='os') 会切到
    # ASSETS.ui_mask_os_in_map（view.py:47-48），网格类也换成 OS 的。
    out['mode'] = mode
    if mode == 'os':
        from module.os_handler.enemy_searching import EnemySearchingHandler
        out['in_map'] = bool(EnemySearchingHandler.is_in_map(_make_main_shim(image)))
    try:
        if mode == 'os':
            # OS 模式需要两半，缺一不可：
            #   1) Perspective.load_image 写死了战役遮罩 → 用垫片换成 ui_mask_os（找四角那步）
            #   2) homography.py:67-72 的 warp 后遮罩按 Scheduler_Command 是否以 Opsi 开头切换
            #      → 不设它的话，四角算对了、自由格搜索照样全败
            apply_os_mask_compat()
            set_os_mask_mode(True)
            try:
                cfg.Scheduler_Command = 'OpsiDaily'
            except Exception:
                pass
            grid_class = None
            try:
                import module.map_detection.os_grid as os_grid_mod
                for name in ('OSGrid', 'OSGridInfo'):
                    if hasattr(os_grid_mod, name):
                        grid_class = getattr(os_grid_mod, name)
                        break
            except Exception:
                grid_class = None
            out['grid_class'] = getattr(grid_class, '__name__', None)
            v = view_mod.View(cfg, mode='os', grid_class=grid_class) if grid_class \
                else view_mod.View(cfg, mode='os')
        else:
            v = view_mod.View(cfg)
    except Exception as e:
        out['construct_error'] = f'{type(e).__name__}: {e}'
        if mode == 'os':
            set_os_mask_mode(False)
        return out
    for name in ('left_edge', 'right_edge', 'upper_edge', 'lower_edge'):
        if hasattr(v, name):
            out[name] = bool(getattr(v, name))
    # MapDetectionError 是上游识别不到网格的正常负样本；其他异常是执行错误。
    from module.map_detection.view import MapDetectionError

    try:
        v.load(image)
        out['load'] = 'ok'
        out['threshold_used'] = int(cfg.INTERNAL_LINES_HOUGHLINES_THRESHOLD)
    except MapDetectionError as e:
        out['load'] = 'negative'
        out['detected'] = False
        out['reason'] = f'{type(e).__name__}: {e}'
        return out
    except Exception as e:
        out['load'] = 'error'
        out['detected'] = False
        out['reason'] = f'{type(e).__name__}: {e}'
        return out
    finally:
        set_os_mask_mode(False)
    if hasattr(v, 'predict'):
        try:
            v.predict()
            out['predict'] = 'ok'
        except Exception as e:
            out['predict'] = f'{type(e).__name__}: {e}'
            out['detected'] = False
            out['reason'] = out['predict']
            grids = getattr(v, 'grids', None)
            if isinstance(grids, dict):
                out['grid_count'] = len(grids)
            return out
    for name in ('shape', 'center_loca', 'center_offset'):
        if hasattr(v, name):
            val = getattr(v, name)
            out[name] = val.tolist() if hasattr(val, 'tolist') else val
    grids = getattr(v, 'grids', None)
    if isinstance(grids, dict):
        out['grid_count'] = len(grids)
        # 检出网格的坐标：用来回答"为什么格数比地图声明的少"——
        # 左侧舰队栏 / 顶部信息条被遮罩盖住的那几格本来就不该检出。
        try:
            keys = sorted((int(k[0]), int(k[1])) for k in grids.keys())
            out['grid_keys'] = [list(k) for k in keys]
            if keys:
                out['grid_bounds'] = [min(k[0] for k in keys), min(k[1] for k in keys),
                                      max(k[0] for k in keys), max(k[1] for k in keys)]
        except Exception as e:
            out['grid_keys_error'] = f'{type(e).__name__}: {e}'
        # 逐格语义（只回 True 的标志，省得 JSON 爆炸）。这是"网格判定"的实质内容：
        # 敌人/舰队/BOSS 落在哪一格。可与关卡 IR 的 map_data 对照——
        # 敌人只应出现在 IR 允许的格子上（ME/MS/MB/MM），坐标错一格就会露馅。
        try:
            flags = {}
            for (x, y), g in grids.items():
                names = [n for n in (
                    'is_enemy', 'is_boss', 'is_siren', 'is_fleet', 'is_current_fleet',
                    'is_submarine', 'may_enemy', 'may_boss', 'may_siren', 'may_mystery',
                    'may_ammo', 'is_spawn_point', 'is_submarine_spawn_point', 'is_land',
                    'is_portal', 'is_mystery', 'is_ammo', 'is_cleared',
                ) if bool(getattr(g, n, False))]
                if names:
                    flags['%d,%d' % (int(x), int(y))] = names
            out['grid_flags'] = flags
        except Exception as e:
            out['grid_flags_error'] = f'{type(e).__name__}: {e}'
    out['detected'] = bool(out.get('grid_count'))
    grid = getattr(v, 'grids', None) or getattr(v, 'grid', None)
    if grid is not None:
        out['grid_shape'] = [int(x) for x in getattr(grid, 'shape', [])] \
            or str(type(grid).__name__)
    out['detected'] = bool(getattr(v, '_detected', False)) or 'grid_shape' in out

    # ---- 战场判据：**地图上必定有船**
    # 实测（docs/archive/history/device-engine.md "误报"一节）：战役章节选择页会把章节预览图误判成地图
    # （10 帧里 6 帧误报），而那些误报帧的逐格标志**全为 0**；四张真地图的船标志都 ≥3。
    # 道理直白：在战斗中画面上不可能没有己方舰队。所以"检出网格 + 至少一个船标志"
    # 才算真的在地图上；`detected_raw` 保留原判，便于诊断时看到底层检出。
    SHIP_FLAGS = ('is_enemy', 'is_boss', 'is_siren', 'is_fleet',
                  'is_current_fleet', 'is_submarine')
    ships = {}
    for key, names in (out.get('grid_flags') or {}).items():
        if any(n in names for n in SHIP_FLAGS):
            ships[key] = names
    out['detected_raw'] = out['detected']
    if out.get('grid_flags_error'):
        # 网格已检出，但逐格语义不可用；不要把“未知”编码成 0 艘船。
        out['ships'] = None
        out['ship_tiles'] = None
    else:
        out['ships'] = len(ships)
        out['ship_tiles'] = ships
    # 注意：**只在战役模式（main）下用这条判据**。海域图（mode=os）的逐格标志在本客户端
    # 本来就为空（OS 网格类的模板与本客户端图标不匹配，S2 阶段已查明并记录），
    # 若一并要求船标志会把**整类海域图误杀** —— 实测产品路径因此从 5/5 掉到 4/5。
    if (bool(args.get('require_ships', True)) and mode != 'os'
            and not out.get('grid_flags_error') and not ships and out['detected']):
        out['detected'] = False
        out['reason'] = ('检出网格但**没有任何船标志**（%s 格），判为非战场画面'
                         % out.get('grid_count'))

    if mode == 'os' and not out['in_map']:
        out['detected'] = False
        out['reason'] = '上游 OSMap.is_in_map 判定当前画面不在海域地图'

    return out


def op_globe_detect(args):
    """大世界（OS）地图识别：上游 `GlobeDetection.load(image)` 对大 globe 模板求单应性。

    返回单应矩阵与"屏幕点 → 大世界坐标 → 回屏幕"的**往返结果**：
    同一变换的逆，误差应≈0 —— 这是不需要真机地图也能做的数值自检。
    """
    import module.os.globe_detection as gd
    image = _require_image()
    apply_numpy2_compat()
    cfg = _map_config()
    try:
        det = gd.GlobeDetection(cfg)
    except TypeError:
        det = gd.GlobeDetection()
    out = {}
    # 位置检测的结果就在上游对象上：`load()` 内部把截图做透视变换、与 globe 模板
    # `cv2.matchTemplate`，落点写进 `self.center_loca`（日志里叫 globe_center）；
    # 匹配度 similarity 只打日志、不存属性 —— 所以这里挂一个 logging handler
    # 把**上游自己打的那行**取回来，而不是在 op 里把匹配重算一遍。
    import logging

    class _Cap(logging.Handler):
        def __init__(self):
            super().__init__()
            self.messages = []

        def emit(self, record):
            try:
                self.messages.append(record.getMessage())
            except Exception:
                pass

    cap = _Cap()
    try:
        from module.logger import logger as _alas_logger
        _alas_logger.addHandler(cap)
    except Exception:
        cap = None
    try:
        det.load(image)
        out['load'] = 'ok'
        # 检测到的位置（大世界坐标）
        loca = getattr(det, 'center_loca', None)
        if loca is not None:
            try:
                out['center_loca'] = [float(v) for v in loca]
            except Exception:
                out['center_loca'] = str(loca)
        if cap is not None:
            import re as _re
            # 上游日志行开头的耗时（`0.080s`）每次运行都不同，而它不是证据：
            # 不归一掉的话，生成的 `docs/archive/reports/map-detection.md` 每跑一次都会多出一个纯计时 diff，
            # 既污染工作区，也会把真正的改动淹掉。相似度等实质内容一律保留。
            timing = _re.compile(r'^\d+(?:\.\d+)?s\s+')
            lines = [m for m in cap.messages
                     if 'similarity' in m or 'globe_center' in m or 'homo_storage' in m]
            out['log_lines'] = [timing.sub('', str(m).strip()) for m in lines][-6:]
            for m in lines:
                if 'similarity' in m:
                    import re as _re
                    nums = _re.findall(r'[0-9]*\.?[0-9]+', str(m))
                    if nums:
                        try:
                            out['similarity'] = float(nums[-1])
                        except Exception:
                            pass
    except Exception as e:
        out['load'] = f'{type(e).__name__}: {e}'
        return out
    finally:
        if cap is not None:
            try:
                from module.logger import logger as _alas_logger2
                _alas_logger2.removeHandler(cap)
            except Exception:
                pass
    homo = getattr(det, 'homography', None)
    if homo is not None:
        size = getattr(homo, 'homo_size', None)
        data = getattr(homo, 'homo_data', None)
        out['homo_size'] = [int(v) for v in size] if size is not None else None
        out['homo_data'] = data.tolist() if hasattr(data, 'tolist') else None
    pts = args.get('points') or [[640, 360], [200, 200]]
    try:
        g = det.screen2globe(pts)
        out['screen2globe'] = g.tolist() if hasattr(g, 'tolist') else g
        back = det.globe2screen(g)
        out['globe2screen'] = back.tolist() if hasattr(back, 'tolist') else back
    except Exception as e:
        out['roundtrip_error'] = f'{type(e).__name__}: {e}'
    return out


def op_map_detect_trace(args):
    """逐步跑上游的地图检测链，定位"画面明明有网格，却报 No vertical line detected"卡在哪一步。

    上游 `Perspective.load` 的链条是：
      load_image(预处理) → detect_lines ×4（inner/edge × 横/竖）
   而 `detect_lines` 里会 **与 `ui_mask_stroke` 相与**——遮罩把"属于 UI 的像素"置零，
    再从剩下的峰值图跑 HoughLines。所以最可能的失效点是遮罩与本客户端 UI 对不上
    （整片地图被遮掉 → 峰值全零 → 找不到线）。
    """
    import numpy as np
    import module.map_detection.perspective as persp
    from module.map_detection.utils_assets import Assets

    image = _require_image()
    apply_numpy2_compat()
    cfg = _map_config(args.get('chapter'))
    p = persp.Perspective(config=cfg)
    out = {}
    try:
        img = p.load_image(image)
    except Exception as e:
        return {'load_image_error': f'{type(e).__name__}: {e}'}
    out['load_image'] = {'shape': list(img.shape), 'mean': round(float(img.mean()), 2),
                         'nonzero_pct': round(float((img > 0).mean()) * 100, 2)}
    mask = Assets().ui_mask_stroke
    out['mask_stroke'] = {'shape': list(mask.shape),
                          'nonzero_pct': round(float((mask > 0).mean()) * 100, 2)}
    out['detecting_area'] = [int(v) for v in cfg.DETECTING_AREA]

    pa = cfg.DETECTING_AREA
    calls = [
        ('inner_h', True, 'INTERNAL_LINES_FIND_PEAKS_PARAMETERS',
         'INTERNAL_LINES_HOUGHLINES_THRESHOLD', 'HORIZONTAL_LINES_THETA_THRESHOLD', 0),
        ('inner_v', False, 'INTERNAL_LINES_FIND_PEAKS_PARAMETERS',
         'INTERNAL_LINES_HOUGHLINES_THRESHOLD', 'VERTICAL_LINES_THETA_THRESHOLD', 0),
        ('edge_h', True, 'EDGE_LINES_FIND_PEAKS_PARAMETERS',
         'EDGE_LINES_HOUGHLINES_THRESHOLD', 'HORIZONTAL_LINES_THETA_THRESHOLD', pa[2] - pa[0]),
        ('edge_v', False, 'EDGE_LINES_FIND_PEAKS_PARAMETERS',
         'EDGE_LINES_HOUGHLINES_THRESHOLD', 'VERTICAL_LINES_THETA_THRESHOLD', pa[3] - pa[1]),
    ]
    for label, horiz, pname, tname, thname, pad in calls:
        entry = {}
        try:
            peaks = p.find_peaks(img, is_horizontal=horiz, param=getattr(cfg, pname), pad=pad)
            entry['peaks_raw'] = int((peaks > 0).sum())
            if peaks.shape == mask.shape:
                entry['peaks_after_mask'] = int(((peaks & mask) > 0).sum())
            else:
                entry['peaks_after_mask'] = f'形状不匹配 peaks{list(peaks.shape)} vs mask{list(mask.shape)}'
            lines = p.hough_lines(peaks, horiz, getattr(cfg, tname), getattr(cfg, thname))
            entry['lines'] = len(lines)
            # HoughLines 的**原始**输出与角度分布：用来区分"Hough 什么都没找到"与
            # "Hough 找到了但被角度过滤器全滤掉"（后者意味着本客户端的地图几何
            # 与上游预期的透视梯形不一致）。
            import cv2
            raw = cv2.HoughLines(peaks, 1, np.pi / 180, int(getattr(cfg, tname)))
            entry['hough_raw'] = 0 if raw is None else int(len(raw))
            if raw is not None and len(raw):
                deg = np.rad2deg(raw[:, 0, 1])
                entry['hough_theta_deg'] = [round(float(deg.min()), 2), round(float(deg.max()), 2)]
            entry['line_params'] = {'hough_threshold': int(getattr(cfg, tname)),
                                    'theta_threshold': float(getattr(cfg, thname)),
                                    'pad': int(pad)}
        except Exception as e:
            entry['error'] = f'{type(e).__name__}: {e}'
        out[label] = entry
    return out


def op_s3_probe_view(args):
    """**只读**探针：当前帧里上游检测出多少格、信息条在不在、相机在哪。

    为什么需要：7-1 那类"进图后空转"的现场，日志只能反推出"视图格数不足"（12–15 格 vs 应有 24 格），
    但看不到**当时**的信息条状态与相机。本 op 用上游自己的检测（`update()` → `View.load` + `predict`）
    把这些值直接量出来，配合 `s3_run_plan(stop_after='map_init')` 就能把"进图/识别"这一段单独定位。

    不改游戏状态（只截图 + 识别），所以不需要 allow_actions。
    """
    inst = _CAMPAIGN.get('obj')
    if inst is None:
        return {'error': '尚未初始化，先调 s3_campaign_init'}
    out = {'chapter': _CAMPAIGN.get('chapter')}
    try:
        out['in_map'] = bool(inst.is_in_map())
    except Exception as e:
        out['in_map'] = f'{type(e).__name__}: {e}'
    try:
        out['info_bar_count'] = int(inst.info_bar_count())
    except Exception as e:
        out['info_bar_count'] = f'{type(e).__name__}: {e}'
    try:
        inst.device.screenshot()
        inst.update()                      # 截图 → View.load → predict（含相机修正）
    except Exception as e:
        out['update_error'] = f'{type(e).__name__}: {e}'
    try:
        v = inst.view
        out['view_cells'] = len(v.grids)
        out['view_shape'] = [int(x) + 1 for x in v.shape]
        out['view_show'] = [' '.join([v[(x, y)].str if (x, y) in v else '..'
                                      for x in range(int(v.shape[0]) + 1)])
                            for y in range(int(v.shape[1]) + 1)]
        out['camera'] = [int(x) for x in inst.camera]
    except Exception as e:
        out['view_error'] = f'{type(e).__name__}: {e}'
    # 地图侧的标志（累积记忆）：看清"上游认为还剩什么"
    for key in ('is_enemy', 'is_boss', 'is_fleet', 'is_current_fleet', 'is_mystery',
                'may_boss'):
        try:
            grids = inst.map.select(**{key: True})
            out[key] = [str(g) for g in grids]
        except Exception:
            pass
    try:
        out['battle_count'] = getattr(inst, 'battle_count', None)
        out['ammo_count'] = getattr(inst, 'ammo_count', None)
    except Exception:
        pass
    return out


def op_map_grids(args):
    """逐格 dump 当前画面的**预测标志**与「BOSS 图标判据」的分数。

    为什么需要：上游认 BOSS 的形状模板 `TEMPLATE_ENEMY_BOSS` 其实就是**BOSS 图标那对
    发光眼睛**的形状，但 `grid_predictor.predict_boss()` 是
    `color_similarity_2d(crop, (255,77,82))` **先转成"与红色有多像"再匹配形状**
    （grid_predictor.py:231-232）—— 也就是说：眼睛不是红色时，形状再对也认不出来，
    而失败表现恰好是用户看到的"全清完小怪、只剩 BOSS，上游却找不到 BOSS 就撤退"
    （`Full scan find boss.` → `No boss found.`）。

    本 op 用**同一帧**对每格同时算多种判据的相似度，从而判定：
      - 是颜色不对（换成实际颜色就能分开 BOSS 与普通格），还是形状/位置也不对；
      - 阈值该定在哪（BOSS 格分数 vs 其余格最高分之间的间隔）。

    args:
        colors: [[名字, r, g, b], ...] 候选颜色，默认含上游的红色与几种蓝色；
        icons:  True 时把每格的裁剪块与相似度图拼成一张图存盘（便于肉眼看）。
    """
    import cv2
    import numpy as np
    import module.map_detection.view as view_mod
    import module.map_detection.grid_predictor as gp_mod
    from module.base.utils import color_similarity_2d, rgb2luma

    image = _require_image()
    apply_numpy2_compat()
    apply_points_empty_compat()
    # 报告**生产行为**：带上 BOSS 眼睛颜色垫片，所以 `is_boss` 这一列就是修好之后的结果；
    # 逐色的 score_* 则保留原样，用来判断阈值余量。
    apply_boss_icon_color_compat()
    apply_in_map_threshold_compat()
    cfg = _map_config(args.get('chapter'))
    v = view_mod.View(cfg)
    v.load(image)
    v.predict()

    # 上游的 BOSS 判据作用在这个区域上（相对格子坐标，格心为 0，格边为 ±1）。
    area = tuple(args.get('area') or (-0.55, -0.2, 0.45, 0.2))
    shape = tuple(args.get('shape') or (50, 20))
    tpl = gp_mod.TEMPLATE_ENEMY_BOSS
    tpl_img = tpl.image
    if getattr(tpl, 'is_gif', False):
        tpl_img = tpl_img[0]
    tpl_gray = cv2.cvtColor(tpl_img, cv2.COLOR_RGB2GRAY) if tpl_img.ndim == 3 else tpl_img

    def _score(sim):
        if sim.ndim == 3:
            sim = cv2.cvtColor(sim, cv2.COLOR_RGB2GRAY)
        res = cv2.matchTemplate(sim.astype(np.uint8), tpl_gray, cv2.TM_CCOEFF_NORMED)
        return float(cv2.minMaxLoc(res)[1])

    colors = args.get('colors') or [
        ['upstream_red', 255, 77, 82],
        ['blue_rbswap', 82, 77, 255],
        ['blue_6080ff', 96, 128, 255],
    ]

    out = {'shape': [int(x) + 1 for x in v.shape], 'grid_count': len(v.grids),
           'tpl_shape': list(tpl_img.shape), 'area': list(area), 'shape_crop': list(shape),
           'grids': []}
    grid_out = {}
    for loca, g in v.grids.items():
        item = {'loca': [int(x) for x in loca], 'str': str(g.str),
                'is_enemy': bool(g.is_enemy), 'is_boss': bool(g.is_boss),
                'is_siren': bool(g.is_siren), 'is_fleet': bool(g.is_fleet),
                'enemy_scale': int(g.enemy_scale), 'enemy_genre': str(g.enemy_genre)}
        try:
            crop = g.relative_crop(area, shape=shape)
        except Exception as e:
            item['crop_error'] = f'{type(e).__name__}: {e}'
            out['grids'].append(item)
            continue
        try:
            item['rgb_mean'] = [round(float(x), 1) for x in
                                np.array(crop).reshape(-1, 3).mean(axis=0)]
        except Exception:
            pass
        for name, r, gg, b in colors:
            sim = color_similarity_2d(crop, color=(int(r), int(gg), int(b)))
            item['score_' + str(name)] = round(_score(sim), 4)
        luma = rgb2luma(crop)
        item['score_luma'] = round(_score(luma), 4)
        out['grids'].append(item)
        grid_out[tuple(int(x) for x in loca)] = (item, crop)

    if args.get('icons'):
        out_dir = str(args.get('out_dir') or os.path.join(
            os.path.dirname(os.path.abspath(__file__)), '..', 'data'))
        try:
            os.makedirs(out_dir, exist_ok=True)
            tiles = []
            for loca in sorted(grid_out):
                item, crop = grid_out[loca]
                tile = cv2.resize(np.array(crop), (150, 60), interpolation=cv2.INTER_NEAREST)
                tile = cv2.copyMakeBorder(tile, 18, 2, 2, 2, cv2.BORDER_CONSTANT,
                                          value=(0, 0, 0))
                cv2.putText(tile, f'{loca} {item.get("str")}', (3, 13),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.4, (255, 255, 255), 1)
                tiles.append(tile)
            cols = int(args.get('cols') or 6)
            rows = []
            for i in range(0, len(tiles), cols):
                chunk = tiles[i:i + cols]
                while len(chunk) < cols:
                    chunk.append(np.zeros_like(tiles[0]))
                rows.append(np.hstack(chunk))
            montage = np.vstack(rows) if rows else np.zeros((10, 10, 3), np.uint8)
            path = os.path.join(out_dir, str(args.get('icons_name') or '_grids_boss.png'))
            out['icons_ok'] = bool(cv2.imwrite(path, montage))
            out['icons_path'] = path
        except Exception as e:
            out['icons_error'] = f'{type(e).__name__}: {e}'

    # 直接给结论用的排序：按上游红色判据与 luma 判据各排一次
    for key in ('score_upstream_red', 'score_blue_rbswap', 'score_luma'):
        ranked = sorted([g for g in out['grids'] if key in g],
                        key=lambda x: -x[key])[:3]
        out['top_' + key] = [[g['loca'], g[key], g['str']] for g in ranked]
    out['boss_grids'] = [[g['loca'], g['str']] for g in out['grids'] if g.get('is_boss')]
    return out


# ---------------------------------------------------------------------------
# 设备引擎（方案 A）：设备 I/O 也走宿主，换引擎＝改配置，C# 侧零改动
# ---------------------------------------------------------------------------
_DEVICE_OBJ = None
_DEVICE_KEY = None
_DEVICE_ARGS = {}


def _device_engine(config=None):
    """构造并缓存引擎的设备层（`module.device.device.Device`）。

    引擎自带**多引擎设备层**（`module/device/method/`：adb / ascreencap / droidcast /
    maatouch / minitouch / hermit / nemu_ipc / scrcpy / uiautomator_2 / wsa / ldopengl），
    由 `Emulator_ScreenshotMethod`（截图后端）与 `Emulator_ControlMethod`（输入后端）选择。
    在 C# 里为每个后端写实现等于重写上游方法层，所以统一走这里。
    """
    global _DEVICE_OBJ, _DEVICE_KEY
    serial = str(_DEVICE_ARGS.get('serial') or '127.0.0.1:16384')
    shot = str(_DEVICE_ARGS.get('screenshot') or 'adb')
    ctrl = str(_DEVICE_ARGS.get('control') or 'ADB')
    transport_key = (serial, shot, ctrl)
    if config is not None:
        config.override(Emulator_Serial=serial, Emulator_ControlMethod=ctrl)
        # Native Device resolves auto and persists the benchmark winner. An
        # override of auto would reset it on every bind, forcing the ADB fallback.
        # Keep the native value across config reloads; explicit backends stay fixed.
        if shot != 'auto':
            config.override(Emulator_ScreenshotMethod=shot)
        identity = _device_config_identity(config)
    if _DEVICE_OBJ is not None:
        if _DEVICE_KEY[:3] != transport_key:
            raise RuntimeError('常驻设备不能在运行中切换串号或后端')
        if config is not None:
            if _DEVICE_KEY[3:] != identity:
                raise RuntimeError('常驻设备的实例或设备配置已变化，请关闭会话后切换')
        return _DEVICE_OBJ
    cfg = config if config is not None else _map_config()
    # adbutils 依赖 pkg_resources；上游兼容模块必须在设备模块前导入。
    import module.device.pkg_resources
    # 把项目内固定的 adb 目录挂到 PATH 上：引擎内部有些地方直接调**裸 `adb`**
    # （例如 `adb push` 推 MaaTouch/minitouch/DroidCast 的二进制），
    # 裸名解析不到时就报 `FileNotFoundError: [WinError 2]` —— 实测踩过，
    # 而且它会**伪装成"某个后端不可用"**（droidcast 就是这么失败的）。
    _adb_dir = os.path.normpath(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), '..', '.runtime', 'venv314',
        'Lib', 'site-packages', 'adbutils', 'binaries'))
    if os.path.isdir(_adb_dir) and _adb_dir not in os.environ.get('PATH', ''):
        os.environ['PATH'] = _adb_dir + os.pathsep + os.environ.get('PATH', '')
    if config is None:
        # Default navigation/capture sessions use the same temporary transport
        # binding as explicit task configs, never persist CLI device selections.
        cfg.override(Emulator_Serial=serial, Emulator_ControlMethod=ctrl)
        if shot != 'auto':
            cfg.override(Emulator_ScreenshotMethod=shot)
    from module.device.device import Device
    _DEVICE_OBJ = Device(cfg)
    _DEVICE_KEY = (*transport_key, *_device_config_identity(cfg))
    return _DEVICE_OBJ


def _device_config_identity(config):
    # ConnectionAttr caches more than the serial: package, server, emulator
    # settings and backend handles. Rebinding config alone cannot change them.
    fields = {name: getattr(config, name) for name in config.bound if name.startswith('Emulator')}
    return (config.config_name, config.SERVER,
            json.dumps(fields, sort_keys=True, ensure_ascii=False, default=str))


def op_device_configure(args):
    """选择设备引擎：`serial` / `screenshot` / `control`。只记录选择，首次用其它 device_* 时构造。"""
    global _DEVICE_OBJ, _DEVICE_KEY
    for k in ('serial', 'screenshot', 'control'):
        if args.get(k):
            _DEVICE_ARGS[k] = args[k]
    _DEVICE_OBJ = None
    _DEVICE_KEY = None
    return {'configured': dict(_DEVICE_ARGS)}


def op_device_info(args):
    """当前设备引擎状态：串号、截图/输入后端、包名。"""
    dev = _device_engine()
    out = {'args': dict(_DEVICE_ARGS)}
    try:
        out['serial'] = str(getattr(dev, 'serial', None) or '')
        out['screenshot_method'] = str(getattr(dev.config, 'Emulator_ScreenshotMethod', ''))
        out['control_method'] = str(getattr(dev.config, 'Emulator_ControlMethod', ''))
        out['package'] = str(getattr(dev.config, 'package', ''))
    except Exception as e:
        out['info_error'] = f'{type(e).__name__}: {e}'
    return out


def op_ui_ensure(args):
    """Use upstream UI navigation and its own page/additional handling."""
    started = time.perf_counter()
    destination_name = args.get('destination')
    result = {'destination': destination_name, 'arrived': False,
              'final_page': None, 'changed': None, 'failure_frames': []}
    device = None
    failure_frame = None

    def finish():
        if result.get('error') and failure_frame is not None:
            # Retain the frame seen by the failed native operation. Do not take
            # another screenshot or click while reporting a navigation error.
            result['failure_frame_source'] = 'unavailable'
            try:
                image = getattr(device, 'image', None) if getattr(device, 'has_cached_image', False) else None
                if image is not None:
                    import io
                    from PIL import Image
                    buffer = io.BytesIO()
                    Image.fromarray(image).save(buffer, format='PNG')
                    with failure_frame.open('xb') as stream:
                        try:
                            stream.write(buffer.getvalue())
                        except Exception:
                            stream.close()
                            failure_frame.unlink()
                            raise
                    result['failure_frames'].append(str(failure_frame))
                    result['failure_frame_source'] = 'last_cached_device_frame'
            except Exception as error:
                message = f'保存导航失败帧失败: {type(error).__name__}: {error}'
                result['failure_frame_error'] = message
                result['error'] += '\n' + message
        result['elapsed_ms'] = round((time.perf_counter() - started) * 1000, 1)
        return result

    if args.get('allow_actions') is not True:
        result.update(error='Explicit allow_actions=true required',
                      error_kind='ActionNotAllowed')
        return finish()

    from module.ui.page import Page
    destination = Page.all_pages.get(destination_name) if isinstance(destination_name, str) else None
    if destination is None or destination.check_button is None:
        result.update(error=f'Unknown or unverifiable destination: {destination_name}',
                      error_kind='UnknownPage')
        return finish()

    path = args.get('failure_frame')
    if path is not None:
        if not isinstance(path, str) or not path.strip() or not Path(path).is_absolute():
            result.update(error='failure_frame must be an absolute artifact path',
                          error_kind='InvalidArtifactPath')
            return finish()
        failure_frame = Path(path)

    ui = None
    try:
        from module.ui.ui import UI
        device = _device_engine()
        # Match native task entry: old detection history belongs to the previous
        # task. Clear once; guards remain active throughout this navigation.
        device.stuck_record_clear()
        device.click_record_clear()
        ui = UI(device.config, device)
        # Fast capture can expose a sibling page during a transition. Give each
        # native page-graph click time to settle before UI.ui_goto selects a new edge.
        native_click = device.click
        had_click_override = 'click' in vars(device)
        previous_click_override = vars(device).get('click')

        def settled_click(*click_args, **click_kwargs):
            value = native_click(*click_args, **click_kwargs)
            time.sleep(1.0)
            return value

        device.click = settled_click
        try:
            changed = ui.ui_ensure(destination, skip_first_screenshot=False)
        finally:
            if had_click_override:
                device.click = previous_click_override
            else:
                del device.click
        current = getattr(ui, 'ui_current', None)
        result['changed'] = bool(changed)
        result['final_page'] = getattr(current, 'name', None)
        # Confirm on a new frame; the frame that ended ui_goto can be transitional.
        device.screenshot()
        result['arrived'] = bool(current == destination and ui.ui_page_appear(destination))
        if not result['arrived']:
            result.update(error='Destination not visible after native UI navigation',
                          error_kind='DestinationNotVisible')
    except (Exception, SystemExit) as e:
        # ui_goto clears this on success, but leaves temporary parent links on failure.
        Page.clear_connection()
        result['final_page'] = getattr(getattr(ui, 'ui_current', None), 'name', None)
        tail = [f'{os.path.basename(frame.filename)}:{frame.lineno} {frame.name}'
                for frame in traceback.extract_tb(e.__traceback__)[-8:]]
        result.update(error=f'{type(e).__name__}: {e}' + ('\n' + '\n'.join(tail) if tail else ''),
                      error_kind=type(e).__name__, traceback_tail=tail)
    return finish()


def op_device_screencap(args):
    """用引擎选定的后端截图到 `path`，返回耗时 —— 用于对比各后端（我们的瓶颈就在截图）。"""
    import time as _time
    dev = _device_engine()
    path = args.get('path')
    # raw=True 时直接调后端原始实现（`screenshot_<method>`），**绕开 ALAS 的截图间隔节流**
    # （`screenshot()` 里有 `self._screenshot_interval.wait()`，实测 0.3s 会把各后端的
    #  速度差异整个盖住：三个后端都量到 ~375ms，其中 ~300ms 是节流）。
    raw = bool(args.get('raw'))
    method = str(getattr(dev.config, 'Emulator_ScreenshotMethod', 'adb'))
    t0 = _time.time()
    if raw:
        fn = getattr(dev, f'screenshot_{method}', None)
        if fn is None:
            return {'error': f'后端 {method} 没有 screenshot_{method} 原始实现'}
        img = fn()
    else:
        img = dev.screenshot()
    ms = (_time.time() - t0) * 1000
    out = {'ms': round(ms, 1), 'raw': raw,
           'screenshot_method': method}
    if path:
        # ALAS 的 `Device.screenshot()` 返回 **numpy 数组**，不是 PIL Image ——
        # 直接 `.save()` 会报 `'numpy.ndarray' object has no attribute 'save'`（实测踩过）。
        #
        # **落盘前必须把通道换回去**：ALAS 内部约定是 **RGB**（`method/adb.py:134` 的
        # BGR2RGB），而 `cv2.imwrite` 把数组当 **BGR** 写 —— 直接写出来的 PNG 在**肉眼/看图工具**
        # 里是 R/B 互换的（真实屏幕上的红 BOSS 图标会显示成蓝色）。
        # 这个坑实实在在误导过我：我据"存盘 PNG 里眼睛是蓝的"写了一版 BOSS 颜色垫片，
        # 而引擎真正交给上游的图（E）上眼睛本来就是红的、上游判据一直能工作 ✗
        # 现在的约定：**落盘 PNG = 与真实屏幕一致**，`load_image(png)` 读回来就等于引擎的 E。
        if hasattr(img, 'save'):
            img.save(path)
        else:
            import cv2 as _cv2
            _write = img
            try:
                import numpy as _np
                if isinstance(img, _np.ndarray) and img.ndim == 3 and img.shape[2] == 3:
                    _write = _cv2.cvtColor(img, _cv2.COLOR_RGB2BGR)
            except Exception:
                _write = img
            _cv2.imwrite(path, _write)
        try:
            out['bytes'] = os.path.getsize(path)
        except Exception:
            pass
        out['path'] = path
    # 尺寸：numpy 用 shape（BGR 的 .size 是元素总数，是个 int，`list(int)` 会炸 —— 实测踩过）
    try:
        if hasattr(img, 'shape'):
            out['size'] = [int(v) for v in img.shape[:2]]
        elif hasattr(img, 'size'):
            out['size'] = [int(v) for v in img.size]
    except Exception:
        pass
    return out


def _button_at(x, y, name='point'):
    """把裸坐标包成上游 `Button`。

    ALAS 的 `Control.click/swipe` 收的是 **Button 对象**（内部取 `button.button` 作为可点区域），
    直接传 int 会报 `'int' object has no attribute 'button'`（实测踩过）。
    """
    from module.base.button import Button
    x, y = int(x), int(y)
    area = (x - 1, y - 1, x + 1, y + 1)
    return Button(area=area, color=(), button=area, name=name)


def op_device_click(args):
    import time as _time
    dev = _device_engine()
    t0 = _time.time()
    dev.click(_button_at(args['x'], args['y']))
    return {'ms': round((_time.time() - t0) * 1000, 1)}


def op_device_swipe(args):
    """滑动。**注意上游签名是 `swipe(p1, p2, duration=...)`** —— 两个点各是一个元组，
    不是四个坐标。此前这里按 `dev.swipe(x1, y1, x2, y2, duration=...)` 调用，
    第四个位置参数正好落在 `duration` 上，报
    `TypeError: Control.swipe() got multiple values for argument 'duration'`（实测踩过 ✗）。
    """
    import time as _time
    dev = _device_engine()
    t0 = _time.time()
    dur = args.get('duration', 0.2)
    # 上游允许 duration 是 float 或 (min, max) 区间
    if isinstance(dur, (list, tuple)) and len(dur) == 2:
        dur = (float(dur[0]), float(dur[1]))
    else:
        dur = float(dur)
    dev.swipe((int(args['x1']), int(args['y1'])), (int(args['x2']), int(args['y2'])),
              duration=dur)
    return {'ms': round((_time.time() - t0) * 1000, 1)}


def op_device_back(args):
    dev = _device_engine()
    try:
        dev.adb_shell(['input', 'keyevent', '4'])
        return {'ok': True}
    except Exception as e:
        return {'ok': False, 'error': f'{type(e).__name__}: {e}'}


def op_device_capture_set(args):
    """用引擎的设备层截图，**直接置入宿主的当前截图**（像素不跨语言边界）。

    与 C# 自己 adb 截图的对比：那条路是 `adb → C# 持有 PNG → 交给宿主 → 解码`，
    本 op 把它压成 `引擎后端 → 宿主`，省掉跨语言传输与一次落盘/读盘。
    想换更快的后端（如 droidcast）只需 `device_configure`，C# 侧零改动。

    `raw=True`（默认）直接调后端的原始实现，绕开 ALAS 的 0.3s 截图间隔节流。
    """
    import time as _time
    _state['image'] = None
    _state['image_path'] = None
    dev = _device_engine()
    raw = bool(args.get('raw', True))
    method = str(getattr(dev.config, 'Emulator_ScreenshotMethod', 'adb'))
    t0 = _time.time()
    if raw:
        fn = getattr(dev, f'screenshot_{method}', None)
        img = fn() if fn is not None else dev.screenshot()
    else:
        img = dev.screenshot()
    cap_ms = (_time.time() - t0) * 1000

    # raw 直接调用配置的截图后端；普通路径还经过上游截图包装器。
    # 后端已确定通道顺序；直接保留 RGB 像素，不写入系统临时文件。
    # 复制一份以免后续截图复用缓冲区时改动当前帧；其他图像类型在内存中解码。
    from io import BytesIO
    import numpy as _np
    from module.base.utils import load_image
    try:
        if isinstance(img, _np.ndarray) and img.ndim == 3 and img.shape[2] == 3:
            _state['image'] = img.copy()
        else:
            buffer = BytesIO()
            img.save(buffer, format='PNG')
            buffer.seek(0)
            _state['image'] = load_image(buffer)
    except Exception as e:
        return {'error': f'{type(e).__name__}: {e}', 'capture_ms': round(cap_ms, 1)}
    return {'capture_ms': round(cap_ms, 1), 'method': method, 'raw': raw,
            'shape': list(getattr(_state['image'], 'shape', ()) or ())}


def op_ui_rules_sweep(args):
    """
    界面与控件识别的**统一验收**：一次跑完三类实体并汇总。

      1. Page                 —— 按 ui_page_appear 原规则判定
      2. 原生模块级控件        —— 调上游 appear/get/at_top
      3. 延迟属性              —— 构造 UI 实例后调其识别方法

    判定口径：**不抛异常即为"可驱动"**；是否命中取决于当前画面是否是该实体所在的页面，
    因此报告里把「可驱动数」与「命中数」分开列，不能混为一谈。
    """
    import importlib
    image = _require_image()

    report = {'pages': {'total': 0, 'driven': 0, 'hit': [], 'errors': []},
              'module_level': {'total': 0, 'driven': 0, 'hit': [], 'errors': []},
              'cached_property': {'total': 0, 'constructed': 0, 'hit': [], 'errors': [], 'no_hit_criterion': []}}

    # ---- 1. 页面
    pl = op_page_list({})
    report['pages']['total'] = pl['count']
    for pg in pl['pages']:
        try:
            r = op_page_appear({'page': pg['page']})
            report['pages']['driven'] += 1
            if r['appear']:
                report['pages']['hit'].append(pg['page'])
        except Exception as e:
            report['pages']['errors'].append(f"{pg['page']}: {type(e).__name__}: {e}")

    # ---- 2. Native module controls (including upstream subclasses).
    inventory = op_ui_rule_list({})
    report['module_level']['total'] = inventory['count']
    report['module_level']['errors'].extend(inventory['errors'])
    for rule in inventory['rules']:
        try:
            checked = op_ui_rule_check({'module': rule['module'], 'name': rule['name']})
            if checked['errors']:
                report['module_level']['errors'].append({'rule': rule['name'],
                                                         'errors': checked['errors']})
            else:
                report['module_level']['driven'] += 1
                if checked['results'].get('appear') is True:
                    report['module_level']['hit'].append(rule['name'])
        except Exception as error:
            report['module_level']['errors'].append(f'{rule["name"]}: {type(error).__name__}: {error}')

    # ---- 3. Resolve lazy objects from the original properties, never a page list.
    properties = sorted({(r['module'], r['owner'], r['attr'])
                         for r in inventory['declarations']
                         if r['scope'] == 'property' and r['owner']})
    report['cached_property']['total'] = len(properties)
    for module, owner, attr in properties:
        label = f'{owner}.{attr}'
        try:
            checked = op_cached_rule_check({'module': module, 'class': owner, 'attr': attr})
            report['cached_property']['constructed'] += 1
            report['cached_property']['errors'].extend(checked['errors'])
            if checked['hit'] and not checked['errors']:
                report['cached_property']['hit'].append(label)
            if not checked['controls']:
                report['cached_property']['no_hit_criterion'].append(label)
        except Exception as error:
            report['cached_property']['errors'].append(f'{label}: {type(error).__name__}: {error}')
    report['factories'] = [r for r in inventory['declarations'] if r['scope'] == 'factory']

    t = report
    report['summary'] = {
        'total': t['pages']['total'] + t['module_level']['total'] + t['cached_property']['total'],
        'driven': t['pages']['driven'] + t['module_level']['driven'] + t['cached_property']['constructed'],
        'errors': len(t['pages']['errors']) + len(t['module_level']['errors'])
                  + len(t['cached_property']['errors']),
    }
    return report

OPS = {
    'ping': op_ping,
    'set_server': op_set_server,
    'screenshot_load': op_screenshot_load,
    'screenshot_set': op_screenshot_set,
    'asset_info': op_asset_info,
    'page_list': op_page_list,
    'ui_rule_check': op_ui_rule_check,
    'ui_rules_sweep': op_ui_rules_sweep,
    'ui_rule_list': op_ui_rule_list,
    'navbar_info': op_navbar_info,
    'ui_rule_inventory': op_ui_rule_inventory,
    'asset_button_center': op_asset_button_center,
    'page_appear': op_page_appear,
    'screenshot_scale': op_screenshot_scale,
    'appear_on': op_appear_on,
    'appear_on_batch': op_appear_on_batch,
    'button_match': op_button_match,
    'template_match': op_template_match,
    'ocr': op_ocr,
    'page_current': op_page_current,
    'account_state': op_account_state,
    'task_catalog': op_task_catalog,
    'task_schedule': op_task_schedule,
    'periodic_plan': op_periodic_plan,
    'periodic_preflight': op_periodic_preflight,
    'periodic_run': op_periodic_run,
    'tool_plan': op_tool_plan,
    'tool_run': op_tool_run,
    'scheduler_run': op_scheduler_run,
    'config_get': op_config_get,
    'statistics_report': op_statistics_report,
    'statistics_refresh_loot': op_statistics_refresh_loot,
    'meowfficer_report': op_meowfficer_report,
    'meowfficer_clear': op_meowfficer_clear,
    'shop_strategy_validate': op_shop_strategy_validate,
    'ui_page_graph': op_ui_page_graph,
    'cached_rule_check': op_cached_rule_check,
    'page_positive_control': op_page_positive_control,
    'rule_positive_control': op_rule_positive_control,
    'map_detection_assets': op_map_detection_assets,
    's3_run_plan': op_s3_run_plan,
    's3_campaign_init': op_s3_campaign_init,
    's3_campaign_info': op_s3_campaign_info,
    's3_campaign_call': op_s3_campaign_call,
    's3_probe_view': op_s3_probe_view,
    'device_capture_set': op_device_capture_set,
    'device_configure': op_device_configure,
    'device_info': op_device_info,
    'ui_ensure': op_ui_ensure,
    'device_screencap': op_device_screencap,
    'device_click': op_device_click,
    'device_swipe': op_device_swipe,
    'device_back': op_device_back,
    'map_detect': op_map_detect,
    'map_detect_trace': op_map_detect_trace,
    'map_grids': op_map_grids,
    'globe_detect': op_globe_detect,
}


def handle(req):
    rid = req.get('id')
    op = req.get('op')
    try:
        fn = OPS.get(op)
        if fn is None:
            raise KeyError(f'未知操作: {op}（可用: {", ".join(sorted(OPS))}）')
        from deploy_storage import install as install_deploy_storage
        install_deploy_storage()
        return {'id': rid, 'ok': True, 'result': fn(req.get('args') or {})}
    except Exception as e:
        return {'id': rid, 'ok': False,
                'error': f'{type(e).__name__}: {e}',
                'traceback': traceback.format_exc().splitlines()[-3:]}


def json_default(o):
    """
    兜底序列化：上游的识别函数经常把 numpy 标量/数组直接塞进返回值
    （例如 Button.match 的 button_offset 是 np.int64），而 json.dumps
    只认 Python 原生类型。没这一层，协议会以 TypeError 整体失败——
    单个字段的类型瑕疵不该让一次识图判定丢掉。
    """
    item = getattr(o, 'item', None)
    if callable(item) and getattr(o, 'shape', None) == ():
        return item()
    tolist = getattr(o, 'tolist', None)
    if callable(tolist):
        return tolist()
    return str(o)


def handle_line(request_json: str) -> str:
    """
    进程内宿主的入口：一行请求 JSON → 一行响应 JSON。

    这是 C# 通过 CPython C API 调用的唯一函数，签名刻意保持最简
    （str -> str），因为 P/Invoke 调 C API 时越简单越不容易出错。
    """
    try:
        req = json.loads(request_json)
    except Exception as e:
        return json.dumps({'id': None, 'ok': False, 'error': f'JSON 解析失败: {e}'},
                          ensure_ascii=False, default=json_default)
    if req.get('op') == 'shutdown':
        return json.dumps({'id': req.get('id'), 'ok': True, 'result': {'bye': True}},
                          ensure_ascii=False, default=json_default)
    return json.dumps(handle(req), ensure_ascii=False, default=json_default)
