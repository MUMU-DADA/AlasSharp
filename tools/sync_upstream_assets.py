# -*- coding: utf-8 -*-
"""把上游的**静态资源**快照进本仓库（vendor/upstream）。

为什么要入库：
- 上游 assets/ 是识别规则的真值来源（模板图、区域、颜色都在这些 PNG 里）。
  只依赖旁边那份 Python fork，仓库就不是自洽的：换台机器、上游一改图，
  历史构建就复现不出来。
- 上游 bin/ 里的模型与设备端二进制同理（OCR 权重、ascreencap/MaaTouch）。

为什么写成"同步 + 校验"而不是一次性拷贝：
- 静态资源会随上游更新。MANIFEST.json 记录**来源 commit + 每个文件的 sha256**，
  既能查"我们快照的是哪一版"，也能查"vendor 里的东西被谁动过"。
- 幂等：重复跑不产生 diff；`--check` 可以在 CI 里当守卫用。

只做文件操作，不 import 上游任何 Python 模块 —— 所以任何 Python 3.8+ 都能跑，
不需要装依赖。
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CSHARP_ROOT = os.path.dirname(HERE)

# 入库范围：显式列举，避免哪天上游多出个几十 MB 的目录被无声拖进来
INCLUDE_ROOTS = [
    'assets',              # 四服务器模板图 + 共用目录（shop/island/map_detection/...）
    'bin/ascreencap',      # 设备端快速截屏二进制（12 个 ABI/版本）
    'bin/cnocr_models',    # 上游 mxnet OCR 权重（onnx 的上游来源）
    'bin/ocr_models',      # 转换出的 onnx 权重 + 字符表
    'bin/MaaTouch',        # 触控后端
    'bin/DroidCast',
    'bin/hermit',
    'bin/scrcpy',
]
EXCLUDE_DIRS = {'__pycache__', '.git', '.idea', '.vscode'}
EXCLUDE_FILES = {'.DS_Store', 'Thumbs.db'}


def sha256_file(path, chunk=1 << 20):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        while True:
            block = f.read(chunk)
            if not block:
                break
            h.update(block)
    return h.hexdigest()


def iter_files(root):
    """产出 (相对路径, 绝对路径)，按 POSIX 风格相对路径排序，保证确定性。"""
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d not in EXCLUDE_DIRS)
        for name in sorted(filenames):
            if name in EXCLUDE_FILES:
                continue
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, root).replace(os.sep, '/')
            yield rel, full


def collect(source):
    """上游侧的 {相对路径: (大小, sha256)}。路径保留上游原样（assets/... , bin/...）。"""
    files = {}
    for include in INCLUDE_ROOTS:
        base = os.path.join(source, include.replace('/', os.sep))
        if not os.path.isdir(base):
            print('  [跳过] 上游没有 %s' % include)
            continue
        for rel, full in iter_files(base):
            key = '%s/%s' % (include, rel)
            files[key] = (os.path.getsize(full), sha256_file(full))
    return files


def git_info(source):
    def run(*args):
        try:
            out = subprocess.run(['git', '-C', source] + list(args),
                                 capture_output=True, text=True, timeout=30)
            return out.stdout.strip() if out.returncode == 0 else None
        except Exception:
            return None

    dirty = run('status', '--porcelain')
    return {
        # 个人 fork 的远端可能暴露账号或凭据；可复现性由 commit 与逐文件哈希保证。
        'repository': 'AzurLaneAutoScript (source identity redacted)',
        'commit': run('rev-parse', 'HEAD'),
        'branch': run('rev-parse', '--abbrev-ref', 'HEAD'),
        # 上游工作区若被改过，快照就不等于任何 commit —— 必须记下来
        'worktree_clean': dirty == '',
    }


def load_manifest(dest):
    path = os.path.join(dest, 'MANIFEST.json')
    if not os.path.exists(path):
        return None
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def write_manifest(dest, source, files, info):
    manifest = {
        'note': '上游静态资源快照清单。由 tools/sync_upstream_assets.py 生成，不要手改。',
        'source': dict(info, repository='AzurLaneAutoScript (source identity redacted)',
                       path='<upstream-root>'),
        'roots': INCLUDE_ROOTS,
        'file_count': len(files),
        'total_bytes': sum(size for size, _ in files.values()),
        'files': [{'path': p, 'size': files[p][0], 'sha256': files[p][1]}
                  for p in sorted(files)],
    }
    path = os.path.join(dest, 'MANIFEST.json')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write('\n')
    return manifest


def sync(source, dest, prune=True):
    print('来源: %s' % os.path.abspath(source))
    print('目标: %s' % os.path.abspath(dest))
    files = collect(source)
    print('上游侧共 %d 个文件，%.1f MB'
          % (len(files), sum(s for s, _ in files.values()) / 1e6))

    copied = updated = 0
    for rel in sorted(files):
        size, digest = files[rel]
        target = os.path.join(dest, rel.replace('/', os.sep))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        if not os.path.exists(target):
            shutil.copyfile(os.path.join(source, rel.replace('/', os.sep)), target)
            copied += 1
        elif os.path.getsize(target) != size or sha256_file(target) != digest:
            shutil.copyfile(os.path.join(source, rel.replace('/', os.sep)), target)
            updated += 1

    removed = []
    if prune and os.path.isdir(dest):
        for rel, full in iter_files(dest):
            if rel == 'MANIFEST.json' or rel == 'README.md':
                continue
            if rel not in files:
                os.remove(full)
                removed.append(rel)

    info = git_info(source)
    manifest = write_manifest(dest, source, files, info)
    print('新增 %d，更新 %d，因上游删除而清理 %d' % (copied, updated, len(removed)))
    if removed:
        print('  清理: %s' % ', '.join(removed[:10]))
    print('清单已写入 MANIFEST.json（来源 commit %s，工作区干净=%s）'
          % ((info['commit'] or '未知')[:12], info['worktree_clean']))
    print('合计 %d 个文件 / %.1f MB'
          % (manifest['file_count'], manifest['total_bytes'] / 1e6))
    return 0


def check(source, dest, strict_drift=False):
    """校验：① vendor 与清单一致 ② 上游是否已走在我们快照之前。"""
    manifest = load_manifest(dest)
    if manifest is None:
        print('MANIFEST.json 不存在，先跑一次同步')
        return 1
    recorded = {f['path']: (f['size'], f['sha256']) for f in manifest['files']}

    problems = []
    for rel, (size, digest) in sorted(recorded.items()):
        target = os.path.join(dest, rel.replace('/', os.sep))
        if not os.path.exists(target):
            problems.append('缺失 %s' % rel)
        elif os.path.getsize(target) != size or sha256_file(target) != digest:
            problems.append('被改动 %s' % rel)

    present = {p for p, _ in iter_files(dest)} - {'MANIFEST.json', 'README.md'}
    for rel in sorted(present - set(recorded)):
        problems.append('清单外多出 %s' % rel)

    current = collect(source)
    drifted = []
    for rel in sorted(set(current) | set(recorded)):
        if rel not in current:
            drifted.append('上游已删除 %s' % rel)
        elif rel not in recorded:
            drifted.append('上游新增 %s' % rel)
        elif current[rel][1] != recorded[rel][1]:
            drifted.append('上游已修改 %s' % rel)

    print('vendor: %d 个文件 / %.1f MB（清单记录 %d 个）'
          % (len(present), sum(os.path.getsize(os.path.join(dest, p.replace('/', os.sep)))
                               for p in present) / 1e6, len(recorded)))
    print('清单来源 commit: %s' % (manifest['source'].get('commit') or '未知'))
    if problems:
        print('vendor 与清单不一致 %d 处:' % len(problems))
        for p in problems[:20]:
            print('  - %s' % p)
    else:
        print('vendor 与清单一致')
    if drifted:
        print('上游相对快照有 %d 处变化（需要重新同步）:' % len(drifted))
        for d in drifted[:20]:
            print('  - %s' % d)
    else:
        print('上游与快照一致')
    if problems or (strict_drift and drifted):
        return 1
    return 0


def main():
    ap = argparse.ArgumentParser(description='上游静态资源快照同步/校验')
    ap.add_argument('--source', default=os.path.normpath(os.path.join(
        CSHARP_ROOT, '.runtime', 'engine')))
    ap.add_argument('--dest', default=os.path.join(CSHARP_ROOT, 'vendor', 'upstream'))
    ap.add_argument('--check', action='store_true', help='只校验，不写文件')
    ap.add_argument('--no-prune', action='store_true', help='不清理上游已删除的文件')
    ap.add_argument('--strict-drift', action='store_true',
                    help='校验时把"上游领先于快照"也当作失败')
    args = ap.parse_args()
    if not os.path.isdir(args.source):
        print('上游目录不存在: %s（用 --source 指定）' % args.source)
        return 2
    if args.check:
        return check(args.source, args.dest, args.strict_drift)
    return sync(args.source, args.dest, prune=not args.no_prune)


if __name__ == '__main__':
    sys.exit(main())
