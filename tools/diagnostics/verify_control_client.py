"""Shared client against real Kestrel, plus transport faults; no UI or device actions."""
from __future__ import annotations

import os
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
from urllib.error import URLError
from xml.sax.saxutils import escape

from verify_control import request

ROOT = Path(__file__).resolve().parents[2]
DOTNET = ROOT / '.runtime/dotnet/dotnet.exe'
SERVER = ROOT / 'src/Alas.DataTool/bin/Release/net10.0/alashub.dll'

HARNESS = r'''
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Client;
using Alas.Contracts;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static async Task<T> Throws<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); }
    catch (T error) { return error; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static JsonObject Queue(int count) => new()
{
    ["tasks"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
    {
        ["id"] = $"stage-{i}", ["kind"] = "campaign_batch",
        ["input"] = new JsonObject { ["chapters"] = new JsonArray("campaign.campaign_main.campaign_1_1") }
    }).ToArray())
};

Check(!JsonSerializer.IsReflectionEnabledByDefault, "Client test must disable JSON reflection");
var endpoint = new Uri(args[0]);
using var client = new ControlClient(endpoint);
await Throws<InvalidOperationException>(() => client.RequestStopAsync());
var initial = await client.GetStateAsync();
Check(initial.Active.Status == "idle" && initial.Report is null, "Initial state contract");
var queue = Queue(1);
await client.SaveQueueAsync(queue);
Check(JsonNode.DeepEquals((await client.GetStateAsync()).Queue, queue), "Draft roundtrip");
var authorization = await Throws<ControlApiException>(() => client.StartRunAsync(new() { Queue = queue, Mode = ControlRunMode.Actions }));
Check(authorization.StatusCode == HttpStatusCode.BadRequest && authorization.Message.Contains("授权"), "Server enforces explicit action authorization");
Check((await client.GetStateAsync()).Runs["runs"]?.AsArray().Count == 0, "Rejected action must not create run artifacts");
Check((await Throws<ControlApiException>(() => client.GetReportAsync("absent"))).StatusCode == HttpStatusCode.NotFound, "Missing report keeps 404");
Check((await Throws<ControlApiException>(() => client.GetReportAsync("../escape"))).StatusCode == HttpStatusCode.BadRequest, "Report identifier stays query data");

// A caller closing its HTTP client after acceptance must not send /stop or own
// the queue lifetime. A second client observes the actual completed dry-run.
using (var first = new ControlClient(endpoint))
{
    await first.GetStateAsync();
    await first.StartRunAsync(new() { Queue = queue });
}
ControlState finished;
using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
{
    do
    {
        await Task.Delay(30, deadline.Token);
        finished = await client.GetStateAsync(deadline.Token);
    } while (finished.Active.Status == "running");
}
Check(finished.Active.Status == "completed" && !finished.Active.StopRequested, "HTTP client disposal must not stop accepted work");
Check(finished.Report?["queue_outcome"]?.GetValue<string>() == "dry_run", "Actual dry-run report outcome is retained");
Check(finished.Report?["device_configure_count"]?.GetValue<int>() == 0, "Dry-run never configures a device");
Check(finished.LiveTasks.Count == 1 && finished.RecentLogs.Count > 0, "Tasks and logs are preserved");
string stamp = Path.GetFileName(finished.Active.RunDirectory)!;
var report = await client.GetReportAsync(stamp);
Check(JsonNode.DeepEquals(report, finished.Report), "Report JSON is preserved without reinterpretation");
Check(finished.LiveTasks[0]?["outcome"]?.GetValue<string>() == "dry_run", "Task dry-run must not be reinterpreted as success");
Check(finished.Runs["runs"]?.AsArray().Count == 1, "Nonempty history envelope roundtrip");

await client.StartRunAsync(new() { Queue = Queue(200) });
Check((await Throws<ControlApiException>(() => client.StartRunAsync(new() { Queue = queue }))).StatusCode == HttpStatusCode.Conflict, "Concurrent start keeps 409");
await client.RequestStopAsync();
using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
{
    do
    {
        await Task.Delay(30, deadline.Token);
        finished = await client.GetStateAsync(deadline.Token);
    } while (finished.Active.Status == "running");
}
Check(finished.Report?["queue_outcome"]?.GetValue<string>() == "cancelled", "Boundary stop preserves cancelled outcome");
Check(finished.LiveTasks.Count == 200 && finished.Report?["device_configure_count"]?.GetValue<int>() == 0, "Stopped queue has every artifact and no device");
Check((await Throws<ControlApiException>(() => client.RequestStopAsync())).StatusCode == HttpStatusCode.Conflict, "Idle stop keeps 409");

// These tests isolate faults which cannot be induced deterministically in a
// healthy local service. They are transport tests, not game/runtime evidence.
string fixture = JsonSerializer.Serialize(initial, ControlJsonContext.Default.ControlState);
var fault = new FaultHandler(fixture);
using var transport = new HttpClient(fault);
var probe = new ControlClient(transport, endpoint);
await probe.GetStateAsync();
fault.Behavior = "drop";
await Throws<HttpRequestException>(() => probe.StartRunAsync(new() { Queue = queue }));
Check(fault.Writes == 1 && fault.Stops == 0, "Unknown acceptance must not retry or send stop");
Check(fault.ExplicitLength && fault.WriteToken == initial.Token, "Known UTF-8 length and per-service token");
fault.Behavior = "cancel";
using (var cancelled = new CancellationTokenSource())
{
    var pending = probe.StartRunAsync(new() { Queue = queue }, cancelled.Token);
    await fault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancelled.Cancel();
    await Throws<OperationCanceledException>(() => pending);
}
Check(fault.Writes == 2 && fault.Stops == 0, "Cancelled HTTP request must not retry or request queue cancellation");
int before = fault.Writes;
await Throws<ArgumentException>(() => probe.SaveQueueAsync(new JsonObject { ["text"] = new string('中', 400000) }));
Check(fault.Writes == before, "Request size is bounded in UTF-8 bytes before sending");
fault.Behavior = "malformed";
await Throws<ControlProtocolException>(() => probe.GetStateAsync());
fault.Behavior = "missing";
await Throws<ControlProtocolException>(() => probe.GetStateAsync());
fault.Behavior = "html";
await Throws<ControlProtocolException>(() => probe.GetStateAsync());
fault.Behavior = "false-ack";
await Throws<ControlProtocolException>(() => probe.RequestStopAsync());
using (var redirects = new ControlClient(new Uri(args[1])))
{
    await redirects.GetStateAsync();
    var redirect = await Throws<ControlApiException>(() => redirects.StartRunAsync(new() { Queue = queue }));
    Check(redirect.StatusCode == HttpStatusCode.TemporaryRedirect, "Default transport must not follow 307 or forward the write token");
}
Console.WriteLine("PASS: shared client real HTTP, explicit authorization, full artifacts, stop, client disposal, no write retries, request cancellation and reflection-free JSON; no device/UI.");

sealed class FaultHandler(string fixture) : HttpMessageHandler
{
    public string Behavior = "state";
    public int Writes, Stops;
    public bool ExplicitLength;
    public string? WriteToken;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method == HttpMethod.Post)
        {
            Writes++;
            if (request.RequestUri!.AbsolutePath == "/api/stop") Stops++;
            byte[] payload = await request.Content!.ReadAsByteArrayAsync(token);
            ExplicitLength = request.Content.Headers.ContentLength == payload.Length;
            WriteToken = request.Headers.GetValues(ControlProtocol.TokenHeader).Single();
        }
        if (Behavior == "drop") throw new HttpRequestException("Simulated loss after possible acceptance");
        if (Behavior == "cancel")
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        string content = Behavior switch { "malformed" => "{", "missing" => "{}", "html" => "<html/>", "false-ack" => "{\"ok\":false}", _ => fixture };
        return new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, Behavior == "html" ? "text/html" : "application/json") };
    }
}
'''


