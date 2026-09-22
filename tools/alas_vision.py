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

    def _rebuild_view(threshold):
        """按给定 Hough 阈值重建 cfg + View（阈值改了必须重建，配置是构造期读入的）。"""
        nonlocal cfg, v
        cfg = _map_config()
        if args.get('backend'):
            cfg.DETECTION_BACKEND = args['backend']
        try:
            cfg.INTERNAL_LINES_HOUGHLINES_THRESHOLD = threshold
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
    for _thr in (None, 50, 40):
        if _thr is not None:
            try:
                _rebuild_view(_thr)
            except Exception as e:
                _last_reason = f'{type(e).__name__}: {e}'
                break
        try:
            v.load(image)
            out['load'] = 'ok'
            out['threshold_used'] = int(getattr(cfg, 'INTERNAL_LINES_HOUGHLINES_THRESHOLD', 0))
            _last_reason = None
            break
        except MapDetectionError as e:
            # 上游自己的负样本信号；先记住，换更低的阈值再试
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
        # ALAS 的 `Device.screenshot()` 返回 **numpy 数组**（BGR），不是 PIL Image ——
        # 直接 `.save()` 会报 `'numpy.ndarray' object has no attribute 'save'`（实测踩过）。
        if hasattr(img, 'save'):
            img.save(path)
        else:
            import cv2 as _cv2
            _cv2.imwrite(path, img)
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
    import time as _time
    dev = _device_engine()
    t0 = _time.time()
    dev.swipe(args['x1'], args['y1'], args['x2'], args['y2'],
              duration=args.get('duration', 0.2))
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

    # 存进宿主的方式与 `screenshot_load` 保持**逐字节一致**：先存临时 PNG 再用上游
    # `load_image` 读回。设备层返回的是 BGR numpy，而夹具路径走的是 `load_image`，
    # 直接塞进去可能踩通道顺序的坑；这里多花 ~10ms 换"与夹具路径完全同源"。
    import tempfile
    from module.base.utils import load_image
    fd, tmp = tempfile.mkstemp(suffix='.png')
    os.close(fd)
    try:
        import cv2 as _cv2
        _cv2.imwrite(tmp, img)
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
    'device_capture_set': op_device_capture_set,
    'device_configure': op_device_configure,
    'device_info': op_device_info,
    'device_screencap': op_device_screencap,
    'device_click': op_device_click,
    'device_swipe': op_device_swipe,
    'device_back': op_device_back,
    'map_detect': op_map_detect,
    'map_detect_trace': op_map_detect_trace,
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
