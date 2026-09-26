#!/usr/bin/env python3
"""Resolve a real .NET host for offline checks, including system SDK installs.

Prefer the repository SDK, then an explicitly configured DOTNET_ROOT, then PATH.
A directory alone is not evidence of an installed host. Missing prerequisites fail
at executable(), rather than silently skipping the cross-language check.
"""
from __future__ import annotations

import os
import shutil
from collections.abc import Mapping
from pathlib import Path


def _host(root: Path) -> Path | None:
    for name in ("dotnet.exe", "dotnet"):
        candidate = root / name
        if candidate.is_file():
            return candidate.resolve()
    return None


def local_root(repo: Path | str) -> Path | None:
    host = _host(Path(repo) / ".runtime" / "dotnet")
    return host.parent if host else None


def resolved_executable(repo: Path | str | None = None,
                        env: Mapping[str, str] | None = None) -> Path | None:
    environment = os.environ if env is None else env
    if repo is not None:
        host = _host(Path(repo) / ".runtime" / "dotnet")
        if host:
            return host
    configured = environment.get("DOTNET_ROOT")
    if configured:
        host = _host(Path(configured))
        if host:
            return host
    found = shutil.which("dotnet", path=environment.get("PATH", ""))
    # Resolve symlinks: /usr/bin/dotnet is commonly a link into the runtime root.
    return Path(found).resolve() if found else None


def executable(repo: Path | str | None = None,
               env: Mapping[str, str] | None = None) -> Path:
    host = resolved_executable(repo, env)
    if host is None:
        raise RuntimeError("未找到 .NET 主机；请准备项目内 .runtime/dotnet、DOTNET_ROOT 或 PATH 上的 dotnet")
    return host


def resolved_root(repo: Path | str | None = None,
                  env: Mapping[str, str] | None = None) -> Path | None:
    host = resolved_executable(repo, env)
    return host.parent if host else None


def apply(env: Mapping[str, str], repo: Path | str | None = None) -> dict[str, str]:
    """Return an independent environment with a valid host root, if available."""
    result = dict(env)
    root = resolved_root(repo, env)
    if root is None:
        result.pop("DOTNET_ROOT", None)
    else:
        result["DOTNET_ROOT"] = str(root)
    return result


def skip_reason(repo: Path | str | None = None) -> str | None:
    """Compatibility diagnostic; required checks must still return failure."""
    if resolved_root(repo) is not None:
        return None
    return "本机没有可用的 .NET 主机（项目内 .runtime/dotnet、DOTNET_ROOT 和 PATH 均未找到）"
