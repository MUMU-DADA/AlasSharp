"""Resolve a fixture's upstream source before importing any native modules."""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import sys

INVOCATION_DIRECTORY = Path.cwd()


def invocation_path(value):
    """CLI paths stay relative to the caller after native assets change cwd."""
    return str((INVOCATION_DIRECTORY / value).resolve())


def initialize(argv=None):
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument('--repo')
    parsed, _ = parser.parse_known_args(argv)
    root = Path(__file__).resolve().parent.parent
    repo = Path(parsed.repo or os.environ.get('ALAS_REPO') or root / '.runtime/engine').resolve()
    if not (repo / 'module/base/utils.py').is_file():
        raise FileNotFoundError(f'不是有效的上游运行时目录: {repo}')
    sys.path.insert(0, str(repo))
    # Native asset paths are relative to the selected upstream source.
    os.chdir(repo)
    return str(repo)
