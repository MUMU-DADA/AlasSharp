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
            'results': results}

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
    # letter 必须是**可迭代对象**：上游 Ocr 会按字母表过滤，传 None 会在遍历时抛
    # `TypeError: 'NoneType' object is not iterable`（实测踩过，且不报"参数错"而报遍历错，很误导）。
    ocr = Ocr(btn, lang=args.get('lang', 'azur_lane'), letter=args.get('letter') or ())
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


def op_cached_rule_check(args):
    """构造 UI 实例，并调用 cached_property 规则的**真实识别方法**（真机页面级验收用）。

    与 ui_rules_sweep 里那段判据的区别很重要：那里只把 `v is True` 记为命中，
    而 `Navbar.get_info` 返回 (active,left,right)、`Switch.get` 返回状态字符串、
    `Setting` 压根没有 appear/get —— 三类规则在那个判据下**结构上永远不可能命中**。
    所以这里按类型分别取判据：

      Navbar  -> get_info / get_active / get_total（选中页签是识别结果）
      Switch  -> get（状态名）与 state_list（可选项清单）
      Setting -> 逐项 is_option_active（**实测**激活项；
                 _product_setting_status 返回的是配置**期望**，不是识别结果）
    """
    import importlib
    from module.config.config import AzurLaneConfig
    image = _require_image()
    cls = getattr(importlib.import_module(args['module']), args['class'])
    inst = cls(AzurLaneConfig('alas'), _make_main_shim(image).device)
    inst.device.image = image
    rule = getattr(inst, args['attr'])
    kind = type(rule).__name__
    detail, errors = {}, []
    hit = False

    if kind == 'Navbar':
        buttons = [getattr(b, 'name', str(b)) for b in rule.grids.buttons]
        active = rule.get_active(inst)
        total = rule.get_total(inst)
        info = rule.get_info(inst)
        detail = {'active': active, 'total': total, 'info': list(info),
                  'buttons': buttons, 'active_color': list(rule.active_color),
                  'inactive_color': list(rule.inactive_color)}
        # 选中项必须能在按钮清单里定位到，否则"识别出了个不存在的页签"
        hit = active is not None and info[0] is not None
    elif kind == 'Switch':
        state = rule.get(inst)
        detail = {'state': state, 'appear': state != 'unknown',
                  'states': [d.get('state') for d in getattr(rule, 'state_list', [])],
                  'offset': getattr(rule, 'offset', None)}
        hit = state != 'unknown'
    elif kind == 'Setting':
        settings = getattr(rule, 'settings', {})
        observed = []
        for (setting, option_name), button in settings.items():
            try:
                if rule.is_option_active(button):
                    observed.append('%s/%s' % (setting, option_name))
            except Exception as e:
                errors.append('%s/%s: %s: %s' % (setting, option_name, type(e).__name__, e))
        detail = {'observed_active': observed,
                  'option_count': len(settings),
                  'settings': sorted({k[0] for k in settings})}
        # 一个激活项都没识别出来也可能是画面本来就没勾选；至少要有可选清单才算跑通
        hit = len(settings) > 0
    else:
        # 运行时算出来的规则（如事件商店的 (count, navbar)）没有统一判据，
        # 只如实报出类型与规模，不计入命中
        detail = {'repr': str(rule)[:300],
                  'size': len(rule) if hasattr(rule, '__len__') else None}

    return {'label': '%s.%s' % (args['class'], args['attr']), 'class': kind,
            'hit': bool(hit), 'detail': detail, 'errors': errors}


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
        rules = op_ui_rule_list({})['rules']
        for r in rules:
            mod = importlib.import_module(r['module'])
            obj = getattr(mod, r['name'])
            kind = type(obj).__name__
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
            ok = bool(per_state) and all(s['ok'] for s in per_state)
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

    实测（docs/s3-entry-sequence.md）：进 3-1 时 `enter_map` 在该处理器上反复点击
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


def apply_brute_finish_none_compat():
    """**小图卡点的真凶**：上游用 `scipy.optimize.brute` 搜消失点/远点，而 scipy 的 brute
    **默认 `finish=fmin`** —— 网格搜完之后再用**无约束**的 Nelder-Mead 精修一次，
    精修结果**可以跑出给定区间**。实测（1-4 失败帧，把 brute 拦下来看参数与返回值）：

        BRUTE _vanish_point_value   ranges=((540,740), (-3000,-1000))  -> [636.18, -1682.27]
        BRUTE _distant_point_value  ranges=((-3200,-1600),)            -> [636.18]   ← 越界！

    远点的 x 漂到了消失点的 x 上 ⇒ 两点距离 0 < 10 ⇒
    `MapDetectionError: Vanish point and distant point too close` ⇒ **整张图判定失败**
    （`perspective.py:123-128`）。这也是 1-1 / 1-4 / 7-1 / 8-1 这些"小图不能跑"的共同原因；
    同时解释了为什么"放宽搜索区间"完全无效（区间不被精修尊重）。

    垫片：强制 `finish=None`（纯网格搜索，结果必在区间内），并用 `np.atleast_1d` 包一层 ——
    因为 `finish=None` 时 1 维 brute 返回的是**标量**，而上游代码是 `brute(...)[0]` 取数组元素，
    不包就会 `IndexError: invalid index to scalar variable`（实测踩过 ✗）。

    离线回归（`data/` 下两帧）：
        失败帧 `_map_init_fail_campaign_1_4_att1.png`：原来直接抛错 → 现在能检出（28 格 / [7,4]）
        好帧   `_14_entrypos.png`：仍是 21 格 / [7,3]（与 1-4 的 G3 一致）✓ **无回归**
    """
    try:
        import numpy as _np
        import module.map_detection.perspective as _p
        from scipy import optimize as _opt
        if getattr(_p, '_alas_brute_compat', False):
            return
        _orig = _opt.brute

        def _brute_no_finish(func, ranges, *a, **kw):
            kw['finish'] = None
            return _np.atleast_1d(_orig(func, ranges, *a, **kw))

        _p.optimize.brute = _brute_no_finish
        _p._alas_brute_compat = True
    except Exception:
        pass


