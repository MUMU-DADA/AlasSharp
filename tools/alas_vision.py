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
import os
import sys
import time
import traceback

FORK = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                     '..', '..', 'my fork project', 'AzurLaneAutoScript'))
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
    mod_name, name = asset_id.split('/', 1)
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
    if s not in ('cn', 'en', 'jp', 'tw'):
        raise ValueError(f'非法服务器: {s}')
    server_module.server = s
    for obj in _state['assets'].values():
        try:
            obj.resource_release()
        except Exception:
            pass
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
            if cls not in ('Navbar', 'Switch', 'Scroll', 'Setting', 'Page'):
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

def op_ui_rule_list(args):
    """
    枚举**模块级**的 Switch/Scroll/Navbar/Setting 实例（可直接驱动的那些）。

    做法：先 AST 扫出「模块级 `X = Switch(...)`」的位置，再只导入这些模块取对象。
    自维护——上游新增实例会自动出现在清单里，不需要硬编码路径。
    """
    import ast
    import importlib
    import os as _os

    found = {}
    for dp, dirs, fs in _os.walk(_os.path.join(FORK, 'module')):
        dirs[:] = [d for d in dirs if d != '__pycache__']
        for fn in fs:
            if not fn.endswith('.py'):
                continue
            p = _os.path.join(dp, fn)
            try:
                tree = ast.parse(open(p, encoding='utf-8').read())
            except Exception:
                continue
            mod = _os.path.relpath(p, FORK)[:-3].replace(_os.sep, '.')
            for node in tree.body:
                if not isinstance(node, ast.Assign) or not isinstance(node.value, ast.Call):
                    continue
                cls = getattr(node.value.func, 'id', None)
                if cls in ('Switch', 'Scroll', 'Navbar', 'Setting') \
                        and isinstance(node.targets[0], ast.Name):
                    found.setdefault(mod, []).append((node.targets[0].id, cls))

    rules, errors = [], []
    for mod, names in sorted(found.items()):
        try:
            m = importlib.import_module(mod)
        except Exception as e:
            errors.append(f'{mod}: {type(e).__name__}: {e}')
            continue
        for name, cls in names:
            obj = getattr(m, name, None)
            if obj is None:
                continue
            entry = {'module': mod, 'name': name, 'class': cls, 'attr': name}
            if cls == 'Switch':
                st = getattr(obj, 'states', None)
                entry['states'] = sorted(st.keys()) if isinstance(st, dict) else None
                entry['offset'] = list(obj.offset) if getattr(obj, 'offset', None) else None
            elif cls == 'Scroll':
                a = getattr(obj, 'area', None)
                entry['area'] = [float(x) for x in a] if a else None
            entry['file'] = getattr(getattr(obj, 'check_button', None), 'file', None) \
                if cls == 'Switch' else None
            rules.append(entry)
    return {'rules': rules, 'count': len(rules), 'errors': errors[:5],
            'by_class': {k: sum(1 for r in rules if r['class'] == k)
                         for k in ('Switch', 'Scroll', 'Navbar', 'Setting')}}

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
    kind = type(obj).__name__
    results = {}
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
            results[meth] = f'{type(e).__name__}: {e}'
    return {'module': args['module'], 'name': args['name'], 'class': kind, 'results': results}

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
    """
    **按上游 UI.ui_page_appear 的原规则**判定当前页面。

    规则（逐条对齐上游 module/ui/ui.py）：
      - page_main：先试 page_main_white.check_button（offset=传入值），
        再试 page_main.check_button（offset=(5,5)），任一命中即为真
      - en 服的 page_academy：额外试 ACADEMY_GOTO_MUNITIONS
      - 其余：page.check_button.match(image, offset=offset)

    注意：`Base.appear(button, offset=...)` 在 offset 为真时走的是 **Button.match（模板匹配）**，
    不是 appear_on（颜色检查）—— 这一点很关键，颜色检查只是快速预筛。
    """
    import module.ui.page as page_mod
    image = _require_image()
    page_name = args['page']
    page = getattr(page_mod, page_name)
    offset = tuple(args.get('offset') or (30, 30))
    server = server_module.server
    tried = []

    def try_button(btn, off, label):
        try:
            hit = bool(btn.match(image, offset=off))
        except Exception as e:
            tried.append({'button': label, 'error': f'{type(e).__name__}: {e}'})
            return False
        tried.append({'button': label, 'offset': list(off), 'appear': hit})
        return hit

    if server == 'en' and page_name == 'page_academy':
        if try_button(_resolve('ui/ACADEMY_GOTO_MUNITIONS'), offset, 'ACADEMY_GOTO_MUNITIONS'):
            return {'page': page_name, 'appear': True, 'tried': tried}

    if page_name == 'page_main':
        if try_button(getattr(page_mod, 'page_main_white').check_button, offset, 'page_main_white'):
            return {'page': page_name, 'appear': True, 'tried': tried}
        if try_button(page.check_button, (5, 5), 'page_main'):
            return {'page': page_name, 'appear': True, 'tried': tried}
        return {'page': page_name, 'appear': False, 'tried': tried}

    hit = try_button(page.check_button, offset, page_name)
    return {'page': page_name, 'appear': hit, 'tried': tried}

