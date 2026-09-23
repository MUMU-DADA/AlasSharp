#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查待提交文本中的个人目录和明确凭据；仅输出相对路径与类别。

范围是 Git 已跟踪文件和未忽略的新文件。JSON/JSONL 另外检查解码后的字符串，
避免 Unicode 转义隐藏路径或凭据。公开上游归属、版权、普通标识符和回环地址
不属于凭据，不用开发者姓名或本机用户名作为硬编码词表。此脚本不调用设备。
"""
from __future__ import annotations

import codecs
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
from collections.abc import Iterator

ROOT = Path(__file__).resolve().parents[2]
FORBIDDEN_TRACKED_ROOTS = {"runs", ".runtime"}

# 占位符（如 <user>、%USERNAME% 或 {user}）不当成真实的个人目录。
PATTERNS = {
    "personal_home_path": re.compile(
        r"(?i)(?:\b[A-Z]:[\\/]+Users[\\/]+|/(?:home|Users)/)"
        r"[^\s\\/:\"<>|{}%]+"),
    "github_token": re.compile(
        r"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,})\b"),
    "openai_api_key": re.compile(
        r"\bsk-(?:(?:proj|svcacct|org)-)?[A-Za-z0-9_-]{32,}\b"),
    "aws_access_key": re.compile(r"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
    "google_api_key": re.compile(r"\bAIza[A-Za-z0-9_-]{35}\b"),
    "slack_token": re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{20,}\b"),
    "private_key": re.compile(
        r"-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----"),
    "credential_url": re.compile(r"https?://[^\s/:<>\"']+:[^\s/@<>\"']+@", re.I),
}


def git_files(*args: str) -> set[str]:
    result = subprocess.run(
        ["git", "ls-files", "-z", *args], cwd=ROOT,
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=True)
    return {os.fsdecode(item) for item in result.stdout.split(b"\0") if item}


def decode_text(raw: bytes) -> str | None:
    """只扫描文本；支持 UTF-8 与带 BOM 的 UTF-16/32，不解析二进制资源。"""
    if raw.startswith((codecs.BOM_UTF32_LE, codecs.BOM_UTF32_BE)):
        encoding = "utf-32"
    elif raw.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)):
        encoding = "utf-16"
    elif b"\0" in raw:
        return None
    else:
        encoding = "utf-8-sig"
    try:
        return raw.decode(encoding)
    except UnicodeError:
        return None


def json_strings(value: object) -> Iterator[str]:
    if isinstance(value, str):
        yield value
    elif isinstance(value, list):
        for child in value:
            yield from json_strings(child)
    elif isinstance(value, dict):
        for key, child in value.items():
            yield key
            yield from json_strings(child)


def scan_text(text: str, suffix: str) -> set[str]:
    found = {category for category, pattern in PATTERNS.items() if pattern.search(text)}
    documents = text.splitlines() if suffix == ".jsonl" else [text]
    if suffix not in {".json", ".jsonl"}:
        return found
    for document in documents:
        try:
            value = json.loads(document)
        except (ValueError, RecursionError):
            # JSON 格式校验属于对应数据合同；这里仍保留原始文本的检查结果。
            continue
        for string in json_strings(value):
            found.update(category for category, pattern in PATTERNS.items()
                         if pattern.search(string))
    return found


def main() -> int:
    try:
        tracked = git_files()
        candidates = tracked | git_files("--others", "--exclude-standard")
    except (OSError, subprocess.CalledProcessError):
        print("隐私检查失败：git_file_inventory_unavailable")
        return 1

    findings: dict[str, set[str]] = {}
    text_count = 0
    for relative in sorted(candidates):
        categories = set()
        if relative in tracked and PurePosixPath(relative).parts[0] in FORBIDDEN_TRACKED_ROOTS:
            categories.add("tracked_local_artifact")
        path = ROOT / relative
        if path.is_symlink():
            categories.update(scan_text(os.readlink(path), ""))
        elif path.is_file():
            try:
                text = decode_text(path.read_bytes())
            except OSError:
                categories.add("file_unreadable")
            else:
                if text is not None:
                    text_count += 1
                    categories.update(scan_text(text, path.suffix.lower()))
        if categories:
            findings[relative] = categories

    for relative, categories in findings.items():
        # 不输出匹配文本、绝对路径、环境变量、Git remote 或异常详情。
        print(f"{relative}: {', '.join(sorted(categories))}")
    if findings:
        print(f"隐私检查失败：{len(findings)} 个文件；检查 {text_count} 个文本文件")
        return 1
    print(f"隐私检查通过：检查 {text_count} 个文本文件，无个人目录或明确凭据")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass
    sys.exit(main())