def op_s3_campaign_init(args):
    """实例化上游章节的 `Campaign`（**不执行任何游戏动作**）。

    S3 要执行的 tier A 调用（`battle_default` / `clear_siren` / …）是 ALAS 的 Campaign 方法，
    按铁律不能重写成 C#。探针已验证它在宿主里可实例化
    （`tools/diagnostics/s3_probe_campaign.py`）；本 op 把它接到协议上。
    """
    chapter = str(args.get('chapter') or 'campaign.campaign_main.campaign_2_1')
    apply_numpy2_compat()
    apply_points_empty_compat()
    apply_fleet_bar_compat()
    apply_auto_search_skip_compat()
    apply_boss_icon_color_compat()
    # 小图卡点的真凶（scipy brute 的 finish=fmin 越界）—— 见函数注释
    apply_brute_finish_none_compat()
    # 两个战斗场景的选择：默认（False）= BOSS 一刷出来就打 BOSS；
    # clear_all=True = 先清光小怪再打 BOSS。按次开关，每次 init 都要显式设回来。
    apply_clear_all_override(bool(args.get('clear_all', False)))
    apply_withdraw_trace_compat()
    for k in ('serial', 'screenshot', 'control'):
        if args.get(k):
            _DEVICE_ARGS[k] = args[k]
    dev = _device_engine()            # 复用设备引擎（含 adb PATH 垫片与 multi_set 配置）
    cfg = _map_config()
    try:
        cfg.bind('Campaign')
    except Exception as e:
        return {'error': f'配置绑定 Campaign 失败: {type(e).__name__}: {e}'}
    # **配置必须跟着章节走**，否则 Campaign 会按错误关卡取参数（实测踩过：
    # 实例化 2-1，而 config.Campaign_Name 还是上一次跑过的 '12-4'）。
    # 同时把截图/输入后端显式设回我们的默认 —— bind('Campaign') 会把它们重置成引擎默认
    # （'auto' 会去跑性能基准）。
    import re as _re
    stage = ''
    m = _re.search(r'campaign_(\d+)_(\d+)$', chapter)
    if m:
        stage = '%s-%s' % (m.group(1), m.group(2))
    try:
        with cfg.multi_set():
            if stage:
                cfg.Campaign_Name = stage
            cfg.Emulator_ScreenshotMethod = str(_DEVICE_ARGS.get('screenshot') or 'scrcpy')
            cfg.Emulator_ControlMethod = str(_DEVICE_ARGS.get('control') or 'MaaTouch')
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
        return {'error': f'配置章节绑定失败: {type(e).__name__}: {e}', 'stage': stage}
    try:
        import importlib
        mod = importlib.import_module(chapter)
        inst = mod.Campaign(cfg, dev)
    except Exception as e:
        return {'error': f'实例化失败: {type(e).__name__}: {e}', 'chapter': chapter}
    # **种一帧**：ALAS 的方法假定 `device.image` 已存在，而它只在 screenshot() 之后才有。
    # 少了这一步，第一个动作就会死在 `AttributeError: 'Device' object has no attribute 'image'`
    # （实测踩过）。顺带也预热了截图后端。
    seeded = None
    try:
        import time as _t
        t0 = _t.time()
        dev.screenshot()
        seeded = round((_t.time() - t0) * 1000, 1)
    except Exception as e:
        seeded = f'失败: {type(e).__name__}: {e}'
    _CAMPAIGN['obj'] = inst
    _CAMPAIGN['chapter'] = chapter
    return {'chapter': chapter, 'instantiated': True, 'frame_seeded_ms': seeded,
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
    try:
        value = fn(*call_args)
    except Exception as e:
        # **`CampaignEnd` 是上游的"关卡已完成"信号，不是错误**（实测：_plan 跑到第三轮时抛出，
        # 而当时关卡确实已清）。此前把它当 error 报出来是语义错误 —— 这里改判为 completed。
        try:
            from module.exception import CampaignEnd as _CE
            _is_end = isinstance(e, _CE)
        except Exception:
            _is_end = type(e).__name__ == 'CampaignEnd'
        if _is_end:
            return {'name': name, 'ms': round((time.time() - t0) * 1000, 1),
                    'completed': True, 'reason': str(e) or 'CampaignEnd'}
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




# 客户端专属弹窗：「关卡 xxx 正在攻略中，请选择前往继续攻略或撤退 [撤退][立即前往]」
# 上游没有它的素材/处理器，导致 enter_map 干等 60s 后 GameStuckError（实测，见
# docs/s3-entry-sequence.md）。这里用 **OCR 识别文字** + 固定坐标点击来适配。
# 「关卡 xxx 正在攻略中…[撤退][立即前往]」是**客户端专属弹窗**，上游没有它的素材/处理器，
# 导致 enter_map 干等 60s 后 GameStuckError（实测 3-2，见 docs/s3-entry-sequence.md）。
#
# 判定方式：**像素特征**而不是 OCR —— 弹窗底部那枚红色「撤退」按钮是最稳的特征。
# 实测（1280x720）：弹窗帧红占比 **0.3271**，普通帧 **0.0000**（两帧），阈值取 0.15 余量充足。
# （OCR 路线走不通：该后端对每个识别字符解包 3 个值，空结果/字符集不匹配都报
#   `too many/not enough values to unpack`。）
_UNFINISHED_RED_BOX = (420, 470, 560, 550)     # 「撤退」按钮区域（留余量）
_UNFINISHED_RED_THRESHOLD = 0.15
_UNFINISHED_ABORT_XY = (479, 510)              # 实测有效


def _proactive_abort_worker(stop_event):
    """进图期间**主动**盯“正在攻略中”弹窗，一出现就点「撤退」。

    为什么需要：被动自愈要等 `enter_map` 卡满 60s 报 GameStuckError 才处理 ——
    用户实测反馈“每次都要等好久才处理”。这里用独立线程提前介入。

    **刻意走 adb 子进程**（exec-out screencap / input tap）而不走引擎设备层：
    引擎那套（scrcpy 流）不是线程安全的，而本线程与主线程（正在跑 enter_map）并发 ✗
    adb 每次都是独立进程，天然安全 ✓ 这也是坐标用固定 (479,510)（弹窗「撤退」，实测有效）的原因。
    """
    import subprocess as _sp
    import numpy as _np
    import cv2 as _cv2
    _base = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                          '..', '.runtime', 'venv314', 'Lib',
                                          'site-packages', 'adbutils', 'binaries', 'adb.exe'))
    _adb = _base if os.path.exists(_base) else 'adb'
    _serial = str(_DEVICE_ARGS.get('serial') or '127.0.0.1:16384')
    _x1, _y1, _x2, _y2 = _UNFINISHED_RED_BOX
    _ax, _ay = _UNFINISHED_ABORT_XY
    while not stop_event.is_set():
        try:
            out = _sp.run([_adb, '-s', _serial, 'exec-out', 'screencap', '-p'],
                          capture_output=True, timeout=15).stdout
            img = _cv2.imdecode(_np.frombuffer(out, _np.uint8), _cv2.IMREAD_COLOR)
            if img is not None and img.shape[0] >= _y2 and img.shape[1] >= _x2:
                patch = img[_y1:_y2, _x1:_x2]
                b = patch[:, :, 0].astype(int)
                g = patch[:, :, 1].astype(int)
                r = patch[:, :, 2].astype(int)
                if float((((r > 140) & (g < 100) & (b < 100))).mean()) > _UNFINISHED_RED_THRESHOLD:
                    _sp.run([_adb, '-s', _serial, 'shell', 'input', 'tap', str(_ax), str(_ay)],
                            timeout=10)
        except Exception:
            pass
        stop_event.wait(1.5)


