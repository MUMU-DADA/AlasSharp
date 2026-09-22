# -*- coding: utf-8 -*-
"""一键同步：把"更新的上游规则/素材"拉齐到本仓库，并复检。

上游更新分三类，本脚本只负责其中**需要动作**的两类：

  A. **运行时自动生效，无需同步**（所以不在这里）
     上游 Python 代码：页面规则（module/ui/page.py）、UI 素材（module/*/assets.py）、
     视觉模块（module/map_detection/*）、OCR —— 宿主 `tools/alas_vision.py` 直接
     import fork 目录并 chdir 过去，上游一改、下次调用就是新规则；
     页面图/控件清单也是运行时向宿主要（`ui_page_graph`），不读导出文件。

  B. **需要重导**：关卡 IR（`data/campaign/**`）+ assets/schema/manifest
     → `tools/export_upstream_data.py`（有 `--check`，无差异返回 0、有差异返回 1）

  C. **需要刷新快照**：`vendor/upstream/`（素材逐字节镜像 + 漂移守卫）
     → `tools/sync_upstream_assets.py`（`--check` / `--strict-drift`）

用法：
  python tools/sync_all.py                    # 检查（默认）：B/C 是否过期，非 0 退出
  python tools/sync_all.py --strict-drift     # 检查时把"上游已走在我们前面"也算失败（CI 守卫）
  python tools/sync_all.py --update           # 更新：重导 IR + 刷新素材快照 + 复检
  python tools/sync_all.py --update --verify  # 更新后再跑免设备的验收
  python tools/sync_all.py --fetch            # 先在 fork 里 `git fetch upstream --no-tags`（只读）

注意：**更新 ≠ 适配完成**。上游代码变更可能在 C# 侧静默失效（例如改了 asset id 而 C# 里
硬编码了旧的 —— 本项目早期就踩过）。所以 `--verify` 那一步不能省：真正的"一键适配"
= 更新 + 跑一致性验收。
"""
import argparse
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..'))
FORK = os.path.normpath(os.path.join(ROOT, '.runtime', 'engine'))
EXPORT = os.path.join(HERE, 'export_upstream_data.py')
ASSETS = os.path.join(HERE, 'sync_upstream_assets.py')
DIAG = os.path.join(HERE, 'diagnostics')

# 免设备的验收（不需要真机）：识图协议 + 产品路径 + 偏移对齐 + 文档汇总
VERIFY_STEPS = [
    (os.path.join(DIAG, 'verify_map_detection.py'), []),
    (os.path.join(DIAG, 'verify_product_map.py'), []),
    (os.path.join(DIAG, 'verify_map_alignment.py'), []),
    (os.path.join(DIAG, 'verify_all.py'), ['--docs-only']),
]

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def run(cmd, title, cwd=None, quiet=False):
    print('── %s' % title)
    print('   $ %s' % ' '.join(os.path.basename(c) if i else c for i, c in enumerate(cmd)))
    proc = subprocess.run(cmd, cwd=cwd or ROOT, capture_output=True)
    out = (proc.stdout or b'').decode('utf-8', 'replace').strip()
    err = (proc.stderr or b'').decode('utf-8', 'replace').strip()
    if out and not quiet:
        for line in out.splitlines()[-12:]:
            print('   | ' + line)
    if err:
        for line in err.splitlines()[-6:]:
            print('   ! ' + line)
    print('   → exit=%d' % proc.returncode)
    return proc.returncode


def check(strict_drift):
    """检查 B/C 是否过期；返回 (ok, 明细)。"""
    details = {}
    details['ir'] = run([sys.executable, EXPORT, '--check'],
                        'B. 关卡 IR / assets / schema 是否与上游一致')
    cmd = [sys.executable, ASSETS, '--check']
    if strict_drift:
        cmd.append('--strict-drift')
    details['assets'] = run(cmd, 'C. vendor 素材快照是否与上游一致%s'
                            % ('（strict：把上游领先也算失败）' if strict_drift else ''))
    return details


def main():
    ap = argparse.ArgumentParser(description='一键同步上游规则/素材')
    ap.add_argument('--update', action='store_true', help='执行更新（默认只检查）')
    ap.add_argument('--strict-drift', action='store_true',
                    help='检查时把"上游已走在我们前面"也算失败')
    ap.add_argument('--verify', action='store_true', help='更新后跑免设备的验收')
    ap.add_argument('--fetch', action='store_true',
                    help='先在 fork 里 git fetch upstream --no-tags（只读，不 push）')
    args = ap.parse_args()

    if args.fetch:
        if not os.path.isdir(os.path.join(FORK, '.git')):
            print('找不到 fork：%s' % FORK)
            return 2
        run(['git', 'fetch', 'upstream', '--no-tags'], 'A0. fork 侧 fetch upstream（只读）',
            cwd=FORK)

    if not args.update:
        details = check(args.strict_drift)
        bad = [k for k, v in details.items() if v != 0]
        print()
        print('检查结果：%s' % ('全部一致 ✅' if not bad else '需要更新 ❌ %s' % bad))
        if bad:
            print('执行 `python tools/sync_all.py --update` 即可拉齐。')
        return 1 if bad else 0

    # ---- 更新
    rc = {}
    rc['ir'] = run([sys.executable, EXPORT, '--out', os.path.join(ROOT, 'data')],
                   'B. 重导关卡 IR / assets / schema')
    rc['assets'] = run([sys.executable, ASSETS], 'C. 刷新 vendor 素材快照')
    if any(v != 0 for v in rc.values()):
        print('\n更新阶段有失败项：%s' % {k: v for k, v in rc.items() if v})
        return 1

    print('\n复检（更新后应全部一致）')
    details = check(args.strict_drift)
    bad = [k for k, v in details.items() if v != 0]
    if bad:
        print('复检仍有不一致：%s' % bad)
        return 1
    print('更新完成 ✅')

    if args.verify:
        print()
        failed = []
        for script, extra in VERIFY_STEPS:
            if not os.path.exists(script):
                continue
            code = run([sys.executable, script] + extra,
                       'V. %s' % os.path.basename(script), quiet=True)
            if code != 0:
                failed.append(os.path.basename(script))
        if failed:
            print('\n验收失败：%s' % failed)
            print('提示：上游代码变更可能在 C# 侧静默失效（asset id/页面规则改名等），'
                  '这些脚本正是用来抓这种事的。')
            return 1
        print('验收全部通过 ✅')
    else:
        print('提示：加 `--verify` 可顺带跑免设备的验收（推荐，更新后必跑）。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
