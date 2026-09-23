# -*- coding: utf-8 -*-
"""CLI dry-run 回归：主线和活动只读规则，带设备选项也不导航或出击。

verify_s3_plan.py 另以会抛错的设备工厂证明 Python dry-run 不初始化设备。
本脚本验证构建后的 C# CLI；--run 缺少授权必须在启动宿主前拒绝。
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
ALASHUB = os.path.join(ROOT, 'src', 'Alas.DataTool', 'bin', 'Release', 'net10.0', 'alashub.exe')
CHAPTER = 'campaign.campaign_main.campaign_2_1'
ADB = os.path.join(ROOT, 'offline-do-not-connect-adb.exe')
SERIAL = 'offline-do-not-connect'

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def main():
    if not os.path.exists(ALASHUB):
        print('**失败**：未找到 %s（先 dotnet build）' % os.path.relpath(ALASHUB, ROOT))
        return 1
    ok = True
    outputs = []
    for chapter in (CHAPTER, 'campaign.event_20200716_en.a1'):
        # Device options and authorization do not turn a dry-run into execution.
        cmd = [ALASHUB, 'campaign', chapter, '--adb', ADB, '--serial', SERIAL,
               '--allow-actions', '--clear-all']
        result = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8',
                                errors='replace', timeout=60)
        out = (result.stdout or '') + (result.stderr or '')
        outputs.append(out)
        checks = [
            ('退出码 0', result.returncode == 0),
            ('dry_run=True', 'dry_run=True' in out),
            ('无导航/设备配置', all(marker not in out for marker in
                                 ('[前置', '[复位', '[兼容', 'Device serial', 'AdbDevice('))),
            ('无运行步骤', 'round=' not in out and 'step=' not in out),
            ('已读取本章计划', chapter in out and 'battle_' in out),
        ]
        for name, good in checks:
            print('%s %-20s %s' % (chapter, name, 'OK' if good else '**失败**'))
            ok = ok and good
    refused = subprocess.run([ALASHUB, 'campaign', CHAPTER, '--serial', SERIAL, '--run'],
                             capture_output=True, text=True, encoding='utf-8', timeout=30)
    denied = refused.returncode == 2 and '[批量' not in refused.stdout
    print('末尾 --run 无授权、宿主启动前拒绝: %s' % ('OK' if denied else '**失败**'))
    ok = ok and denied
    print()
    print('dry-run 纯度回归：%s' % ('通过' if ok else '**失败**'))
    if not ok:
        print('---- 输出（前 40 行）----')
        for line in '\n'.join(outputs).splitlines()[:40]:
            print('  ' + line)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