def op_s3_abort_unfinished(args):
    """检测并关闭"关卡正在攻略中"弹窗（客户端专属；上游不认识它）。

    为什么需要：游戏里点「撤退」只是**离开地图**、保留可续战状态；此后进任何**别的**关卡
    都会弹这个对话框，上游不识别 → 干等 60s → `GameStuckError`（实测 3-2）。
    做法：量「撤退」按钮区域的红像素占比，超阈值就点它。`dry=true` 只检测不点击。
    """
    # **必须现抓一帧**：本 op 在 `enter_map` 卡住时被调用，而宿主缓存里（`_state['image']`）
    # 还是进图**之前**那一帧 —— 读缓存会永远看不到当下的弹窗（实测：自愈时报 red=0.0 而弹窗就在屏幕上）。
    import cv2 as _cv2
    try:
        _dev = _device_engine()
        _dev.screenshot()
        image = getattr(_dev, 'image', None)
        if image is None:
            image = _require_image()
    except Exception:
        image = _require_image()
    x1, y1, x2, y2 = _UNFINISHED_RED_BOX
    patch = image[y1:y2, x1:x2]
    try:
        r = patch[:, :, 0].astype(int)
        g = patch[:, :, 1].astype(int)
        b = patch[:, :, 2].astype(int)
        frac = float((((r > 140) & (g < 100) & (b < 100))).mean())
    except Exception as e:
        return {'error': f'{type(e).__name__}: {e}'}
    hit = frac > _UNFINISHED_RED_THRESHOLD
    out = {'unfinished_dialog': hit, 'red_frac': round(frac, 4),
           'threshold': _UNFINISHED_RED_THRESHOLD}
    if hit and not args.get('dry'):
        try:
            dev = _device_engine()
            from module.base.button import Button as _B
            x, y = _UNFINISHED_ABORT_XY
            area = (x - 1, y - 1, x + 1, y + 1)
            dev.click(_B(area=area, color=(), button=area, name='abort_unfinished'))
            out['clicked'] = list(_UNFINISHED_ABORT_XY)
        except Exception as e:
            out['click_error'] = f'{type(e).__name__}: {e}'
    return out


