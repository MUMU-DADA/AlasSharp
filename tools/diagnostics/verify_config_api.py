#!/usr/bin/env python3
"""Offline contract check for the server-backed upstream configuration API."""
from __future__ import annotations

import json
import os
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.exe"


def request(base: str, path: str, method: str = "GET", body: dict | None = None,
            token: str | None = None) -> tuple[int, dict]:
    raw = None if body is None else json.dumps(body, ensure_ascii=False).encode()
    headers = {"Content-Type": "application/json"}
    if token:
        headers["X-Alas-Token"] = token
    req = urllib.request.Request(base + path, data=raw, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=5) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read())


def main() -> int:
    if not EXE.is_file():
        raise SystemExit("先构建 src/Alas.Server/Alas.Server.csproj")
    with tempfile.TemporaryDirectory(prefix="alas-config-api-") as folder:
        root = Path(folder)
        port = 18877
        process = subprocess.Popen(
            [str(EXE), "--root", str(ROOT), "--repo", str(ROOT / ".runtime" / "engine"),
             "--data", "data", "--tools", "tools", "--workspace", str(root / "workspace"),
             "--port", str(port)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            cwd=ROOT)
        base = f"http://127.0.0.1:{port}"
        try:
            for _ in range(60):
                try:
                    status, state = request(base, "/api/state")
                    if status == 200:
                        break
                except Exception:
                    time.sleep(.1)
            else:
                raise AssertionError("控制服务未启动")
            token = state["token"]
            assert request(base, "/api/instances")[0] == 200
            schema_status, schema = request(base, "/api/schema?language=zh-CN")
            assert schema_status == 200 and {"menu", "args", "translations"} <= schema.keys()
            get_status, config = request(base, "/api/config/alas")
            assert get_status == 200 and config["instance"] == "alas" and len(config["revision"]) == 64
            path = "Alas.Emulator.PackageName"
            old = config["values"]["Alas"]["Emulator"]["PackageName"]
            assert request(base, "/api/config/alas", "PATCH", {
                "revision": config["revision"], "changes": [{"path": path, "value": old}],
            })[0] == 403
            patch_status, patched = request(base, "/api/config/alas", "PATCH", {
                "revision": config["revision"], "changes": [{"path": path, "value": old}],
            }, token)
            assert patch_status == 200 and patched["values"]["Alas"]["Emulator"]["PackageName"] == old
            bad_status, _ = request(base, "/api/config/alas", "PATCH", {
                "changes": [{"path": path, "value": 1}],
            }, token)
            assert bad_status >= 400
            print("PASS: instances/schema/config GET, token boundary, typed patch and invalid value rejection")
            return 0
        finally:
            process.terminate()
            process.wait(timeout=10)


if __name__ == "__main__":
    raise SystemExit(main())
