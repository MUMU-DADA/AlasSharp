using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Alas.Client;
using Alas.Contracts;
using Alas.Server;

string root = Path.Combine(Path.GetFullPath(args.Single()), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var reservation = new TcpListener(IPAddress.Loopback, 0);
reservation.Start();
int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
reservation.Stop();
using var shutdown = new CancellationTokenSource();
var server = new ControlServer(root, root, "", "", null, null, port);
Task running = server.RunAsync(shutdown.Token);
try
{
    var endpoint = new Uri($"http://127.0.0.1:{port}/");
    using var client = new ControlClient(endpoint);
    for (int attempt = 0; ; attempt++)
    {
        try { await client.GetStateAsync(); break; }
        catch (HttpRequestException) when (attempt < 50) { await Task.Delay(100); }
    }
    var schema = await client.GetEngineSettingsAsync();
    Check(schema.Groups.SelectMany(group => group!["fields"]!.AsArray())
        .Select(field => field!["key"]!.GetValue<string>()).SequenceEqual(["AdbPath", "VisionRuntime", "OcrModelDirectory"]),
        "HTTP schema includes unavailable deployment fields");
    Check(!File.Exists(Path.Combine(root, "settings.json")), "GET wrote settings");
    var response = await client.PatchEngineSettingsAsync(new() { Values = new()
        { ["AdbPath"] = "tools/adb", ["VisionRuntime"] = "vision/runtime", ["OcrModelDirectory"] = "models" } });
    Check(response.Updated.SequenceEqual(["AdbPath", "OcrModelDirectory", "VisionRuntime"]), "Typed client lost patch acknowledgement");
    var reread = await client.GetEngineSettingsAsync();
    Check(reread.Groups[1]!["fields"]![0]!["value"]!.GetValue<string>() == "vision/runtime", "Read did not return saved settings");
    string file = Path.Combine(root, "settings.json");
    string before = await File.ReadAllTextAsync(file);
    try
    {
        await client.PatchEngineSettingsAsync(new() { Values = new() { ["AdbPath"] = "changed", ["PythonExecutable"] = "retired" } });
        throw new InvalidOperationException("Retired setting accepted");
    }
    catch (ControlApiException error) when (error.StatusCode == HttpStatusCode.BadRequest) { }
    Check(await File.ReadAllTextAsync(file) == before, "HTTP rejected patch partially saved");
    using var raw = new HttpClient();
    using var denied = await raw.PatchAsync(new Uri(endpoint, "api/settings"),
        new StringContent("{\"values\":{\"AdbPath\":\"unauthenticated\"}}", Encoding.UTF8, "application/json"));
    Check(denied.StatusCode == HttpStatusCode.Forbidden && await File.ReadAllTextAsync(file) == before, "Write token boundary lost");
    await client.CreateInstanceAsync(new() { Instance = "fixture" });
    var startup = await client.SetStartupRunAsync(new() { Instance = "fixture", Enabled = true });
    Check(startup.Enabled && (await client.GetStartupRunAsync("fixture")).Enabled, "Startup preference did not round-trip");
    Check(File.Exists(Path.Combine(root, "startup.json")) && !Directory.Exists(Path.Combine(root, "config")),
        "Native settings wrote upstream deployment files");
    Check((await client.GetStateAsync()).Active.Status == "idle", "Saving settings started a task");
    await File.WriteAllTextAsync(file, "{broken");
    try { await client.GetEngineSettingsAsync(); throw new InvalidOperationException("Corrupt settings accepted over HTTP"); }
    catch (ControlApiException error) when (error.StatusCode == HttpStatusCode.BadRequest) { }
    Console.WriteLine("PASS: real Server and typed Client settings read/write, token protection, atomic rejection, startup isolation and corrupt-file errors");
}
finally
{
    shutdown.Cancel();
    try { await running; }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
}

static void Check(bool condition, string message)
{ if (!condition) throw new InvalidOperationException(message); }