def op_asset_info(args):
    obj = _resolve(args['asset'])
    info = {
        'id': args['asset'],
        'type': type(obj).__name__,
        'name': getattr(obj, 'name', None),
        'is_gif': bool(getattr(obj, 'is_gif', False)),
    }
    for attr in ('area', 'color', 'button', 'file'):
        try:
            v = getattr(obj, attr)
        except Exception:
            v = None
        if isinstance(v, tuple):
            v = list(v)
        info[attr] = v
    try:
        if info['is_gif']:
            obj.ensure_template()
            info['frame_shapes'] = [list(f.shape) for f in obj.image]
            info['image_count'] = len(obj.image)
        else:
            obj.ensure_template()
            info['image_shape'] = list(obj.image.shape)
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
    ocr = Ocr(btn, lang=args.get('lang', 'azur_lane'), letter=args.get('letter'))
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
        sub = modname[len('module.'):-len('.assets')]
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


def _variants(asset_id):
    """同一按钮在不同主界面版本下的候选资产，**只保留真的声明过的**。

    上游把新版主界面的按钮定义成独立资产（`module/ui_white/assets.py` 里的
    `X_WHITE`），旧版 `ui/X` 在新版界面上实测只有 ≤0.25 分——模板早已不在屏上，
    点它的坐标只会点到空气。命名是上游的既有约定，这里只是按约定去**探测**
    （解析不到就丢弃），不猜、不硬编码映射表。
    """
    out = [asset_id]
    name = asset_id.split('/', 1)[1] if '/' in asset_id else asset_id
    white = 'ui_white/%s_WHITE' % name
    if white != asset_id:
        try:
            _resolve(white)
            out.append(white)
        except Exception:
            pass
    return out


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
            variants = _variants(bid)
            node['links'].append({'to': dest.name, 'button': bid, 'variants': variants})
            edges += 1
        nodes.append(node)

    # 往返自检：资产 id 反查必须拿回**同一个对象**，否则 C# 会点到别的按钮
    bad = [aid for aid, obj in pairs if _resolve(aid) is not obj]
    return {'nodes': nodes, 'node_count': len(nodes), 'edge_count': edges,
            'unmapped': unmapped, 'roundtrip_bad': bad,
            'roundtrip_checked': len(pairs)}


def op_ui_rules_sweep(args):
    """
    界面与控件识别的**统一验收**：一次跑完三类实体并汇总。

      1. Page（53）           —— 按 ui_page_appear 原规则判定
      2. 模块级 Switch/Scroll（20）—— 调上游 appear/get/at_top
      3. cached_property 规则（6） —— 构造 UI 实例后调其识别方法

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

    # ---- 2. 模块级实例
    ml = op_ui_rule_list({})
    report['module_level']['total'] = ml['count']
    for r in ml['rules']:
        try:
            res = op_ui_rule_check({'module': r['module'], 'name': r['name']})['results']
            bad = any(isinstance(v, str) and ('Error' in v or 'Exception' in v)
                      for v in res.values())
            if bad:
                report['module_level']['errors'].append(f"{r['name']}: {res}")
            else:
                report['module_level']['driven'] += 1
                if res.get('appear') or res.get('at_bottom'):
                    report['module_level']['hit'].append(r['name'])
        except Exception as e:
            report['module_level']['errors'].append(f"{r['name']}: {type(e).__name__}: {e}")

    # ---- 3. cached_property 规则（需 UI 实例；用上游的 AzurLaneConfig + 注入真实截图）
    from module.config.config import AzurLaneConfig
    targets = [
        ('module.retire.dock', 'Dock', 'dock_filter'),
        ('module.storage.ui', 'StorageUI', 'storage_filter'),
        ('module.shop.ui', 'ShopUI', '_shop_bottom_navbar'),
        ('module.shop.ui', 'ShopUI', 'shop_nav_250814'),
        ('module.shop.ui', 'ShopUI', 'shop_tab_250814'),
        ('module.shop_event.ui', 'EventShopUI', 'event_shop_tab_count_and_navbar'),
    ]
    report['cached_property']['total'] = len(targets)
    for modname, clsname, attr in targets:
        label = f'{clsname}.{attr}'
        try:
            cls = getattr(importlib.import_module(modname), clsname)
            inst = cls(AzurLaneConfig('alas'), _make_main_shim(image).device)
            inst.device.image = image
            rule = getattr(inst, attr)
            report['cached_property']['constructed'] += 1
            # Setting 类**没有** appear/get_info/get 方法（它是 is_option_active /
            # _product_setting_status / set），用同一判据结构上永远不可能命中，
            # 留在分母里会让 hit 比率失真。单独记出来，不混入命中统计。
            if type(rule).__name__ == 'Setting':
                report['cached_property']['no_hit_criterion'].append(label)
                continue
            # 口径修正：构造成功 ≠ 识别命中。这里**真正跑一次识别**，只有返回真才算命中。
            # （原先把构造成功记进 hit，字段名与含义不符，会误导后续判断。）
            for meth in ('appear', 'get_info', 'get'):
                fn = getattr(rule, meth, None)
                if not callable(fn):
                    continue
                try:
                    v = fn(inst)
                    if hasattr(v, 'item'):
                        v = v.item()
                    if v is True:
                        report['cached_property']['hit'].append(label)
                except Exception:
                    pass
                break
        except Exception as e:
            report['cached_property']['errors'].append(f'{label}: {type(e).__name__}: {e}')

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
    'ui_page_graph': op_ui_page_graph,
}


def handle(req):
    rid = req.get('id')
    op = req.get('op')
    try:
        fn = OPS.get(op)
        if fn is None:
            raise KeyError(f'未知操作: {op}（可用: {", ".join(sorted(OPS))}）')
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