class RedirectProbe(BaseHTTPRequestHandler):
    forwarded = 0

    def log_message(self, *_):
        pass

    def do_GET(self):
        payload = json.dumps({'token': 'test-token', 'queue': {}, 'active': {'status': 'idle'},
                              'report': None, 'live_tasks': [], 'recent_logs': [], 'runs': {}}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length', '0')))
        if self.path == '/forwarded':
            type(self).forwarded += 1
        self.send_response(307 if self.path != '/forwarded' else 200)
        self.send_header('Location', '/forwarded')
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', '11')
        self.end_headers()
        self.wfile.write(b'{"ok":true}')


def main() -> int:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    local = ROOT / '.runtime/control-client-tests'
    local.mkdir(parents=True, exist_ok=True)
    environment = {**os.environ, 'DOTNET_ROOT': str(DOTNET.parent),
                   'DOTNET_CLI_HOME': str(ROOT / '.runtime/dotnet-home'),
                   'NUGET_PACKAGES': str(ROOT / '.runtime/nuget/packages'),
                   'DOTNET_CLI_TELEMETRY_OPTOUT': '1'}
    with tempfile.TemporaryDirectory(dir=local) as temporary:
        work = Path(temporary)
        project = work / 'ClientProbe.csproj'
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault></PropertyGroup>
<ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Client/Alas.Client.csproj'))}" /></ItemGroup></Project>''', encoding='utf-8')
        (work / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        build = subprocess.run([str(DOTNET), 'build', str(project), '-c', 'Release',
                                '--source', str(ROOT / '.runtime/nuget/source'), '-p:NuGetAudit=false'],
                               cwd=ROOT, env=environment, capture_output=True, timeout=120)
        assert build.returncode == 0, (build.stdout + build.stderr).decode('utf-8', errors='replace')
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        with (work / 'server.log').open('wb') as output:
            process = subprocess.Popen([str(DOTNET), str(SERVER), 'control', '--port', str(port),
                                        '--workspace', str(work / 'workspace'), '--artifacts', str(work / 'runs')],
                                       cwd=ROOT, env=environment, stdout=output, stderr=output)
            try:
                until = time.monotonic() + 15
                while True:
                    assert process.poll() is None and time.monotonic() < until, 'Kestrel 未能启动'
                    try:
                        if request(base, '/api/state')[0] == 200:
                            break
                    except URLError:
                        time.sleep(0.05)
                redirect_server = ThreadingHTTPServer(('127.0.0.1', 0), RedirectProbe)
                thread = Thread(target=redirect_server.serve_forever, daemon=True)
                thread.start()
                try:
                    redirect_base = f'http://127.0.0.1:{redirect_server.server_port}'
                    result = subprocess.run([str(DOTNET), str(work / 'bin/Release/net10.0/ClientProbe.dll'), base, redirect_base],
                                            cwd=ROOT, env=environment, capture_output=True, timeout=120)
                    assert RedirectProbe.forwarded == 0, '客户端不得自动转发写请求和令牌'
                finally:
                    redirect_server.shutdown()
                    redirect_server.server_close()
                    thread.join(timeout=5)
                print(result.stdout.decode('utf-8', errors='replace'))
                assert result.returncode == 0, result.stderr.decode('utf-8', errors='replace')
            finally:
                if process.poll() is None:
                    process.terminate()
                process.wait(timeout=10)
    return 0


if __name__ == '__main__':
    sys.exit(main())
