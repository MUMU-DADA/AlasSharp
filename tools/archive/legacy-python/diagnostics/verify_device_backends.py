# -*- coding: utf-8 -*-
"""截图后端对照 + **成功判据**（需设备；会把配置切来切去，结束时还原）。

**为什么需要这个脚本**（2026-09-23 的实测教训）：
我量到 `nemu_ipc` "稳态 7 ms/帧、比 adb 快 45 倍"，差点据此改默认 —— 复验发现那是
**抓帧失败的耗时**（`NemuIpcError: Connection failed` → `RequestHumanTakeover`，一帧都没有）。
`device_configure` 会**照单接受**这种后端（`ok=True`），错误要到真抓帧才暴露。

所以本脚本把当时的验证协议固化下来，**耗时只在帧有效时才计入**：

    ① op 返回里没有 `error`
    ② 返回键含 `capture_ms` 与 `shape`
    ③ `_state['image']` 真被写入（有帧）

三条任一不满足 → 该后端记 **不可用**，且**不参与耗时比较**。

**它守的线**：以后任何"换个后端试试"的念头，都会被同一套判据约束；
默认后端必须**通过三条判据**，否则脚本非零退出。

用法：
    python tools/diagnostics/verify_device_backends.py                # 探默认 + adb + droidcast + nemu_ipc
    python tools/diagnostics/verify_device_backends.py adb droidcast  # 只探这几个
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
TOOLS = ROOT / 'tools'
CONFIG = ENGINE / 'config' / 'alas.json'
SERIAL = os.environ.get('ALAS_SERIAL', '127.0.0.1:16384')
DEFAULT_CANDIDATES = ['adb', 'droidcast', 'nemu_ipc']

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def load_host():
    sys.path.insert(0, str(TOOLS))
    os.chdir(ENGINE)
    import alas_vision as av                                    # noqa: E402
    return av


def current_method(av) -> str:
    from module.config.config import AzurLaneConfig            # noqa: E402
    return str(getattr(AzurLaneConfig('alas'), 'Emulator_ScreenshotMethod', '') or '')


def probe(av, method: str, frames: int = 3) -> dict:
    """按三条判据探一个后端；返回 {'usable', 'times', 'invalid_reason'}。"""
    configured = av.handle_line(json.dumps({
        'id': 1, 'op': 'device_configure',
        'args': {'serial': SERIAL, 'screenshot': method, 'control': 'adb'}}))
    if not json.loads(configured).get('ok'):
        return {'usable': False, 'reason': 'device_configure 失败', 'times': []}

    times, reason = [], ''
    for _ in range(frames):
        response = json.loads(av.handle_line(json.dumps({
            'id': 1, 'op': 'device_capture_set', 'args': {'raw': True}})))
        result = response.get('result') or {}
        error = response.get('error') or result.get('error')
        if error:
            reason = f'抓帧报错：{str(error)[:80]}'
            break
        if 'capture_ms' not in result or 'shape' not in result:
            reason = f"返回里没有 capture_ms/shape（键={sorted(result.keys())}）"
            break
        if av._state.get('image') is None:
            reason = '抓帧没有写入 _state[\'image\']（等于没有帧）'
            break
        times.append(float(result['capture_ms']))
    return {'usable': not reason, 'reason': reason, 'times': times}


def main() -> int:
    if not CONFIG.is_file():
        print(f'[跳过] 没有账号配置 {CONFIG}（无设备/无配置时本检查不适用）')
        return 0
    av = load_host()

    candidates = sys.argv[1:] or DEFAULT_CANDIDATES
    original = current_method(av)
    if original and original not in candidates:
        candidates = [original] + candidates

    backup = Path(tempfile.mkdtemp(prefix='alas-backends-')) / 'alas.json.bak'
    shutil.copy2(CONFIG, backup)
    rows, default_ok = [], True
    try:
        for method in candidates:
            outcome = probe(av, method)
            avg = sum(outcome['times']) / len(outcome['times']) if outcome['times'] else None
            rows.append((method, outcome['usable'], avg, outcome['reason']))
            if method == original and not outcome['usable']:
                default_ok = False
            mark = 'ok  ' if outcome['usable'] else 'BAD '
            avg_text = f'{avg:6.0f} ms' if avg is not None else '      —'
            note = '' if outcome['usable'] else f"  ← {outcome['reason']}"
            tag = '（默认）' if method == original else ''
            print(f"  {mark} {method:12s}{tag:8s} {avg_text}{note}")
    finally:
        # **一定要还原**：切换后端会写进 alas.json
        shutil.copy2(backup, CONFIG)
        av.handle_line(json.dumps({
            'id': 1, 'op': 'device_configure',
            'args': {'serial': SERIAL, 'screenshot': original, 'control': 'adb'}}))

    same = CONFIG.read_bytes() == backup.read_bytes()
    print()
    print(f"配置已还原为 {original or '(空)'}；与备份逐字节一致={same}")
    print('判据：① 无 error ② 返回含 capture_ms/shape ③ _state["image"] 有帧 —— '
          '**只有三条都过，耗时才算数**')

    failures = []
    if not same:
        failures.append('配置没有还原（alas.json 与备份不一致）')
    if not default_ok:
        failures.append(f'默认后端 {original} 没通过三条判据 —— 当前画面链路拿不到帧')
    if failures:
        print()
        print(f'结果: FAIL（{len(failures)} 项）')
        for item in failures:
            print(f'  - {item}')
        return 1
    usable = [r for r in rows if r[1]]
    print()
    print(f'结果: OK（默认 {original} 有效；{len(usable)}/{len(rows)} 个后端通过三条判据）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
