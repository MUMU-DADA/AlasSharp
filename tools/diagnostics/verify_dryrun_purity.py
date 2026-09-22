# -*- coding: utf-8 -*-
"""dry-run 纯度回归：**不带 `--run` 时，campaign 命令绝不能碰游戏**。

为什么专门测它：`campaign` 命令在**真跑**时会自动导航（首关前 + 关间复位）。
一旦这个分支判断被改错，dry-run 也会去点游戏 —— 后果是"以为只是看计划，其实动了游戏"，
而**表面输出看不出来**。所以把这条不变式固化成测试：

断言（全部离线判断，只看输出与副作用标记）：
  1. 退出码 0；
  2. 输出里 `dry_run=True`；
  3. **不出现 `[前置]` / `[复位]`**（那两行只应出现在真跑路径）；
  4. **不出现 `round=` / `step=`**（没有任何步骤被真正执行）；
  5. 安全锁仍在（不带 `--allow-actions` 的真跑请求应被拒，这里用 plan_steps 存在性间接确认干跑成立）。

用法：`python tools/diagnostics/verify_dryrun_purity.py`（需要已构建的 alashub）。
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
ALASHUB = os.path.join(ROOT, 'src', 'Alas.DataTool', 'bin', 'Release', 'net8.0', 'alashub.exe')
CHAPTER = 'campaign.campaign_main.campaign_2_1'
ADB = os.environ.get('STUB_ADB', '')
SERIAL = os.environ.get('SERIAL', '127.0.0.1:16384')

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def main():
    if not os.path.exists(ALASHUB):
        print('**跳过**：未找到 %s（先 dotnet build）' % os.path.relpath(ALASHUB, ROOT))
        return 0
    # 故意**同时传** --adb/--serial（即具备自动导航能力），但不传 --run
    cmd = [ALASHUB, 'campaign', CHAPTER, '--adb', ADB, '--serial', SERIAL]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace',
                       timeout=600)
    out = (r.stdout or '') + (r.stderr or '')
    checks = [
        ('退出码 0', r.returncode == 0),
        ('dry_run=True', 'dry_run=True' in out),
        ('无 [前置]/[复位]（未导航）', ('[前置' not in out) and ('[复位' not in out)),
        ('无 round=/step=（未执行任何步骤）', ('round=' not in out) and ('step=' not in out)),
        ('有 plan_steps（干跑确实读到了计划）', 'battle_' in out),
    ]
    ok = True
    for name, good in checks:
        print('%-34s %s' % (name, 'OK' if good else '**失败**'))
        ok = ok and good
    print()
    print('dry-run 纯度回归：%s' % ('通过' if ok else '**失败**'))
    if not ok:
        print('---- 输出（前 40 行）----')
        for line in out.splitlines()[:40]:
            print('  ' + line)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
