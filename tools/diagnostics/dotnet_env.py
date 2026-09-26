#!/usr/bin/env python3
"""解析 .NET 运行时目录，避免把**不存在的目录**塞给 `DOTNET_ROOT`。

背景（实测）：`verify_config_get` / `verify_account_state_cache` / `verify_periodic_run_result` /
`verify_task_schedule` 把 `DOTNET_ROOT` 固定指向 `<repo>/.runtime/dotnet`，而本机并没有这个目录
→ apphost 报 `missing_runtime=true`、`verify_all.py` 里这些步骤全红。
（同族的 `verify_deploy_*` 能"通过"是因为它们在没有本地 dotnet 时**跳过**了。）

规则：
  1. `<repo>/.runtime/dotnet` 存在 → 用它；
  2. 否则用 `PATH` 上 `dotnet` 所在目录（系统 SDK）；
  3. 两者都没有 → **不要设** `DOTNET_ROOT`（宁可不设，也不指向不存在的目录），
     调用方可用 `skip_reason()` 如实跳过。
"""
from __future__ import annotations

import shutil
from pathlib import Path


def local_root(repo: Path | str) -> Path | None:
    candidate = Path(repo) / ".runtime" / "dotnet"
    if (candidate / "dotnet.exe").is_file() or (candidate / "dotnet").is_file() \
            or (candidate / "shared").is_dir():
        return candidate
    return None


def resolved_root(repo: Path | str | None = None) -> Path | None:
    if repo is not None:
        local = local_root(repo)
        if local is not None:
            return local
    found = shutil.which("dotnet")
    return Path(found).parent if found else None


def apply(env: dict, repo: Path | str | None = None) -> dict:
    """把 `DOTNET_ROOT` 设成真实存在的位置；解析不到就删掉它（不指向不存在的目录）。"""
    result = dict(env)
    root = resolved_root(repo)
    if root is None:
        result.pop("DOTNET_ROOT", None)
    else:
        result["DOTNET_ROOT"] = str(root)
    return result


def skip_reason(repo: Path | str | None = None) -> str | None:
    """没有可用的 .NET 时给出人话原因，否则 None。"""
    if resolved_root(repo) is not None:
        return None
    return ("本机没有可用的 .NET：既没有 <repo>/.runtime/dotnet，PATH 上也没有 dotnet"
            "（有 SDK/运行时之后再跑本检查）")
