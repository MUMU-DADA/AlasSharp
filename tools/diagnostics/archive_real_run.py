#!/usr/bin/env python3
"""Archive a completed real sortie queue without changing its evidence fields."""
from __future__ import annotations

import argparse
import hashlib
import re
from pathlib import Path
from tempfile import TemporaryDirectory

from audit_queue_evidence import encoded, load_file, private_text, sanitize
from audit_real_records import ARCHIVE, ROOT, audit_archive


REDACTIONS = [
    'absolute project paths replaced with <project> placeholders',
    'device serial and configured endpoint replaced with <device>',
    'account config and config_name replaced with <redacted-account-config>',
    'raw console log and screenshots remain in local ignored directory',
]


def archive_run(source: Path, product_commit: str) -> Path:
    if not re.fullmatch(r'[0-9a-fA-F]{7,40}', product_commit):
        raise ValueError('product commit must be a Git revision hash')
    source = source.resolve()
    if not source.is_relative_to(ROOT) or not source.is_dir():
        raise ValueError('source must be an existing directory within the project')
    source_id = source.relative_to(ROOT).as_posix()
    destination = ARCHIVE / source.name
    if destination.exists():
        raise FileExistsError(f'archive already exists: {destination}')

    names = ['index.json', 'session-log.jsonl', 'queue.json']
    names += sorted(path.name for path in source.glob('index-*.json'))
    if (source / 'plan.json').is_file():
        names.append('plan.json')
    names += sorted(path.name for pattern in ('sortie-*.json', 'task-*.json')
                    for path in source.glob(pattern))
    if len(names) != len(set(names)) or not any(name.startswith('sortie-') for name in names):
        raise ValueError('source must contain a sortie artifact')

    copies = {}
    originals = {}
    for name in names:
        original = (source / name).read_bytes()
        sanitized = sanitize(load_file(source / name), ROOT)
        if private_text(sanitized):
            raise ValueError(f'private content remains after redaction: {name}')
        copies[name] = encoded(sanitized, name)
        originals[name] = hashlib.sha256(original).hexdigest()

    metadata = {
        'source_run': source_id,
        'product_base_commit': product_commit,
        'entry': 'alashub queue --file <input> --run --allow-actions',
        'redactions': REDACTIONS,
        'source_files_sha256': originals,
        'files': {name: hashlib.sha256(data).hexdigest() for name, data in copies.items()},
    }
    with TemporaryDirectory(prefix='archive-real-', dir=ARCHIVE) as temporary:
        staging = Path(temporary) / source.name
        staging.mkdir()
        for name, data in copies.items():
            (staging / name).write_bytes(data)
        (staging / 'archive.json').write_bytes(encoded(metadata, 'archive.json'))
        records, errors = audit_archive(staging)
        if errors or not records or any(record['verdict'] != 'consistent' for record in records):
            raise ValueError(f'archived evidence failed audit: {errors or records}')
        staging.rename(destination)
    return destination


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path, help='completed run artifact directory')
    parser.add_argument('--product-commit', required=True, help='commit used for the product run')
    args = parser.parse_args()
    print(archive_run(args.source, args.product_commit))


if __name__ == '__main__':
    main()
