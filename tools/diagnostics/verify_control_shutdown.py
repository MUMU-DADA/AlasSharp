"""Run real Kestrel + dry-run queue; graceful shutdown must reject late POSTs and flush artifacts."""
from __future__ import annotations

import json
import os
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from urllib.error import URLError
from xml.sax.saxutils import escape

from verify_control import request, wait_state

ROOT = Path(__file__).resolve().parents[2]
DOTNET = ROOT / '.runtime/dotnet/dotnet.exe'

# The test host only turns a local test file into the public shutdown token. It
# executes the production server and runtime, without a fake queue or device.
HARNESS = r'''
using Alas.Runtime;
using Alas.Server;
using System.Text.Json.Nodes;

string root = args[0], work = args[1];
int port = int.Parse(args[2]);
string repo = Environment.GetEnvironmentVariable("ALAS_REPO") ?? Path.Combine(root, ".runtime", "engine");
string data = Environment.GetEnvironmentVariable("ALAS_DATA") ?? Path.Combine(root, "data");
string tools = Path.Combine(root, "tools");
// Direct lifecycle guard: even an idle, already-closed workspace must refuse writes.
var closed = new ControlWorkspace(root, repo, data, tools, Path.Combine(work, "closed-runs"), Path.Combine(work, "closed"));
await closed.BeginShutdown();
var input = JsonNode.Parse("""{"queue":{"tasks":[{"id":"closed","kind":"task_catalog","input":{}}]}}""")!.AsObject();
try { closed.StartRun(input); throw new Exception("Closed workspace accepted a run"); }
catch (ControlWorkspaceUnavailableException) { }
try { closed.SaveQueueRequest(input); throw new Exception("Closed workspace accepted a draft"); }
catch (ControlWorkspaceUnavailableException) { }
using var shutdown = new CancellationTokenSource();
var server = new ControlServer(root, repo, data, tools, Path.Combine(work, "runs"), Path.Combine(work, "workspace"), port);
var running = server.RunAsync(shutdown.Token);
while (!running.IsCompleted && !File.Exists(Path.Combine(work, "shutdown.request")))
    await Task.Delay(20);
if (!running.IsCompleted)
{
    shutdown.Cancel();
    File.WriteAllText(Path.Combine(work, "stopping"), "shutdown was requested");
}
return await running;
'''


