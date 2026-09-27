# -*- coding: utf-8 -*-
"""统一的 Engine 离线验收入口。

默认套件只验证当前产品执行图：Alas.Engine、Server/UI 边界、纯视觉 worker、
离线导出合同、结果合同和隐私边界。退役的 Core/S3/R5/Python 业务宿主脚本
保留在历史目录或作为单独 oracle 时，不会被本入口加载。

``--docs-only`` 保留用于同步脚本；它会跳过标记为真机的步骤。``--device-only``
只运行真机步骤。逐任务或逐地图的旧检查不属于当前产品验收。
"""
from __future__ import annotations

import argparse
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
PY = sys.executable

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# (脚本, 说明, 需要真机, 超时秒)
# 这些检查都属于 Engine 产品合同或离线来源校验；旧 Core/S3/R5 验收不再登记。
STEPS = [
    ("verify_privacy.py", "提交隐私边界", False, 120),
    ("verify_architecture.py", "Engine 唯一产品核心与纯视觉边界", False, 120),
    ("verify_result_contract.py", "sortie-result/1 跨语言合同", False, 300),
    ("verify_export_integrity.py", "离线导出完整性合同", False, 300),
    ("verify_config_export.py", "章节 Config 来源与继承导出", False, 300),
    ("verify_map_export.py", "MAP 声明导出", False, 300),
    ("verify_campaign_export.py", "Campaign 声明导出", False, 300),
    ("verify_pages_export.py", "页面规则导出", False, 180),
    ("verify_plan_export.py", "历史计划仅作离线导出完整性校验", False, 180),
]


def run_step(script: str, need_device: bool, timeout: int,
             docs_only: bool = False, device_only: bool = False):
    """Run one registered check and always retain its local diagnostic log."""
    if docs_only and need_device:
        return "skipped", 0.0, "--docs-only"
    if device_only and not need_device:
        return "skipped", 0.0, "--device-only"
    path = os.path.join(HERE, script)
    if not os.path.isfile(path):
        return "missing", 0.0, path
    log = Path(ROOT) / ".runtime/verification/verify_all" / (Path(script).stem + ".log")
    log.parent.mkdir(parents=True, exist_ok=True)
    started = time.time()
    process = None
    try:
        environment = dict(os.environ, PYTHONDONTWRITEBYTECODE="1", PYTHONUTF8="1")
        process = subprocess.Popen(
            [PY, path], cwd=ROOT, env=environment,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace",
            start_new_session=os.name != "nt")
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                               capture_output=True, timeout=20, check=False)
            else:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            stdout, stderr = process.communicate(timeout=20)
            log.write_text(stdout + "\n--- stderr ---\n" + stderr +
                           f"\nTimed out after {timeout} seconds.\n", encoding="utf-8")
            return "timeout", time.time() - started, f"超过 {timeout}s"
        log.write_text(stdout + "\n--- stderr ---\n" + stderr, encoding="utf-8")
        output = stderr or stdout or ""
        tail = [line for line in output.strip().splitlines() if line.strip()]
        return ("ok" if process.returncode == 0 else "fail"), time.time() - started, \
            (tail[-1][:160] if tail else "")
    except Exception as error:
        if process is not None and process.poll() is None:
            process.kill()
            process.communicate(timeout=20)
        detail = f"{type(error).__name__}: {error}"
        log.write_text(detail + "\n", encoding="utf-8")
        return "error", time.time() - started, detail


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--docs-only", action="store_true")
    modes.add_argument("--device-only", action="store_true")
    parser.add_argument("--only", help="逗号分隔的已登记检查名")
    try:
        args = parser.parse_args()
    except SystemExit as error:
        return error.code

    docs_only, device_only = args.docs_only, args.device_only
    registered = {step[0] for step in STEPS}
    only = None if args.only is None else {item.strip() for item in args.only.split(",") if item.strip()}
    if args.only is not None and (not only or only - registered):
        print("--only 包含未知或空检查名")
        return 2

    mode = "离线 Engine 检查" if docs_only else ("只跑真机" if device_only else "Engine 产品检查")
    if only:
        mode += "，只跑 " + ", ".join(sorted(only))
    print(f"=== Engine 验收（{mode}）===")

    results = []
    started = time.time()
    for script, description, need_device, timeout in STEPS:
        if only and script not in only:
            state, seconds, note = "skipped", 0.0, "--only"
        else:
            state, seconds, note = run_step(script, need_device, timeout, docs_only, device_only)
        print(f"{script:<30} {state:<8} {seconds:6.1f}s  {note}", flush=True)
        results.append(dict(script=script, description=description,
                            state=state, seconds=round(seconds, 3)))

    bad = [item for item in results if item["state"] in {"fail", "timeout", "error", "missing"}]
    summary = Path(ROOT) / ".runtime/verification/verify_all/results.json"
    summary.parent.mkdir(parents=True, exist_ok=True)
    summary.write_text(json.dumps({
        "mode": mode, "ok": not bad, "results": results,
    }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    detail = "" if not bad else ": " + ", ".join(f"{item['script']}({item['state']})" for item in bad)
    print(f"总耗时 {(time.time() - started) / 60:.1f} 分钟；{len(bad)} 步异常{detail}")
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main())
