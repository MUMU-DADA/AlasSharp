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
    """直接调用上游 Button.match（底层 cv2.matchTemplate，含上游的参数顺序与自动交换）。"""
    image = _require_image()
    button = _resolve(args['asset'])
    offset = args.get('offset', 30)
    similarity = args.get('similarity', 0.85)
    appear = bool(button.match(image, offset=offset, similarity=similarity))
    return {
        'match': appear,
        'offset': offset,
        'similarity': similarity,
        'button_offset': list(button.button) if button._button_offset is not None else None,
    }


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


OPS = {
    'ping': op_ping,
    'set_server': op_set_server,
    'screenshot_load': op_screenshot_load,
    'asset_info': op_asset_info,
    'appear_on': op_appear_on,
    'appear_on_batch': op_appear_on_batch,
    'button_match': op_button_match,
    'template_match': op_template_match,
    'ocr': op_ocr,
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
                          ensure_ascii=False)
    if req.get('op') == 'shutdown':
        return json.dumps({'id': req.get('id'), 'ok': True, 'result': {'bye': True}},
                          ensure_ascii=False)
    return json.dumps(handle(req), ensure_ascii=False)
