#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
识图引擎 worker（进程外宿主）。

真正的识图逻辑在 `alas_vision.py` 里，本文件只负责两件事：
  - 把**协议流**与**普通输出**分开（否则上游 logger 的横幅会污染协议）
  - 按 stdio 或 TCP 收发一行一条的 JSON

进程内宿主（C# 通过 CPython C API 调用）走的是同一份 `alas_vision.handle_line()`，
因此两种宿主语义一致，换宿主不影响上层。

用法：
    python vision_worker.py --stdio          # 供 C# 以子进程方式驱动
    python vision_worker.py --tcp 34567      # 供其它进程连接
"""
from __future__ import annotations

import argparse
import json
import os
import sys

# ⚠️ 必须在 import alas_vision（它会 import ALAS 模块）**之前**做协议流分离。
#    上游 `module.logger` 在 import 时就会往 stdout 打横幅（"------ START ------"），
#    若协议也写在 stdout 上，C# 侧读到的第一行是横幅而不是 JSON（实测踩过两次：
#    把 dup 放晚了，横幅早就打出去了）。
_PROTO = None
if '--tcp' not in sys.argv:
    _PROTO = os.fdopen(os.dup(1), 'w', encoding='utf-8', newline='\n')
    os.dup2(2, 1)                       # 此后 print / logger 一律进 stderr
    sys.stdout = sys.stderr

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import alas_vision  # noqa: E402


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
        response = alas_vision.handle_line(line)
        send(json.loads(response))
        if '"bye": true' in response:
            break


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--stdio', action='store_true')
    ap.add_argument('--tcp', type=int, default=None)
    ap.add_argument('--log', default=None)
    args = ap.parse_args()

    def log(msg):
        if args.log:
            with open(args.log, 'a', encoding='utf-8') as f:
                f.write(msg + '\n')

    log(f'worker start pid={os.getpid()} fork={alas_vision.FORK}')

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
        serve(sys.stdin.readline, _PROTO.write, _PROTO.flush)
    log('worker exit')
    return 0


if __name__ == '__main__':
    sys.exit(main())
