"""SSE state feed: real Kestrel/client/dry-run and deterministic server lifecycle faults."""
from __future__ import annotations

import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
from xml.sax.saxutils import escape

import dotnet_env

ROOT = Path(__file__).resolve().parents[2]
DOTNET = dotnet_env.executable(ROOT)

HARNESS = r'''
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Alas.Client;
using Alas.Contracts;
using Alas.Runtime;
using Alas.Server;

static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
static async Task Until(Func<bool> ready, string reason)
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    try { while (!ready()) await Task.Delay(20, limit.Token); }
    catch (OperationCanceledException) { throw new Exception(reason); }
}
static async Task<ControlStateUpdate> Next(IAsyncEnumerator<ControlStateUpdate> stream)
{
    Check(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)), "Unexpected stream EOF");
    return stream.Current;
}

// Test the production fan-out independently of socket buffers: all observers
// share one sampler; pending capacity is bounded; failure ends the stream.
int reads = 0, value = 0;
bool fail = false;
await using (var feed = new ControlStateFeed(() =>
{
    Interlocked.Increment(ref reads);
    if (Volatile.Read(ref fail)) throw new IOException("injected snapshot read failure");
    return Volatile.Read(ref value).ToString();
}, CancellationToken.None))
{
    await Task.Delay(1150);
    Check(reads == 0, "No observers must mean no disk sampling");
    using var fast = feed.Subscribe();
    using var slow = feed.Subscribe();
    Check(reads == 1, "Two initial observers share a snapshot");
    var initial = await fast.Pending.Reader.ReadAsync();
    Check((await slow.Pending.Reader.ReadAsync()).Cursor == initial.Cursor, "Initial cursor shared");
    await Until(() => Volatile.Read(ref reads) >= 2, "Sampler did not run");
    Check(!fast.Pending.Reader.TryRead(out _), "Unchanged payload must not publish revisions");
    for (int i = 1; i <= 3; i++)
    {
        Volatile.Write(ref value, i);
        var update = await fast.Pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Check(update.Json == i.ToString(), "Fast observer receives current snapshot");
    }
    var latest = await slow.Pending.Reader.ReadAsync();
    Check(latest.Json == "3" && !slow.Pending.Reader.TryRead(out _), "Slow observer keeps only latest full snapshot");
    Volatile.Write(ref fail, true);
    try { await fast.Pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Expected fault"); }
    catch (System.Threading.Channels.ChannelClosedException error) { Check(error.InnerException is IOException, "Sampling fault is visible"); }
    Volatile.Write(ref fail, false);
    using var recovered = feed.Subscribe();
    Check((await recovered.Pending.Reader.ReadAsync()).Json == "3", "New subscription recovers after read fault");
    recovered.Dispose(); fast.Dispose(); slow.Dispose();
    int before = Volatile.Read(ref reads);
    await Task.Delay(1150);
    Check(reads == before, "Last disconnect stops sampling");
}

string root = args[0], work = args[1];
// Keep a writer open to deterministically check Windows sharing compatibility.
// Parsing an incomplete document must still report the original evidence error.
string sharing = Path.Combine(work, "sharing");
Directory.CreateDirectory(sharing);
string statePath = Path.Combine(sharing, "state.json");
using (var writer = new FileStream(statePath, FileMode.Create, FileAccess.Write, FileShare.Read))
{
    writer.Write(Encoding.UTF8.GetBytes("{\"completed\":{}}")); writer.Flush();
    Check(!RunReport.Build(sharing).Findings.Any(f => f.Code == "unreadable_artifact" && f.Artifact == statePath),
        "Report reads must coexist with the artifact writer");
    writer.SetLength(1); writer.Flush();
    Check(RunReport.Build(sharing).Findings.Any(f => f.Code == "unreadable_artifact" && f.Artifact == statePath),
        "Concurrent read must not suppress incomplete evidence findings");
}
int port = int.Parse(args[2]);
var endpoint = new Uri($"http://127.0.0.1:{port}");
string repo = Environment.GetEnvironmentVariable("ALAS_REPO") ?? Path.Combine(root, ".runtime", "engine");
string data = Environment.GetEnvironmentVariable("ALAS_DATA") ?? Path.Combine(root, "data");
using var shutdown = new CancellationTokenSource();
var server = new ControlServer(root, repo, data, Path.Combine(root, "tools"), Path.Combine(work, "runs"), Path.Combine(work, "workspace"), port);
Task running = server.RunAsync(shutdown.Token);
using var client = new ControlClient(endpoint);
using var observer = new ControlClient(endpoint);
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
try
{
    using var http = new HttpClient();
    foreach (var (header, content) in new[] { ("Origin", "http://example.invalid"), ("Host", "example.invalid") })
    {
        using var denied = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "api/events"));
        denied.Headers.Add(header, content);
        using var result = await http.SendAsync(denied, lifetime.Token);
        Check(result.StatusCode == HttpStatusCode.Forbidden, "SSE keeps same-origin/host guard");
    }
    using (var malformed = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "api/events")))
    {
        malformed.Headers.Add("Last-Event-ID", "bad-cursor");
        using var result = await http.SendAsync(malformed, lifetime.Token);
        Check(result.StatusCode == HttpStatusCode.BadRequest, "Malformed cursor rejected before streaming");
    }

    await using var events = client.WatchStateAsync(cancellationToken: lifetime.Token).GetAsyncEnumerator();
    var initial = await Next(events);
    Check(initial.Reset && initial.State.Active.Status == "idle", "Initial reset carries authoritative idle state");
    string cursor = initial.Cursor;
    await using (var same = observer.WatchStateAsync(cursor, lifetime.Token).GetAsyncEnumerator())
        Check(!(await Next(same)).Reset, "Current cursor reconnect provides full snapshot without reset");
    await using (var stale = observer.WatchStateAsync(new string('0', 32) + ":1", lifetime.Token).GetAsyncEnumerator())
        Check((await Next(stale)).Reset, "Different service epoch resets state");

    var queue = new JsonObject { ["tasks"] = new JsonArray(Enumerable.Range(0, 200).Select(i => (JsonNode)new JsonObject
    {
        ["id"] = $"stage-{i}", ["kind"] = "campaign_batch",
        ["input"] = new JsonObject { ["chapters"] = new JsonArray("campaign.campaign_main.campaign_1_1") }
    }).ToArray()) };
    // The SSE state must suffice to acquire the write token (no GET /api/state first).
    await client.StartRunAsync(new() { Queue = queue }, lifetime.Token);
    bool liveLog = false;
    for (int i = 0; i < 100; i++)
    {
        var state = await observer.GetStateAsync(lifetime.Token);
        if (state.Active.Status == "running" && state.RecentLogs.Count > 0 && state.Active.RunDirectory is { } directory)
        {
            Check(!File.Exists(Path.Combine(directory, "session-log.jsonl")), "Running logs came from memory, not final log file");
            liveLog = true;
            break;
        }
        await Task.Delay(10, lifetime.Token);
    }
    Check(liveLog, "Must observe runtime logs before disposal");
    var changed = await Next(events);
    Check(changed.Cursor != cursor && !changed.Reset, "Changed state advances snapshot cursor");
    await using (var old = observer.WatchStateAsync(cursor, lifetime.Token).GetAsyncEnumerator())
        Check((await Next(old)).Reset, "Obsolete cursor receives reset rather than fictitious replay");

    // Disconnect observers; commands continue independently and complete normally.
    await events.DisposeAsync();
    ControlState completed;
    do { await Task.Delay(30, lifetime.Token); completed = await observer.GetStateAsync(lifetime.Token); }
    while (completed.Active.Status == "running");
    Check(!completed.Active.StopRequested && completed.Report?["queue_outcome"]?.GetValue<string>() == "dry_run",
        $"SSE disconnect must not stop or reinterpret queue; status={completed.Active.Status}, error={completed.Active.Error}, outcome={completed.Report?["queue_outcome"]}");
    Check(completed.LiveTasks.Count == 200 && completed.RecentLogs.Count <= 80, "All tasks and bounded recent log window");
    Check(completed.Report?["device_configure_count"]?.GetValue<int>() == 0, "No device configured");
    Check(!completed.RecentLogs.Any(n => n?["message"]?.GetValue<string>() == "识图宿主已释放"),
        "Queue completion keeps the shared host alive");
    var logIds = completed.RecentLogs.Select(n => n!["id"]!.GetValue<long>()).ToArray();
    Check(logIds.All(id => id > 0) && logIds.SequenceEqual(logIds.Order()) && logIds.Distinct().Count() == logIds.Length,
        "SSE carries stable ordered Core log IDs");

    // An open stream must not keep Kestrel alive through its HTTP shutdown deadline.
    await using var closing = client.WatchStateAsync(cancellationToken: lifetime.Token).GetAsyncEnumerator();
    await Next(closing);
    var eof = closing.MoveNextAsync().AsTask();
    shutdown.Cancel();
    Check(!await eof.WaitAsync(TimeSpan.FromSeconds(5)), "ApplicationStopping promptly closes the stream");
    await running.WaitAsync(TimeSpan.FromSeconds(5));
    var finalLog = File.ReadLines(Path.Combine(completed.Active.RunDirectory!, "session-log.jsonl"))
        .Select(line => JsonNode.Parse(line)).ToArray();
    Check(finalLog.Count(n => n?["message"]?.GetValue<string>() == "识图宿主已释放") == 1,
        "Service shutdown records shared-host disposal exactly once");
    Check(File.Exists(Path.Combine(completed.Active.RunDirectory!, "state.json")), "Shutdown preserves state artifact");
    using var secondShutdown = new CancellationTokenSource();
    var restarted = new ControlServer(root, repo, data, Path.Combine(root, "tools"), Path.Combine(work, "runs"), Path.Combine(work, "workspace"), port);
    var restartedTask = restarted.RunAsync(secondShutdown.Token);
    try
    {
        await using var afterRestart = observer.WatchStateAsync(changed.Cursor, lifetime.Token).GetAsyncEnumerator();
        var reset = await Next(afterRestart);
        Check(reset.Reset && reset.Cursor[..32] != changed.Cursor[..32] && reset.State.Token != changed.State.Token,
            "Actual service restart replaces cursor epoch and write token");
        Check(reset.State.Runs["runs"]?.AsArray().Count == 1, "Restart preserves completed artifacts/history");
    }
    finally { secondShutdown.Cancel(); await restartedTask.WaitAsync(TimeSpan.FromSeconds(5)); }
    Console.WriteLine("PASS: real Kestrel SSE/client, reset/reconnect, live runtime logs, bounded fan-out, sampling fault recovery, disconnect independence and prompt shutdown; zero devices/windows.");
}
finally
{
    shutdown.Cancel();
    await running.WaitAsync(TimeSpan.FromSeconds(45));
}
'''