def op_s3_run_plan(args):
    """按关卡 IR 的**计划顺序**执行多个上游调用（S3 的实质机制）。

    为什么要它：单次 `battle_default` 清不掉图（实测 4 步后仍剩敌人）——
    上游 2-1 的计划是 `['battle_default','check_accessibility','clear_all_mystery',
    'fleet_boss.clear_boss']`，**多调用组合**才是完整流程。

    安全设计：
      - `dry_run` **默认 true**：只回planned_calls（离线可校验机制，不碰游戏）；
      - 真跑必须 `allow_actions=true`（与 s3_campaign_call 同一把锁）；
      - `max_seconds` 硬上限；任一步报错立即停（不硬撑）；
      - 只执行 IR 里 `battle_*` 方法的 calls，按方法序号排序，与上游 `BattlePlanRunner` 同序。

    注意：必须在**同一个进程**里完成 init → enter_map → map_init → 各调用，
    否则 `self.map` 等状态会丢（实测：换进程调用报 `'Campaign' object has no attribute 'map'`）。
    """
    import time as _t
    chapter = str(args.get('chapter') or 'campaign.campaign_main.campaign_2_1')
    dry = bool(args.get('dry_run', True))
    if not dry and not args.get('allow_actions'):
        return {'refused': True,
                'reason': '真跑需要 allow_actions=true（dry_run 默认可离线校验）'}
    # 舰队选择也要能由调用方指定（不同账号/关卡要用不同舰队；此前只走 init 的默认值）。
    init = op_s3_campaign_init({'chapter': chapter,
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
        return {'error': init['error'], 'stage': 'init'}
    inst = _CAMPAIGN.get('obj')

    # 从 IR 取该章节的计划（与 C# BattlePlanRunner 同序：按 battle_* 方法序号）
    import glob as _glob, os as _os, json as _json, re as _re
    stem = chapter.split('.')[-1]
    ir_path = None
    for pth in _glob.glob(_os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                        '..', 'data', 'campaign', '**', stem + '.json'),
                          recursive=True):
        ir_path = pth
        break
    if not ir_path:
        return {'error': '找不到 IR: %s' % stem}
    with open(ir_path, encoding='utf-8') as f:
        ir = _json.load(f)
    battles = [b for b in ((ir.get('campaign') or {}).get('battles') or [])
               if str(b.get('method', '')).startswith('battle_')]

    def _idx(name):
        m = _re.search(r'(\d+)', name)
        return int(m.group(1)) if m else 9999
    battles.sort(key=lambda b: _idx(b['method']))
    planned = []
    for b in battles:
        planned.append({'method': b['method'], 'calls': list(b.get('calls') or []),
                        'plan_complete': bool(b.get('plan_complete'))})
    import re as _re2
    _m = _re2.search(r'campaign_(\d+)_(\d+)$', chapter)
    stage = '%s-%s' % (_m.group(1), _m.group(2)) if _m else ''
    out = {'chapter': chapter, 'stage': stage,
           'tier': (ir.get('campaign') or {}).get('tier'),
           'planned_methods': planned, 'dry_run': dry,
           # `calls` 是**语义轨迹**（从 battle_* 方法体 AST 抽出来的，**同时包含顶层步骤与
           # 嵌套辅助调用**，例如 check_accessibility(grid) 是上游内部辅助方法、需要参数）。
           # 所以它**不是可逐条重放的清单** —— 真正的计划步骤是 `planned_methods` 里的
           # `battle_*` 方法本身（它们内部会去做清神秘/BOSS/可达性检查）。
           'semantic_trace': [c for b in planned for c in b['calls']],
           'plan_steps': [b['method'] for b in planned]}
    if dry:
        out['note'] = ('dry_run：未触碰游戏。真跑需 allow_actions=true，'
                       '并会在同一进程内完成 init→enter_map→map_init→各调用')
        return out

    # ---- 真跑 ----
    t_start = _t.time()
    max_s = float(args.get('max_seconds') or 600)
    steps = []
    # **顺序要求**（见 docs/s3-entry-sequence.md）：ensure_chapter 必须在 get_entrance 之前，
    # 否则入口坐标取不到（空 Button）。这个坑我自己踩过一次，这里必须显式做。
    if stage:
        r = op_s3_campaign_call({'name': 'campaign_ensure_chapter',
                                 'args': [int(stage.split('-')[0])], 'allow_actions': True})
        steps.append({'step': 'ensure_chapter', 'ms': r.get('ms'), 'error': r.get('error')})
    # 进图前先清掉"未完成出击"（客户端弹窗，上游不识别；否则本关必卡 60s）
    try:
        _ab = op_s3_abort_unfinished({'dry': False})
        steps.append({'step': 'abort_unfinished', 'dialog': _ab.get('unfinished_dialog')})
    except Exception:
        pass
    r = (op_s3_campaign_call({'name': 'campaign_get_entrance', 'args': [stage],
                              'store': 'ENTRANCE', 'allow_actions': True})
         if not (steps and steps[-1].get('error')) else {'error': 'skipped'})
    steps.append({'step': 'get_entrance', 'ms': r.get('ms'), 'error': r.get('error')})
    if not r.get('error'):
        # **主动盯防**（用户实测反馈：被动自愈要等 60s 才处理，太久）：
        # 进图期间后台线程每 1.5s 查一次"正在攻略中"弹窗，一出现就点掉。
        import threading as _th
        _stop_ev = _th.Event()
        _watcher = _th.Thread(target=_proactive_abort_worker, args=(_stop_ev,), daemon=True)
        _watcher.start()
        try:
            r = op_s3_campaign_call({'name': 'enter_map', 'args': ['@ENTRANCE', 'normal'],
                                     'allow_actions': True})
            steps.append({'step': 'enter_map', 'ms': r.get('ms'), 'error': r.get('error')})
        finally:
            _stop_ev.set()
            _watcher.join(timeout=5)
        # **自愈**：进图失败且像"卡住"时，多半是那个客户端弹窗挡着
        # （「关卡 xxx 正在攻略中…[撤退][立即前往]」）——它出现在 **enter_map 过程当中**，
        # 所以进图前那次检测抓不到（实测曾连续两轮各卡 60s，还被误判成"那关有问题"）。
        if r.get('error') and 'Stuck' in str(r.get('error')):
            ab = op_s3_abort_unfinished({'dry': False})
            steps.append({'step': 'enter_map_abort',
                          'dialog': ab.get('unfinished_dialog'),
                          'red_frac': ab.get('red_frac')})
            r2 = op_s3_campaign_call({'name': 'enter_map',
                                     'args': ['@ENTRANCE', 'normal'],
                                     'allow_actions': True})
            steps.append({'step': 'enter_map_retry', 'ms': r2.get('ms'),
                          'error': r2.get('error')})
    if not steps[-1].get('error'):
        # **上游 run() 的顺序是 handle_map_fleet_lock() 再 map_init()** ——
        # 之前只调 map_init，导致 execute_a_battle() 抛 KeyError: ()（实测：20 轮里只有 1 轮真打了）✗
        if not steps or not steps[-1].get('error'):
            _fl = op_s3_campaign_call({'name': 'handle_map_fleet_lock', 'allow_actions': True})
            steps.append({'step': 'handle_map_fleet_lock', 'ms': _fl.get('ms'),
                          'error': _fl.get('error')})
        # `map_init` 里第一次 `update()` 就可能因**退化机位**失败：
        # 实测 7-1（8x3 三行图）同一张图里，一次成功、一次报
        # `MapDetectionError: Vanish point and distant point too close`
        # （日志里 `vanish_point == distant_point == (654, -1425)`）——
        # 三行图的网格线在透视里近乎平行，机位不巧时消失点算到无穷远，几何退化。
        # 这不是"图不支持"，所以**换个机位重试**。
        #
        # **但"没抛异常"不等于"结果可用"**（实测 1-4 踩到）：1-4 的 `map_init` 全程无异常，
        # 可视图的**格距被判错**（检出 9 行 / 该图只有 3 行，同帧 `tile_center: 0.636 bad match`）
        # → 敌人落到 `map_data` 里不是 `ME` 的格子上 → `grid_info.update()` 把 `is_enemy` 丢掉
        # → 上游地图里一个敌人都没有 → `battle_0` 报 `No battle executed` → 十次无战果 → 撤退。
        # 所以这里加一道**只读一致性校验**：视图认出了敌人/BOSS，而地图侧一个都没有 ⇒ 判为失败重试。
        def _map_init_health():
            h = {}
            try:
                v = inst.view
                h['view_cells'] = len(getattr(v, 'grids', {}) or {})
                h['view_enemies'] = len(v.select(is_enemy=True))
                h['view_boss'] = len(v.select(is_boss=True))
            except Exception as e:
                h['view_error'] = f'{type(e).__name__}: {e}'
            try:
                h['map_enemies'] = len(inst.map.select(is_enemy=True))
                h['map_boss'] = len(inst.map.select(is_boss=True))
                h['camera'] = [int(x) for x in inst.camera]
                h['camera_in_bounds'] = bool(
                    0 <= h['camera'][0] <= int(inst.map.shape[0])
                    and 0 <= h['camera'][1] <= int(inst.map.shape[1]))
            except Exception as e:
                h['map_error'] = f'{type(e).__name__}: {e}'
            view_ships = (h.get('view_enemies') or 0) + (h.get('view_boss') or 0)
            map_ships = (h.get('map_enemies') or 0) + (h.get('map_boss') or 0)
            h['consistent'] = not (view_ships > 0 and map_ships == 0)
            return h

        # 三种纠正手段，逐个试：
        #   ① 先直接重试一次（有时只是抓帧时机问题）；
        #   ② 上游自己的 `ensure_edge_insight()` —— 它靠边界线把相机重新锚到角上，
        #      是上游 `full_scan` 在 `map.update` 判失败时用的**原生恢复手段**；
        #   ③ 设备级滑动换机位（不依赖任何识别结果，前面已证明 `map_swipe`/`_map_swipe` 用不了）。
        _recover = [
            ('retry', None),
            ('ensure_edge_insight', {'name': 'ensure_edge_insight', 'args': [False],
                                     'allow_actions': True}),
            ('device_swipe', None),
        ]
        _init_attempts = []
        for _att, (_label, _call) in enumerate(_recover):
            r = op_s3_campaign_call({'name': 'map_init', 'args': ['@MAP'],
                                     'allow_actions': True})
            _entry = {'attempt': _att + 1, 'recover': _label, 'ms': r.get('ms'),
                      'error': r.get('error')}
            if not r.get('error'):
                _entry['health'] = _map_init_health()
            _init_attempts.append(_entry)
            if not r.get('error') and (_entry.get('health') or {}).get('consistent'):
                break
            # **把失败这一刻的现场帧存下来**：离线复现是定位这类问题的唯一可靠手段，
            # 而"失败帧"必须在这一刻抓 —— 事后再截图，相机早被重试挪走了（实测踩过：
            # 事后抓到的帧检测完全正常，21/21 格，反而把结论带偏 ✗）。
            try:
                import cv2 as _cv2
                _img = getattr(inst.device, 'image', None)
                if _img is not None:
                    _fp = os.path.normpath(os.path.join(
                        os.path.dirname(os.path.abspath(__file__)), '..', 'data',
                        '_map_init_fail_%s_att%d.png' % (
                            _CAMPAIGN.get('chapter', 'x').split('.')[-1], _att + 1)))
                    _cv2.imwrite(_fp, _cv2.cvtColor(_img, _cv2.COLOR_RGB2BGR))
                    _entry['saved_frame'] = _fp
            except Exception as _e:
                _entry['save_frame_error'] = f'{type(_e).__name__}: {_e}'
            if r.get('error') and 'Vanish point' not in str(r.get('error')) \
                    and 'No vertical line' not in str(r.get('error')):
                break                      # 别的错误不靠挪机位解决
            if _att + 1 >= len(_recover):
                break
            _next = _recover[_att + 1][0]
            try:
                if _next == 'ensure_edge_insight':
                    _rc = op_s3_campaign_call({'name': 'ensure_edge_insight',
                                               'args': [False], 'allow_actions': True})
                    _entry['recover_error'] = _rc.get('error')
                elif _next == 'device_swipe':
                    _mv = op_device_swipe({'x1': 760, 'y1': 394,
                                           'x2': 960 if _att % 2 == 0 else 560,
                                           'y2': 394, 'duration': 0.4})
                    _entry['recover_error'] = _mv.get('error')
            except Exception as _e:
                _entry['recover_error'] = f'{type(_e).__name__}: {_e}'
        steps.append({'step': 'map_init', 'ms': _init_attempts[-1].get('ms'),
                      'error': _init_attempts[-1].get('error'),
                      'attempts': _init_attempts})
    # **从半途状态接着打**：`map_init` 会把 `battle_count` 清 0（map_data_init），而 `battle_count`
    # 决定 `battle_function()` 选哪个 `battle_N`（campaign_base.py:79-92）。所以"进图时小怪已经
    # 清完、只剩 BOSS"这种状态下，清 0 只会让上游去跑 battle_0（清路障）→ 十次无战果 → 撤退。
    # 传 `battle_count` 就能显式复位到正确的回合（上游自己的语义，不是我另造的逻辑）。
    if args.get('battle_count') is not None:
        try:
            _want = int(args['battle_count'])
            _had = getattr(inst, 'battle_count', None)
            inst.battle_count = _want
            steps.append({'step': 'set_battle_count', 'from': _had, 'to': _want})
        except Exception as e:
            steps.append({'step': 'set_battle_count',
                          'error': f'{type(e).__name__}: {e}'})
    # 执行**计划步骤本身**（battle_* 方法），而不是逐条重放语义轨迹 —— 见上面说明。
    # `repeat_until_cleared`：对齐上游 `CampaignBase.run()` 的**循环**语义
    # （一轮计划 ≠ 清图；上游是循环调用直到满足结束条件）。默认关闭，开启时按 max_rounds 上限。
    repeat = bool(args.get('repeat_until_cleared'))
    max_rounds = int(args.get('max_rounds') or 3)

    # **诊断出口**：`stop_after='map_init'` 时，进图 + 图内初始化做完就停，**不打任何一场**。
    # 用途：把"进图/识别"这一段单独拿出来量（视图检出多少格、信息条在不在、相机对不对），
    # 而不用为了看一眼状态先打一场（此前只能靠完整跑一遍再从日志里反推 ✗）。
    if str(args.get('stop_after') or '') == 'map_init':
        out['stopped_after'] = 'map_init'
        out['steps'] = steps
        return out

    def _sortie_state():
        """用**上游自己的状态**判断是否该继续（本地 `enemies_left` 已被两次证明不可靠）。

        返回 (continue_needed, reason)：
          - 已离开地图（`is_in_map()` 为假）→ 出击已结束，不需要再跑；
          - `map_clear_percentage >= 100` → 图已清，不需要再跑；
          - 否则继续（受 max_rounds / max_seconds 约束）。
        这两个信号都来自上游运行时，比本地标志计数可靠得多。
        """
        try:
            if not inst.is_in_map():
                return False, 'left_map'
        except Exception:
            pass
        try:
            pct = getattr(inst, 'map_clear_percentage', None)
            if isinstance(pct, (int, float)) and float(pct) >= 100.0:
                return False, 'map_clear_100'
        except Exception:
            pass
        return True, 'still_in_map'

    _round = 0
    while True:
        _round += 1
        # **改用上游自己的调度入口**：`campaign_base.run()` 的循环体就是
        #   `for _ in range(20): execute_a_battle()`（收到 CampaignEnd 即停）
        # 只调 IR 里的 `battle_0`/`battle_6` 等于只做了上游逻辑的一小部分 → **清不完**
        # （用户实测反馈"并没有完全打完"，根因即此）。轮数由 max_rounds 控制，
        # 上游默认 20 —— 建议调用方传 `--max-rounds 20`。
        # **先触发上游的 BOSS 扫描确认**：本客户端 BOSS 图标识别失败（实测 3 个标志里
        # 没有 is_boss ✗），导致 `battle_6` 的 `if boss:` 分支被跳过 -> 十次无战果 ->
        # 上游自己撤退（用户实测："全清完小怪后只剩boss就主动撤退" ✓）。
        # `full_scan_find_boss()`（camera.py:530）正是用候选出生点扫描确认 BOSS 的上游能力；
        # 正常流程会在只剩 BOSS 时走它，而我的执行器此前从未触发 ✗。
        for _step_name in ('full_scan_find_boss', 'execute_a_battle'):
            if _t.time() - t_start > max_s:
                steps.append({'round': _round, 'step': _step_name, 'skipped': '超过 max_seconds'})
                break
            if steps and steps[-1].get('error'):
                steps.append({'round': _round, 'step': _step_name, 'skipped': '前一步出错，停止'})
                break
            r = op_s3_campaign_call({'name': _step_name, 'allow_actions': True})
            # **客观进度**：上游的 map_clear_percentage（属性是 0..1）与弹药数 ——
            # 此前我用 campaign_end=True 当"清图"判据，被用户当场否证（那其实是 withdraw 路径）。
            _pct = None
            _ammo = None
            try:
                _pct = round(float(getattr(inst, 'map_clear_percentage', -1)) * 100, 1)
            except Exception:
                pass
            try:
                _ammo = getattr(inst, 'ammo_count', None)
            except Exception:
                pass
            # **直接测量**（不再靠读代码推断）：battle_count 决定 `battle_function` 选哪个
            # `battle_N`；若它一直不递增，就会永远停在 battle_0（清路障）而打不到 BOSS ✗
            _bc = None
            _cfgkeys = {}
            try:
                _bc = getattr(inst, 'battle_count', None)
            except Exception:
                pass
            for _k in ('MAP_CLEAR_ALL_THIS_TIME', 'POOR_MAP_DATA',
                       'MAP_HAS_MOVABLE_NORMAL_ENEMY', 'Error_HandleError'):
                try:
                    _cfgkeys[_k] = getattr(getattr(inst, 'config', None), _k, None)
                except Exception:
                    pass
            steps.append({'round': _round, 'step': _step_name, 'ms': r.get('ms'),
                          'error': r.get('error'), 'completed': r.get('completed'),
                          'map_clear_pct': _pct, 'ammo': _ammo,
                          'battle_count': _bc, 'cfg': _cfgkeys})
            if r.get('completed'):
                # 上游宣布关卡完成 —— 这是**正常收尾**，不需要再跑下一轮
                out['campaign_end'] = True
                out['campaign_end_step'] = _step_name
                break
        else:
            # 本轮跑完：判断是否还需要再来一轮
            if not repeat or _round >= max_rounds or _t.time() - t_start > max_s:
                break
            need, why = _sortie_state()
            steps.append({'round': _round, 'check': 'sortie_state', 'value': why})
            if not need:
                out['stop_reason'] = why      # 上游语义给出的结束原因
                break
            continue
        break
    out['steps'] = steps
    try:
        out['map_clear_pct_final'] = round(float(getattr(inst, 'map_clear_percentage', -1)) * 100, 1)
        out['cleared'] = bool(out['map_clear_pct_final'] >= 100)
    except Exception:
        out['cleared'] = None
    out['elapsed_s'] = round(_t.time() - t_start, 1)
    out['stopped_early'] = bool(steps and steps[-1].get('error'))
    return out


def _map_config():
    """S2 需要上游配置（`DETECTION_BACKEND` 等决定用 Homography 还是 Perspective 后端）。
    做法与 cached_rule_check 一致：用上游自己的 AzurLaneConfig，不自己造配置层。"""
    from module.config.config import AzurLaneConfig
    return AzurLaneConfig('alas')


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

    返回检测出的网格规模与四边标志。**负样本也是有效证据**：不在地图上时应给出
    "检测不到"，而不是崩掉或给出错误坐标。真机正样本要求"游戏停在地图上"
    （出击之后的画面；大世界地图是免费的，但本账号 OS 未解锁）。
    """
    import module.map_detection.view as view_mod
    image = _require_image()
    # 几何放大：**单行/扁格子**的小地图（例：进图后的 1-1，7 格一行）竖直分隔线太短，
    # 默认阈值与降阈值都检不出（实测 trace: inner_v.lines=0）。等比放大能把短竖线拉长到
    # 检测阈值以上；格索引与逐格标志都是尺度无关的，所以不影响返回语义。
    _scale = int(args.get('upscale') or 1)
    if _scale > 1:
        import cv2 as _cv2
        image = _cv2.resize(image, None, fx=_scale, fy=_scale,
                            interpolation=_cv2.INTER_CUBIC)
    apply_numpy2_compat()
    apply_points_empty_compat()
    cfg = _map_config()
    # 上游有两个检测后端（Homography / Perspective），由 config.DETECTION_BACKEND 选。
    # 允许显式指定：真机上出现过 homography 后端"找不到水平线/垂直线"而画面明明有网格，
    # 这时要能立刻对比另一个后端，而不是猜。
    if args.get('backend'):
        cfg.DETECTION_BACKEND = args['backend']
    out = {'backend': str(getattr(cfg, 'DETECTION_BACKEND', ''))}
    # 作业海域（OS）的地图要用另一套遮罩：View(config, mode='os') 会切到
    # ASSETS.ui_mask_os_in_map（view.py:47-48），网格类也换成 OS 的。
    mode = str(args.get('mode') or 'main')
    out['mode'] = mode
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
        return out
    for name in ('left_edge', 'right_edge', 'upper_edge', 'lower_edge'):
        if hasattr(v, name):
            out[name] = bool(getattr(v, name))
    # 上游语义：`load(image)` 自己负责找网格；**不是地图画面时它会抛
    # MapDetectionError('No map grids found')** —— 那是正常的负样本信号，不是缺陷。
    # 要把它与"接线错误"（少传参数、np.stack 报错之类）区分开，所以分类型捕。
    import module.map_detection.utils as md_utils
    from module.map_detection.view import MapDetectionError

    def _rebuild_view(threshold, peaks=None):
        """按给定 Hough 阈值重建 cfg + View（阈值改了必须重建，配置是构造期读入的）。

        `peaks` 可覆盖 `INTERNAL_LINES_FIND_PEAKS_PARAMETERS`（scipy.find_peaks 参数）。
        为什么需要它：现场帧（进图后的 1-1，7 格单行）报 `No vertical line detected`，
        而 trace 显示 `inner_v.peaks_raw=784 / lines=0` —— **峰找得到，卡在"峰→线"**；
        降 Hough 阈值（50/40）与放大都无效（已证），所以这一档要动**峰参数本身**。
        """
        nonlocal cfg, v
        cfg = _map_config()
        if args.get('backend'):
            cfg.DETECTION_BACKEND = args['backend']
        try:
            cfg.INTERNAL_LINES_HOUGHLINES_THRESHOLD = threshold
        except Exception:
            pass
        if peaks:
            try:
                merged = dict(getattr(cfg, 'INTERNAL_LINES_FIND_PEAKS_PARAMETERS', {}) or {})
                merged.update(peaks)
                cfg.INTERNAL_LINES_FIND_PEAKS_PARAMETERS = merged
                # 边线只跟着降 prominence；不动它的 height（那是"亮线"区间，
                # 改了会把边线与内部线混为一谈）。
                if 'prominence' in peaks:
                    em = dict(getattr(cfg, 'EDGE_LINES_FIND_PEAKS_PARAMETERS', {}) or {})
                    em['prominence'] = peaks['prominence']
                    cfg.EDGE_LINES_FIND_PEAKS_PARAMETERS = em
            except Exception:
                pass
        if mode == 'os':
            cfg.Scheduler_Command = 'OpsiDaily'
            v = view_mod.View(cfg, mode='os', grid_class=grid_class) if grid_class \
                else view_mod.View(cfg, mode='os')
        else:
            v = view_mod.View(cfg)

    # 失败后**降阈值重试**：困难图的竖线在倾斜 3D 平面上票数摊开，
    # 默认阈值 75 一条都拟合不出 → 消失点几何退化（Vanish point ... too close）。
    # 实测：2-1 / 10-4 用默认即可，困难 1-4 需要 50；但**不能全局降** ——
    # 降到 40 时 2-1 会多检出一整圈（39 格 / shape [7,4] 而非 24 / [5,3]）。
    # 所以按"默认优先、失败才降"的顺序试，与上游自己的
    # search_tile_center → corner → rectangle 多策略同思路。
    _last_reason = None
    # 档位 = (Hough 阈值, 峰参数覆盖)。
    #   前 3 档：**峰参数**（现场 1-1 报 No vertical line detected 而 peaks_raw=784，
    #           说明卡在"峰→线"，所以先降 find_peaks 的门槛：height 下限 150→130→110、
    #           prominence 10→7）。注意 height 上界 (255-33=222) 不动：那是"内部线/边线"的
    #           分界，动了会把两者混起来。
    #   后 2 档：原有的**降 Hough 阈值**（困难图需要 50；40 是兜底）。
    # 顺序仍是"默认优先、失败才降"，与上游多策略同思路；且实测 2-1 用默认即可，
    # 所以正常画面不会走到后面这些档（也就不会引入额外的假阳性或耗时）。
    _TIERS = (
        (None, None),
        (None, {'height': (130, 222), 'prominence': 7}),
        (None, {'height': (110, 222), 'prominence': 5}),
        (50, None),
        (40, None),
    )
    for _thr, _peaks in _TIERS:
        if _thr is not None or _peaks is not None:
            try:
                _rebuild_view(_thr if _thr is not None else 75, _peaks)
            except Exception as e:
                _last_reason = f'{type(e).__name__}: {e}'
                break
        try:
            v.load(image)
            out['load'] = 'ok'
            out['threshold_used'] = int(getattr(cfg, 'INTERNAL_LINES_HOUGHLINES_THRESHOLD', 0))
            if _peaks:
                out['peaks_used'] = {k: (list(v) if isinstance(v, tuple) else v)
                                     for k, v in _peaks.items()}
            out['tier'] = f'thr={_thr} peaks={_peaks}'
            _last_reason = None
            break
        except MapDetectionError as e:
            # 上游自己的负样本信号；先记住，换下一档再试
            _last_reason = str(e)
            continue
        except Exception as e:
            set_os_mask_mode(False)
            # 非地图画面上上游会在 np.stack 上抛 TypeError（它假定调用方已确认在地图上）。
            # 产品侧需要在任意画面上安全地"试一试"，所以这里也归为未检测到，
            # 但**保留原始异常文本**，免得把"接线的锅"当成"画面的锅"。
            out['load'] = 'negative'
            out['detected'] = False
            out['reason'] = f'{type(e).__name__}: {e}'
            return out
    if _last_reason is not None:
        set_os_mask_mode(False)
        out['load'] = 'negative'
        out['detected'] = False
        out['reason'] = _last_reason
        out['thresholds_tried'] = [t for t in (None, 50, 40)]
        return out
    set_os_mask_mode(False)      # 用完即复位，不影响后续战役检测
    # 回退结果的**几何合理性闸门**：降阈值会把非地图画面也"检出"成一片网格
    # （实测战役菜单 os_map.png 在 thr=50 下报 59 格 / shape [7,7]），
    # 而真地图在回退阈值下是**干净矩形**（困难 1-4 → 21 格 = 7x3）。
    # 判据：回退生效时，格数必须等于 (sx+1)*(sy+1)；给不出干净矩形就当没检出。
    # 只在回退路径上卡这一道 —— 默认阈值下的正常结果不适用（10-4 本来就缺 6 格是 UI 遮挡）。
    if _thr is not None:
        _shape = getattr(v, 'shape', None)
        _grids = getattr(v, 'grids', None)
        if _shape is None or not isinstance(_grids, dict):
            out['load'] = 'negative'
            out['detected'] = False
            out['reason'] = 'fallback 未给出网格'
            return out
        _sx, _sy = int(_shape[0]), int(_shape[1])
        if len(_grids) != (_sx + 1) * (_sy + 1):
            out['load'] = 'negative'
            out['detected'] = False
            out['reason'] = ('fallback 网格不干净（%d 格 vs %dx%d=%d），判为非地图画面'
                             % (len(_grids), _sx + 1, _sy + 1, (_sx + 1) * (_sy + 1)))
            return out
    if hasattr(v, 'predict'):
        try:
            v.predict()
            out['predict'] = 'ok'
        except Exception as e:
            out['predict'] = f'{type(e).__name__}: {e}'
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
    # 实测（docs/device-engine.md "误报"一节）：战役章节选择页会把章节预览图误判成地图
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
    out['ships'] = len(ships)
    out['ship_tiles'] = ships
    # 注意：**只在战役模式（main）下用这条判据**。海域图（mode=os）的逐格标志在本客户端
    # 本来就为空（OS 网格类的模板与本客户端图标不匹配，S2 阶段已查明并记录），
    # 若一并要求船标志会把**整类海域图误杀** —— 实测产品路径因此从 5/5 掉到 4/5。
    if bool(args.get('require_ships', True)) and mode != 'os' and not ships and out['detected']:
        out['detected'] = False
        out['reason'] = ('检出网格但**没有任何船标志**（%s 格），判为非战场画面'
                         % out.get('grid_count'))

    # 几何放大重试（自动升档）：默认与降阈值都没检出时，把图放大再试 —— 见函数开头说明。
    # 只对战役模式、且不是"已经在放大档里"时触发，避免递归失控。
    # 注意：**默认不做**自动升档 —— 实测放大对本项目的单行小地图无效
    # （放大同时也放大了掩膜与线段参数，比例不变），却会让每个负样本多花 2-3 倍时间。
    # 需要时显式传 auto_upscale=true。
    if (_scale == 1 and mode == 'main' and not out.get('grid_count')
            and args.get('auto_upscale') and not args.get('no_upscale_retry')):
        for _s in (2, 3):
            _retry = dict(args)
            _retry['upscale'] = _s
            _retry['no_upscale_retry'] = True
            _r2 = op_map_detect(_retry)
            if _r2.get('grid_count'):
                _r2['upscale_used'] = _s
                _r2['upscale_note'] = '默认阈值与降阈值均未检出，放大 %dx 后检出' % _s
                return _r2
        out['upscale_tried'] = [2, 3]
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
            lines = [m for m in cap.messages
                     if 'similarity' in m or 'globe_center' in m or 'homo_storage' in m]
            out['log_lines'] = [str(m).strip() for m in lines][-6:]
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
    cfg = _map_config()
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
    # 小图卡点的真凶（scipy brute 的 finish=fmin 越界）—— 见函数注释
    apply_brute_finish_none_compat()
    cfg = _map_config()
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


def _device_engine():
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
    key = (serial, shot, ctrl)
    if _DEVICE_OBJ is not None and _DEVICE_KEY == key:
        return _DEVICE_OBJ
    cfg = _map_config()
    # 两个坑（实测踩过，缺一不可）：
    #   1) 必须先导入 `module.device.pkg_resources` —— adbutils 会 import pkg_resources，
    #      ALAS 靠这个桩顶替，而桩**只有先被导入才生效**（真实运行由 device.py 保证）；
    #   2) 配置必须放进 `cfg.multi_set()` —— 否则 ALAS 的配置系统会回写覆盖，
    #      Serial 变回 'auto'，设备探测失败，Device.__init__ 重试 4 次后抛
    #      `RequestHumanTakeover`（消息还是空的，极具误导性）。
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
    with cfg.multi_set():
        cfg.Emulator_Serial = serial
        cfg.Emulator_ScreenshotMethod = shot
        cfg.Emulator_ControlMethod = ctrl
    from module.device.device import Device
    _DEVICE_OBJ = Device(cfg)
    _DEVICE_KEY = key
    return _DEVICE_OBJ


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
        dev.back()
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

    # **通道顺序必须在这里定死**（这条链路踩过两次，代价是一版错误的垫片）：
    #   - `raw=True` 直接调后端原始实现，**绕过了 `method/adb.py:134` 的 BGR2RGB** → 拿到的是 BGR；
    #   - 落盘用 `cv2.imwrite`（把数组当 BGR）＋ 读回用 `load_image`（PIL，忠实读）＝ **一次 R/B 互换**。
    # 所以先统一成"ALAS 约定的 RGB"，再用 `RGB2BGR` 忠实落盘；`load_image` 读回来的就正好是
    # 引擎交给上游的那张图 E —— 与夹具路径（`screenshot_load`）同源，也与上游内部一致。
    import tempfile
    import numpy as _np
    import cv2 as _cv2
    from module.base.utils import load_image
    fd, tmp = tempfile.mkstemp(suffix='.png')
    os.close(fd)
    try:
        if isinstance(img, _np.ndarray) and img.ndim == 3 and img.shape[2] == 3:
            engine_img = _cv2.cvtColor(img, _cv2.COLOR_BGR2RGB) if raw else img
            _cv2.imwrite(tmp, _cv2.cvtColor(engine_img, _cv2.COLOR_RGB2BGR))
        else:
            engine_img = img
            img.save(tmp)
        _state['image'] = load_image(tmp)
        _state['path'] = tmp
    except Exception as e:
        return {'error': f'{type(e).__name__}: {e}', 'capture_ms': round(cap_ms, 1)}
    return {'capture_ms': round(cap_ms, 1), 'method': method, 'raw': raw,
            'shape': list(getattr(_state['image'], 'shape', ()) or ())}


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
    'cached_rule_check': op_cached_rule_check,
    'page_positive_control': op_page_positive_control,
    'rule_positive_control': op_rule_positive_control,
    'map_detection_assets': op_map_detection_assets,
    's3_abort_unfinished': op_s3_abort_unfinished,
    's3_run_plan': op_s3_run_plan,
    's3_campaign_init': op_s3_campaign_init,
    's3_campaign_info': op_s3_campaign_info,
    's3_campaign_call': op_s3_campaign_call,
    's3_probe_view': op_s3_probe_view,
    'device_capture_set': op_device_capture_set,
    'device_configure': op_device_configure,
    'device_info': op_device_info,
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