def wait_for(predicate, timeout=30):
    until = time.monotonic() + timeout
    while time.monotonic() < until:
        if predicate():
            return
        time.sleep(0.05)
    raise AssertionError('关闭验收等待条件超时')


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    except AttributeError:
        pass
    assert DOTNET.is_file(), '先准备项目内 .NET SDK'
    local = ROOT / '.runtime/control-shutdown-tests'
    local.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=local) as temporary:
        work = Path(temporary)
        project = work / 'ShutdownProbe.csproj'
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
<ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Server/Alas.Server.csproj'))}" /></ItemGroup></Project>''', encoding='utf-8')
        (work / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        environment = {**os.environ, 'DOTNET_ROOT': str(DOTNET.parent),
                       'DOTNET_CLI_HOME': str(ROOT / '.runtime/dotnet-home'),
                       'NUGET_PACKAGES': str(ROOT / '.runtime/nuget/packages'),
                       'DOTNET_CLI_TELEMETRY_OPTOUT': '1'}
        build = subprocess.run([str(DOTNET), 'build', str(project), '-c', 'Release',
                                '--source', str(ROOT / '.runtime/nuget/source'), '-p:NuGetAudit=false'],
                               cwd=ROOT, env=environment, capture_output=True, timeout=120)
        if build.returncode:
            (work / 'build.log').write_bytes(build.stdout + build.stderr)
            raise AssertionError('关闭验收宿主构建失败')
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        with (work / 'server.log').open('wb') as output:
            process = subprocess.Popen([str(DOTNET), str(work / 'bin/Release/net10.0/ShutdownProbe.dll'),
                                        str(ROOT), str(work), str(port)], cwd=ROOT,
                                       env=environment, stdout=output, stderr=output)
            pending = None
            try:
                def ready():
                    assert process.poll() is None, '服务意外退出'
                    try:
                        return request(base, '/api/state')[0] == 200
                    except URLError:
                        return False
                wait_for(ready)
                token = request(base, '/api/state')[1]['token']
                queue = {'tasks': [{'id': f'stage-{i}', 'kind': 'campaign_batch',
                                    'input': {'chapters': ['campaign.campaign_main.campaign_1_1']}}
                                   for i in range(200)]}
                assert request(base, '/api/run', data={'queue': queue}, token=token)[0] == 202
                snapshot = next((work / 'workspace').glob('request-*.json'))
                run_id = snapshot.stem.removeprefix('request-')
                # A directory at the marker path forces an IO failure on every OS.
                # In-memory boundary cancellation and the shutdown wait must survive.
                (work / 'workspace' / f'stop-{run_id}.request').mkdir()
                # Keep a second HTTP request in flight while stopping. Releasing it
                # after the first worker completes reproduces the old shutdown race.
                payload = json.dumps({'queue': {'tasks': [{'id': 'late', 'kind': 'task_catalog'}]}}).encode()
                pending = socket.create_connection(('127.0.0.1', port), timeout=10)
                pending.sendall((f'POST /api/run HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n'
                                 f'X-Alas-Token: {token}\r\nContent-Type: application/json\r\n'
                                 f'Content-Length: {len(payload)}\r\nConnection: close\r\n\r\n').encode()
                                + payload[:-1])
                # Observe that the first run is still active before requesting shutdown.
                state = wait_state(base, lambda s: s['active']['status'] == 'running')
                assert state['active']['mode'] == 'dry_run'
                (work / 'shutdown.request').write_text('stop', encoding='utf-8')
                wait_for(lambda: (work / 'stopping').exists())
                wait_for(lambda: len(list((work / 'runs').glob('*/session-log.jsonl'))) == 1)
                assert process.poll() is None, '关闭应等候已接受的 HTTP 请求'
                pending.sendall(payload[-1:])
                response = b''
                while chunk := pending.recv(8192):
                    response += chunk
                assert response.startswith(b'HTTP/1.1 409'), '关闭后不能接受延迟提交'
                document = json.loads(response.partition(b'\r\n\r\n')[2])
                assert '正在关闭' in document['error'], '应由关闭门禁拒绝，而非运行冲突'
                assert process.wait(timeout=15) == 0, '服务应正常退出'
                records = list((work / 'runs').glob('*/queue.json'))
                assert len(records) == 1, '关闭期间不能启动第二个队列'
                result = json.loads(records[0].read_text(encoding='utf-8'))
                assert result['outcome'] == 'cancelled' and result['stopped_early']
                assert len(result['tasks']) == 200
                assert any(task['outcome'] == 'skipped' for task in result['tasks'])
                assert all(task['outcome'] != 'failed' for task in result['tasks'])
                run = records[0].parent
                assert (run / 'state.json').is_file()
                assert len(list(run.glob('task-*.json'))) == 200
                logs = [json.loads(line) for line in (run / 'session-log.jsonl').read_text(encoding='utf-8').splitlines()]
                assert any(entry.get('message') == '识图宿主已释放' for entry in logs), '退出前应完成宿主释放与日志落盘'
                assert not any(entry.get('message') == '设备后端已配置' for entry in logs)
                assert len(list((work / 'workspace').glob('request-*.json'))) == 1
                print('Kestrel 关闭验收通过：停止标记写入失败仍边界取消、拒绝延迟 POST、200 份任务工件与日志完整、正常退出；无设备调用')
            finally:
                if pending:
                    pending.close()
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=10)
    return 0


if __name__ == '__main__':
    sys.exit(main())
