#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
adb 桩：在**没有真机**的条件下把设备层的真实调用链跑通。

它不是 mock 接口，而是一个**可执行文件**——C# 侧照旧走
「启动进程 → 拼参数 → 读二进制 stdout」，因此进程调用、二进制捕获、
参数拼接这些真正容易出错的地方都被完整验到。

支持的子命令（够设备层最小闭环用）：
    -s <serial> devices                 → 列出设备
    -s <serial> get-state               → device
    -s <serial> shell wm size           → Physical size: 1280x720
    -s <serial> exec-out screencap -p   → 输出指定 PNG 的**原始字节**
    -s <serial> shell input tap X Y     → 追加到日志文件（供断言）
    -s <serial> shell input swipe ...   → 同上

环境变量：
    STUB_ADB_SCREENSHOT  截图用的 PNG 路径（默认取 ALAS 素材里的一张真实整屏图）
    STUB_ADB_LOG         命令日志文件（默认 %TEMP%/stub_adb.log）
"""
from __future__ import annotations

import os
import sys

SCREENSHOT = os.environ.get('STUB_ADB_SCREENSHOT') or ''
LOG = os.environ.get('STUB_ADB_LOG') or os.path.join(
    os.environ.get('TEMP', '.'), 'stub_adb.log')


def log(line: str) -> None:
    with open(LOG, 'a', encoding='utf-8') as f:
        f.write(line + '\n')


def main(argv):
    args = [a for a in argv[1:]]
    serial = None
    if len(args) >= 2 and args[0] == '-s':
        serial = args[1]
        args = args[2:]

    log(f"serial={serial} args={' '.join(args)}")

    if not args:
        return 1
    head = args[0]

    if head == 'devices':
        sys.stdout.write('List of devices attached\n'
                         f'{serial or "emulator-5554"}\tdevice\n\n')
        return 0

    if head == 'get-state':
        sys.stdout.write('device\n')
        return 0

    if head == 'shell':
        sub = args[1:]
        if sub[:2] == ['wm', 'size']:
            sys.stdout.write('Physical size: 1280x720\n')
            return 0
        if sub[:1] == ['input']:
            log(f"input: {' '.join(sub)}")     # tap / swipe 已在上面的 log 里，这里再明确一次
            return 0
        sys.stdout.write('\n')
        return 0

    if head == 'exec-out':
        sub = args[1:]
        if sub[:1] == ['screencap']:
            if not SCREENSHOT or not os.path.isfile(SCREENSHOT):
                sys.stderr.write(f'STUB_ADB_SCREENSHOT 未设置或不存在: {SCREENSHOT}\n')
                return 2
            with open(SCREENSHOT, 'rb') as f:
                sys.stdout.buffer.write(f.read())
            sys.stdout.buffer.flush()
            return 0
        return 1

    sys.stderr.write(f'stub adb: 不支持 {head}\n')
    return 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
