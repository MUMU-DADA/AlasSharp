# -*- coding: utf-8 -*-
"""一键同步：把上游规则/素材的离线来源拉齐到本仓库，并复检。

上游更新分三类，本脚本只负责其中**需要动作**的两类：

  A. **Engine 运行时不直接加载上游业务 Python**。
     上游 Python 只作为构建期来源、离线 oracle 和漂移检查输入；产品运行时由
     `Alas.Engine` 的 C# 规则与状态机执行，Python 发布物只提供纯 CV/OCR worker。

  B. **需要重导**：静态素材/Config/MAP 展示合同（`data/**`）
     → `tools/export_upstream_data.py`（只作离线声明审计）

  C. **需要编译**：Campaign 来源合同（可审阅 C# 来源清单）
     → `tools/migration/compile_campaign_rules.py`

  D. **需要刷新快照**：`vendor/upstream/`（素材逐字节镜像 + 漂移守卫）
     → `tools/sync_upstream_assets.py`（`--check` / `--strict-drift`）

用法：
  python tools/sync_all.py                    # 检查（默认）：B/C 是否过期，非 0 退出
  python tools/sync_all.py --strict-drift     # 检查时把"上游已走在我们前面"也算失败（CI 守卫）
  python tools/sync_all.py --update           # 更新：重导声明 + 刷新素材快照 + 复检
  python tools/sync_all.py --verify           # 检查后运行免设备验收
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
RULES = os.path.join(HERE, 'migration', 'compile_campaign_rules.py')
RULES_OUTPUT = os.path.join(ROOT, 'src', 'Alas.Engine', 'Rules', 'Generated', 'CampaignRuleSources.g.cs')
MAPS = os.path.join(HERE, 'migration', 'compile_campaign_maps.py')
MAPS_OUTPUT = os.path.join(ROOT, 'src', 'Alas.Engine', 'Rules', 'Generated', 'CampaignMaps.g.cs')
DIAG = os.path.join(HERE, 'diagnostics')

# 免设备的验收（不需要真机）：离线导出契约、Engine 架构和结果合同
VERIFY_STEPS = [
    (os.path.join(DIAG, 'verify_architecture.py'), []),
    (os.path.join(DIAG, 'verify_map_export.py'), []),
    (os.path.join(DIAG, 'verify_campaign_export.py'), []),
    (os.path.join(DIAG, 'verify_config_export.py'), []),
    (os.path.join(DIAG, 'verify_pages_export.py'), []),
    (os.path.join(DIAG, 'verify_result_contract.py'), []),
    (os.path.join(DIAG, 'verify_export_integrity.py'), []),
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
    if out and (not quiet or proc.returncode != 0):
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
                        'B. 关卡声明 / assets / schema 是否与上游一致')
    cmd = [sys.executable, ASSETS, '--check']
    if strict_drift:
        cmd.append('--strict-drift')
    details['assets'] = run(cmd, 'C. vendor 素材快照是否与上游一致%s'
                            % ('（strict：把上游领先也算失败）' if strict_drift else ''))
    details['rules'] = run([sys.executable, RULES, '--upstream', FORK,
                            '--output', RULES_OUTPUT, '--check'],
                           'D. C# Campaign 来源合同是否与上游一致')
    details['maps'] = run([sys.executable, MAPS, '--upstream', FORK,
                           '--output', MAPS_OUTPUT, '--check'],
                          'E. C# 地图规则声明是否与上游一致')
    return details


def verify():
    """Required checks fail closed, including a missing verification script."""
    failed = []
    for script, extra in VERIFY_STEPS:
        if not os.path.isfile(script):
            print('缺少验收脚本：%s' % os.path.basename(script))
            failed.append(os.path.basename(script))
            continue
        if run([sys.executable, script] + extra,
               'V. %s' % os.path.basename(script), quiet=True) != 0:
            failed.append(os.path.basename(script))
    if failed:
        print('\n验收失败：%s' % failed)
        return 1
    print('验收全部通过 ✅')
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description='一键同步上游规则/素材')
    ap.add_argument('--update', action='store_true', help='执行更新（默认只检查）')
    ap.add_argument('--strict-drift', action='store_true',
                    help='检查时把"上游已走在我们前面"也算失败')
    ap.add_argument('--verify', action='store_true', help='检查或更新后跑免设备的验收')
    ap.add_argument('--fetch', action='store_true',
                    help='先在 fork 里 git fetch upstream --no-tags（只读，不 push）')
    args = ap.parse_args(argv)

    if args.fetch:
        if not os.path.isdir(os.path.join(FORK, '.git')):
            print('找不到 fork：%s' % FORK)
            return 2
        if run(['git', 'fetch', 'upstream', '--no-tags'], 'A0. fork 侧 fetch upstream（只读）',
               cwd=FORK) != 0:
            return 1

    if not args.update:
        details = check(args.strict_drift)
        bad = [k for k, v in details.items() if v != 0]
        print()
        print('检查结果：%s' % ('全部一致 ✅' if not bad else '需要更新 ❌ %s' % bad))
        if bad:
            print('执行 `python tools/sync_all.py --update` 即可拉齐。')
        verification = verify() if args.verify else 0
        return 1 if bad or verification else 0

    # ---- 更新
    rc = {}
    rc['ir'] = run([sys.executable, EXPORT, '--out', os.path.join(ROOT, 'data')],
                   'B. 重导关卡声明 / assets / schema')
    rc['assets'] = run([sys.executable, ASSETS], 'C. 刷新 vendor 素材快照')
    rc['rules'] = run([sys.executable, RULES, '--upstream', FORK,
                       '--output', RULES_OUTPUT], 'D. 编译 C# Campaign 来源合同')
    rc['maps'] = run([sys.executable, MAPS, '--upstream', FORK,
                      '--output', MAPS_OUTPUT], 'E. 编译 C# 地图规则声明')
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
        return verify()
    else:
        print('提示：加 `--verify` 可顺带跑免设备的验收（推荐，更新后必跑）。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
