# -*- coding: utf-8 -*-
"""Verify the standalone loopback server and an explicitly selected UI root."""

from __future__ import annotations

import base64
import hashlib
import json
import os
import socket
import subprocess
import tempfile
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

import dotnet_env

ROOT = Path(__file__).resolve().parents[2]
DOTNET = dotnet_env.executable(ROOT)
SERVER = ROOT / "src" / "Alas.Server" / "bin" / "Release" / "net10.0" / "Alas.Server.dll"


def port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def get(base: str, path: str, *, method: str = "GET", headers: dict[str, str] | None = None, body=None):
    request = Request(base + path, method=method, headers=headers or {},
                      data=None if body is None else json.dumps(body).encode("utf-8"))
    try:
        with urlopen(request, timeout=3) as response:
            return response.status, response.headers, response.read()
    except HTTPError as error:
        return error.code, error.headers, error.read()


def wait_state(base: str) -> None:
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        try:
            status, _, body = get(base, "/api/state")
            if status == 200 and json.loads(body)["active"]["status"] == "idle":
                return
        except (URLError, TimeoutError, json.JSONDecodeError):
            pass
        time.sleep(0.1)
    raise AssertionError("standalone server did not start")


def normalized_script_hash(body: str) -> str:
    normalized = body.replace("\r\n", "\n").replace("\r", "\n")
    digest = base64.b64encode(hashlib.sha256(normalized.encode("utf-8")).digest()).decode("ascii")
    return f"'sha256-{digest}'"


def main() -> int:
    if not DOTNET.is_file() or not SERVER.is_file():
        raise AssertionError("build Alas.Server in Release before this check")
    with tempfile.TemporaryDirectory(prefix="alas-server-static-") as directory:
        root = Path(directory)
        (root / "outside.txt").write_text("outside", encoding="utf-8")
        importmap_body = '{\n  "imports": {\n    "ui": "./main.js"\n  }\n}'
        expected_hash = normalized_script_hash(importmap_body)
        policies: list[str] = []
        for label, newline in (("lf", "\n"), ("crlf", "\r\n"), ("cr", "\r")):
            ui = root / label
            ui.mkdir()
            index = (
                '<!doctype html><script type="importmap">' + importmap_body +
                '</script><script type="module" src="main.js"></script>'
                '<link rel="stylesheet" href="app.css">\n'
            )
            (ui / "index.html").write_bytes(index.replace("\n", newline).encode("utf-8"))
            (ui / "main.js").write_text("console.log('ui');", encoding="utf-8")
            (ui / "app.css").write_text("body { color: black; }", encoding="utf-8")
            (ui / "payload.wasm").write_bytes(b"\0asm\1\0\0\0")
            service_port = port()
            extra_port = port()
            base = f"http://127.0.0.1:{service_port}"
            env = {**os.environ, "ASPNETCORE_URLS": f"http://0.0.0.0:{extra_port}"}
            process = subprocess.Popen(
                [str(DOTNET), str(SERVER), "--root", str(root), "--ui-root", str(ui),
                 "--port", str(service_port)],
                cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env,
            )
            try:
                wait_state(base)
                status, headers, body = get(base, "/")
                assert status == 200 and b"<!doctype html>" in body
                assert headers["Content-Type"].startswith("text/html")
                policy = headers["Content-Security-Policy"]
                assert expected_hash in policy
                policies.append(policy)
                status, headers, _ = get(base, "/main.js")
                assert status == 200 and headers["Content-Type"].startswith("text/javascript")
                status, headers, _ = get(base, "/payload.wasm")
                assert status == 200 and headers["Content-Type"] == "application/wasm"
                status, headers, body = get(base, "/app.css", method="HEAD")
                assert status == 200 and not body and headers["Content-Length"]
                assert get(base, "/missing.js")[0] == 404
                assert get(base, "/%2e%2e/outside.txt")[0] == 404
                assert get(base, "/../outside.txt")[0] == 404
                assert get(base, "/api/state", headers={"Host": "example.invalid"})[0] == 403
                status, _, body = get(base, "/api/state")
                assert status == 200 and json.loads(body)["token"]
                if label == "lf":
                    token_headers = {"X-Alas-Token": json.loads(body)["token"], "Content-Type": "application/json"}
                    task_payload = {"instance": "fixture", "task": "observe", "input": {}}
                    # The task route is an Engine entry.  A missing Engine
                    # config instance is reported as 404; it must not fall
                    # back to the retired Core/Python scheduler.
                    assert get(base, "/api/tasks/run", method="POST", headers=token_headers,
                               body=task_payload)[0] == 404
                    script_payload = {"script": ""}
                    assert get(base, "/api/tasks/validate-script", method="POST", headers=token_headers,
                               body=script_payload)[0] == 501
                    assert get(base, "/api/scheduler/start", method="POST", headers=token_headers,
                               body={"instance": "fixture", "confirm_actions": True})[0] == 404
                    payload = {"queue": {"tasks": [{"id": "observe", "kind": "observe", "required": True}]}}
                    assert get(base, "/api/run", method="POST", headers=token_headers, body=payload)[0] == 202
                    deadline = time.monotonic() + 10
                    while True:
                        state = json.loads(get(base, "/api/state")[2])
                        if state["active"]["status"] != "running":
                            break
                        assert time.monotonic() < deadline, "Engine dry-run did not finish"
                        time.sleep(0.05)
                    assert state["active"]["status"] == "completed", state["active"]
                    assert state["report"]["queue_outcome"] == "dry_run" and state["report"]["evidence_complete"]
                    assert state["report"]["totals"]["tasks_succeeded"] == 0
                    assert len(state["live_tasks"]) == 1
                    stamp = state["report"]["stamp"]
                    assert json.loads(get(base, f"/api/report?stamp={stamp}")[2])["evidence_complete"]
                with socket.socket() as probe:
                    probe.settimeout(0.5)
                    assert probe.connect_ex(("127.0.0.1", extra_port)) != 0
            finally:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        assert len(set(policies)) == 1
    print("PASS: standalone Server static/loopback guards, Engine dry-run/report and HTTP 501 for unported capabilities")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
