#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
识图引擎 worker（路线甲 · S1'）。

设计原则（本人明确要求）：
    **识图不重写。** 本 worker 只做三件事：
      1) 复用上游的 `Button` / `Template` 对象（直接 import `module.<x>.assets`，不重建）；
      2) 直接调用上游的方法（`appear_on` / `match` / `match_result`），
         底层就是上游的 cv2 调用序列；
      3) 把结果序列化成 JSON 回给 C#。
    C# 侧一行图像算法都不实现 —— 手工移植 cv2 已被实测证伪：
    OpenCV 会按模板/搜索区的尺寸比切换相关算法，同一块内容在不同尺寸下
    得分不同（实测同一位置 0.7487 vs 1.0000），逐位一致不可能做到。

协议：一行一个 JSON（请求/响应各一行），UTF-8。
    请求: {"id": 1, "op": "appear_on", "args": {...}}
    响应: {"id": 1, "ok": true, "result": {...}}  或  {"id": 1, "ok": false, "error": "..."}

用法：
    python vision_worker.py --stdio            # 标准输入输出（默认）
    python vision_worker.py --tcp 34567        # 监听 127.0.0.1:34567
"""
from __future__ import annotations

import argparse
import importlib
import json
import os
import sys
import time
import traceback

FORK = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                     '..', '..', 'my fork project', 'AzurLaneAutoScript'))
os.chdir(FORK)
sys.path.insert(0, FORK)

# ⚠️ 必须在 import 任何 ALAS 模块**之前**把协议流与普通输出分开。
#    上游 `module.logger` 在被 import 时就会往 stdout 打横幅（"------ START ------"），
#    若协议也写在 stdout 上，C# 侧读到的第一行是横幅而不是 JSON（实测踩过两次：
#    把 dup 放在 main() 里太晚，横幅早就打出去了）。
#    做法：复制一份真正的 fd 1 专供协议，再把 fd 1 指向 stderr。
_PROTO = None
if '--tcp' not in sys.argv:
    _PROTO = os.fdopen(os.dup(1), 'w', encoding='utf-8', newline='\n')
    os.dup2(2, 1)                       # 此后 print / logger 一律进 stderr
    sys.stdout = sys.stderr

import module.device.pkg_resources  # noqa: E402  桩必须最先导入
import module.config.server as server_module  # noqa: E402

# 只在 worker 内部使用：把当前截图与已解析的素材对象缓存起来
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
    """取 Button 当前服的期望色（用上游自己的属性解析）。"""
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
    }


def op_set_server(args):
    s = args['server']
    if s not in ('cn', 'en', 'jp', 'tw'):
        raise ValueError(f'非法服务器: {s}')
    server_module.server = s
    # 服别变了，已缓存的素材对象要重新解析（上游用 cached_property）
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
        info['image_count'] = len(obj.image) if info['is_gif'] else 1
        if info['is_gif']:
            info['frame_shapes'] = [list(f.shape) for f in obj.image]
        else:
            info['image_shape'] = list(obj.image.shape)
    except Exception as e:
        info['image_error'] = f'{type(e).__name__}: {e}'
    return info


def op_appear_on(args):
    """直接调用上游 Button.appear_on（= color_similar(get_color(...), color)）。"""
    image = _require_image()
    button = _resolve(args['asset'])
    threshold = args.get('threshold', 10)
    t0 = time.perf_counter()
    appear = bool(button.appear_on(image, threshold=threshold))
    ms = (time.perf_counter() - t0) * 1000
    from module.base.utils import get_color, color_similarity
    got = [float(v) for v in get_color(image, button.area)]
    expected = _color_of(button)
    return {
        'appear': appear,
        'threshold': threshold,
        'color': got,
        'expected': expected,
        'tolerance': float(color_similarity(got, expected)) if expected else None,
        'elapsed_ms': round(ms, 4),
    }


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
    name = args.get('name')
    sim, button = template.match_result(image, name=name)
    return {'similarity': float(sim), 'button_area': list(button.area)}


def op_ocr(args):
    """走上游的 OCR 门面（ONNX 后端），识图同样不重写。"""
    image = _require_image()
    from module.ocr.ocr import Ocr
    from module.base.button import Button
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


def serve(readline, writeline, flush=None):
    def send(obj):
        writeline(json.dumps(obj, ensure_ascii=False) + '\n')
        if flush:
            flush()

    while True:
        line = readline()
        if not line:
            break
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError as e:
            send({'id': None, 'ok': False, 'error': f'JSON 解析失败: {e}'})
            continue
        if req.get('op') == 'shutdown':
            send({'id': req.get('id'), 'ok': True, 'result': {'bye': True}})
            break
        send(handle(req))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--stdio', action='store_true')
    ap.add_argument('--tcp', type=int, default=None)
    ap.add_argument('--log', default=None, help='把启动信息写到文件（stdio 模式下不能污染 stdout）')
    args = ap.parse_args()

    def log(msg):
        if args.log:
            with open(args.log, 'a', encoding='utf-8') as f:
                f.write(msg + '\n')

    log(f'worker start pid={os.getpid()} fork={FORK}')

    if args.tcp:
        import socket
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(('127.0.0.1', args.tcp))
        srv.listen(1)
        log(f'listening on 127.0.0.1:{args.tcp}')
        conn, _addr = srv.accept()
        f = conn.makefile('rw', encoding='utf-8', newline='\n')
        serve(f.readline, f.write, f.flush)
        conn.close()
        srv.close()
    else:
        # _PROTO 已在模块顶部（ALAS import 之前）准备好
        serve(sys.stdin.readline, _PROTO.write, _PROTO.flush)
    log('worker exit')
    return 0


if __name__ == '__main__':
    sys.exit(main())