def main() -> int:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    local = ROOT / '.runtime/control-events-tests'
    local.mkdir(parents=True, exist_ok=True)
    environment = {**os.environ, 'DOTNET_ROOT': str(DOTNET.parent),
                   'DOTNET_CLI_HOME': str(ROOT / '.runtime/dotnet-home'),
                   'NUGET_PACKAGES': str(ROOT / '.runtime/nuget/packages')}
    with tempfile.TemporaryDirectory(dir=local) as temporary:
        work = Path(temporary)
        project = work / 'EventsProbe.csproj'
        references = ''.join(f'<ProjectReference Include="{escape(str(ROOT / f"src/Alas.{name}/Alas.{name}.csproj"))}" />'
                             for name in ('Client', 'Server'))
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>Alas.ControlProbe</AssemblyName></PropertyGroup>
<ItemGroup>{references}</ItemGroup></Project>''', encoding='utf-8')
        (work / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        build = subprocess.run([str(DOTNET), 'build', str(project), '-c', 'Release',
                                *(['--source', str(ROOT / '.runtime/nuget/source')]
                                   if (ROOT / '.runtime/nuget/source').is_dir() else []), '-p:NuGetAudit=false'],
                               cwd=ROOT, env=environment, capture_output=True, timeout=120)
        assert build.returncode == 0, (build.stdout + build.stderr).decode('utf-8', errors='replace')
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        result = subprocess.run([str(DOTNET), str(work / 'bin/Release/net10.0/Alas.ControlProbe.dll'),
                                 str(ROOT), str(work), str(port)], cwd=ROOT, env=environment,
                                capture_output=True, timeout=120)
        (local / 'latest.log').write_bytes(result.stdout + result.stderr)
        assert result.returncode == 0, result.stderr.decode('utf-8', errors='replace')
        print('\n'.join(line for line in result.stdout.decode('utf-8', errors='replace').splitlines()
                        if line.startswith('PASS:')))
    return 0


if __name__ == '__main__':
    sys.exit(main())
