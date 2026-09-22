# -*- coding: utf-8 -*-
"""adb 访问的共用工具：把"连接会掉"这件事集中处理掉。

本机实测的连接生命周期（踩了很多次才看清）：
- 每个**新的 adb 客户端进程**都可能拉起自己的 server（MuMu 自带的 adb 与 venv 里的
  adbutils adb 会互相抢占 5037），server 一换，之前的 TCP 连接就没了；
- 表现是：`connect` 说"connected"，紧接着 `screencap` 返回 **0 字节**，
  交给解码器就报 UnidentifiedImageError —— 看着像图像问题，其实是连接问题；
- 可靠做法是**在同一个进程里** `connect → devices 校验 → 操作`，并带重试。

所以诊断脚本不要自己拼 subprocess，一律走这里。
"""
import os
import subprocess
import time

ADB = os.environ.get('STUB_ADB')
SERIAL = os.environ.get('SERIAL', '127.0.0.1:16384')


def _run(args, timeout=120, binary=False):
    return subprocess.run([ADB] + list(args), capture_output=True, timeout=timeout,
                          text=not binary,
                          encoding=None if binary else 'utf-8', errors=None if binary else 'replace')


def devices():
    r = _run(['devices'])
    return r.stdout or ''


def ensure(serial=None, attempts=4, verbose=True):
    """确保 serial 处于 device 状态；返回是否就绪。"""
    serial = serial or SERIAL
    for i in range(attempts):
        c = _run(['connect', serial])
        time.sleep(1.5)
        d = _run(['devices'])
        if any(line.split('\t')[0].strip() == serial and 'device' in line
               for line in (d.stdout or '').splitlines()):
            return True
        if verbose:
            print('[adb  ] 第 %d 次未就绪: %s' % (i + 1, (c.stdout or '').strip()[:80]))
        time.sleep(2)
    return False


def shell(*args, serial=None, attempts=3):
    """执行 adb shell 命令，返回 CompletedProcess（失败重试）。"""
    serial = serial or SERIAL
    last = None
    for _ in range(attempts):
        last = _run(['-s', serial, 'shell'] + [str(a) for a in args])
        if last.returncode == 0:
            return last
        ensure(serial, verbose=False)
    return last


def screencap(path, serial=None, attempts=4, min_bytes=1024):
    """截图到 path。**按字节数判定成功**（0 字节 = 连接问题，不是图像问题），失败重连重试。"""
    serial = serial or SERIAL
    for i in range(attempts):
        with open(path, 'wb') as f:
            subprocess.run([ADB, '-s', serial, 'exec-out', 'screencap', '-p'],
                           stdout=f, stderr=subprocess.DEVNULL, timeout=180)
        size = os.path.getsize(path)
        if size >= min_bytes:
            return size
        ensure(serial, verbose=False)
        time.sleep(1.5)
    raise RuntimeError('screencap 连续 %d 次失败（最后 %d 字节）：'
                       '多半是 adb 连接被新 server 抢掉了' % (attempts, os.path.getsize(path)))
