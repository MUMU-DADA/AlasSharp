#!/usr/bin/env python3
"""Cache audited AzurPilot service dependencies without replacing engine code.

Only the listed files are admitted. A differing existing file is a conflict,
not an invitation to overwrite the runtime. No configs, logs or device data
are copied; the receipt contains relative names, source commit and hashes.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
FILES = (
    'module/api/__init__.py',
    'module/api/protocol.py',
    'module/api/config_service.py',
    'module/api/statistics_service.py',
    'module/api/meowfficer_service.py',
    'module/config/transaction.py',
    'module/config/time_source.py',
    'module/base/device_id.py',
    'module/base/async_executor.py',
    'module/os/ship_exp_data.py',
    'module/statistics/resource_stats.py',
    'module/statistics/cl1_database.py',
    'module/statistics/opsi_month.py',
    'module/statistics/ship_exp_stats.py',
    'module/statistics/commission_income_stats.py',
    'module/statistics/daily_summary_store.py',
    'module/shop_strategy/__init__.py',
    'module/shop_strategy/adapter.py',
    'module/shop_strategy/compiler.py',
    'module/shop_strategy/errors.py',
    'module/shop_strategy/evaluator.py',
    'module/shop_strategy/models.py',
    'module/shop_strategy/runtime.py',
)
# These existing modules are used unchanged; their hashes are recorded too.
ENGINE_DEPENDENCIES = ('module/logger.py', 'deploy/atomic.py')
RECEIPT = 'upstream-services.manifest.json'


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def safe_path(root: Path, relative: str) -> Path:
    path = root / relative
    for part in (path, *path.parents):
        if part == root:
            break
        if part.is_symlink() or part.is_junction():
            raise ValueError(f'linked service path: {relative}')
    if not path.resolve().is_relative_to(root):
        raise ValueError(f'escaped service path: {relative}')
    return path


def sync(source: Path, destination: Path, *, check: bool = False) -> dict:
    source, destination = source.resolve(), destination.resolve()
    if source == destination:
        raise ValueError('source and destination must differ')
    payloads = {}
    missing = []
    # Complete preflight before any write: one conflict leaves all files alone.
    for relative in FILES:
        payload = safe_path(source, relative).read_bytes()
        target = safe_path(destination, relative)
        if target.exists():
            if target.read_bytes() != payload:
                raise ValueError(f'existing engine file differs: {relative}')
        else:
            missing.append(relative)
        payloads[relative] = payload
    dependencies = [
        {'path': relative, 'sha256': digest(safe_path(destination, relative).read_bytes())}
        for relative in ENGINE_DEPENDENCIES
    ]
    revision = subprocess.run(['git', '-C', str(source), 'rev-parse', 'HEAD'],
                              capture_output=True, text=True, check=True).stdout.strip()
    clean = subprocess.run(['git', '-C', str(source), 'status', '--porcelain', '--', *FILES],
                           capture_output=True, text=True, check=True).stdout == ''
    receipt = {
        'contract': 'upstream-services/1', 'source': 'AzurPilot',
        'commit': revision, 'selected_files_clean': clean,
        'files': [{'path': p, 'size': len(data), 'sha256': digest(data)}
                  for p, data in payloads.items()],
        'engine_dependencies': dependencies,
        'excluded': ['module/statistics/azurstats.py (existing implementation requires separate migration)'],
    }
    receipt_path = safe_path(destination, RECEIPT)
    if check:
        if missing:
            raise ValueError('missing services: ' + ', '.join(missing))
        if not receipt_path.exists() or json.loads(receipt_path.read_text(encoding='utf-8')) != receipt:
            raise ValueError('service receipt is missing or differs')
        return receipt
    for relative in missing:
        target = safe_path(destination, relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        # Exclusive create also refuses concurrent runtime changes.
        with target.open('xb') as stream:
            stream.write(payloads[relative])
    receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return receipt


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--destination', type=Path, default=ROOT / '.runtime/engine')
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    try:
        receipt = sync(args.source, args.destination, check=args.check)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        # Relative conflict names are useful; local absolute paths stay local.
        print(f'FAIL: {error}')
        return 1
    print(f"PASS: {len(receipt['files'])} service files; existing engine code preserved")
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
