"""Exercise real ControlWorkspace scheduler lifecycle with one synthetic host; no Python/device."""
from pathlib import Path
import os
import subprocess
import tempfile
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]
HARNESS = r'''
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Runtime;
using Alas.Vision;

static void Check(bool value, string name) { if (!value) throw new Exception(name); }
string root = args[0];
string repo = Path.Combine(root, "repo");
Directory.CreateDirectory(Path.Combine(repo, "config"));
File.WriteAllText(Path.Combine(repo, "config", "fixture.json"), """
{"Alas":{"Emulator":{"Serial":"fixture-device","ScreenshotMethod":"adb","ControlMethod":"ADB"}}}
""");
File.WriteAllText(Path.Combine(repo, "config", "template.json"), """{"Alas":{}}""");
var engine = new Engine();
int constructed = 0;
var workspace = new ControlWorkspace(root, repo, Path.Combine(root,"data"), Path.Combine(root,"tools"),
    Path.Combine(root,"runs"), Path.Combine(root,"workspace"), _ => { constructed++; return engine; });
var start = new JsonObject { ["instance"] = "fixture", ["confirm_actions"] = true };
try { workspace.StartScheduler(new JsonObject { ["instance"]="fixture" }); throw new Exception("Missing authorization accepted"); }
catch (ArgumentException) { }
Check(constructed == 0, "No host for denied start");
// Read-only service first: the action queue must reuse and configure this same host.
workspace.ReadHostJson("fixture-read", new JsonObject());
for (int attempt = 0; attempt < 2; attempt++)
{
    engine.Ready.Reset(); engine.Finish.Reset();
    workspace.StartScheduler(start);
    Check(engine.Ready.Wait(TimeSpan.FromSeconds(5)), "Scheduler did not enter host");
    var state = workspace.State();
    Check(state["active"]!["instance"]!.GetValue<string>() == "fixture", "Selected instance in activity");
    Check(state["active"]!["kind"]!.GetValue<string>() == "scheduler_run", "Scheduler activity kind");
    Check(state["active"]!["scheduler"]!["phase"]!.GetValue<string>() == "waiting", "Read live native state");
    try { workspace.StartScheduler(start); throw new Exception("Concurrent start accepted"); }
    catch (ControlWorkspaceUnavailableException) { }
    Task? shutdown = null;
    if (attempt == 0) Check(workspace.RequestStop(), "Stop accepted");
    else shutdown = workspace.BeginShutdown();
    Check(File.Exists(Path.Combine(engine.Directory!, "stop.request")), "Stop file reaches native boundary");
    Check(workspace.State()["active"]!["status"]!.GetValue<string>() == "running", "Never declare completion before native return");
    Check(shutdown is null || !shutdown.IsCompleted, "Shutdown must wait for native boundary");
    engine.Finish.Set();
    if (shutdown is not null) await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
    for (int n=0; n<100 && workspace.State()["active"]!["status"]!.GetValue<string>() == "running"; n++) await Task.Delay(10);
    state = workspace.State();
    Check(state["active"]!["status"]!.GetValue<string>() == "completed", "Worker completed");
    string run = state["active"]!["run_directory"]!.GetValue<string>();
    Check(File.Exists(Path.Combine(run, "queue.json")) && File.Exists(Path.Combine(run,"session-log.jsonl")), "Final evidence flushed");
    var queue = JsonNode.Parse(File.ReadAllText(Path.Combine(run,"queue.json")))!;
    Check(queue["outcome"]!.GetValue<string>() == "cancelled", "Stopping is not scheduler success");
}
Check(constructed == 1 && engine.Configurations == 1 && engine.Disposed, "Single host/device configuration, disposed at shutdown");
try { workspace.StartScheduler(start); throw new Exception("Shutdown accepted new run"); }
catch (ControlWorkspaceUnavailableException) { }
Console.WriteLine("PASS: direct Core scheduler selected instance, live state, shared host, concurrent-start rejection, boundary stop, shutdown and artifacts");

sealed class Engine : VisionEngineBase
{
    public ManualResetEventSlim Ready = new(false), Finish = new(false);
    public int Configurations;
    public bool Disposed;
    public string? Directory;
    protected override JsonNode CallRaw(string op, object? args)
    {
        var body = JsonSerializer.SerializeToNode(args)!.AsObject();
        if (op == "device_configure")
        {
            Configurations++;
            if (body["serial"]!.GetValue<string>() != "fixture-device") throw new Exception("Device instance mismatch");
            return new JsonObject { ["configured"] = body.DeepClone() };
        }
        if (op == "scheduler_run")
        {
            if (body["instance"]!.GetValue<string>() != "fixture") throw new Exception("Wrong instance");
            Directory = body["artifact_directory"]!.GetValue<string>();
            File.WriteAllText(Path.Combine(Directory,"state.json"), """{"instance":"fixture","phase":"waiting"}""");
            Ready.Set();
            if (!Finish.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Probe stop timeout");
            return new JsonObject { ["instance"]="fixture", ["decision"]="stopped", ["stop_observed"]=true,
                ["constructed"]=true,["ran"]=true,["dispatch_count"]=0,["failed_dispatches"]=0 };
        }
        return new JsonObject();
    }
    public override void Dispose() { Disposed = true; base.Dispose(); }
}
'''


def main():
    local = ROOT / '.runtime/verification'
    local.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='scheduler-core-', dir=local) as folder:
        work = Path(folder)
        project = work / 'SchedulerProbe.csproj'
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Core/Alas.Core.csproj'))}" /></ItemGroup></Project>''', encoding='utf-8')
        (work / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        env = dict(os.environ, DOTNET_CLI_HOME=str(ROOT / '.runtime/dotnet-home'),
                   NUGET_PACKAGES=str(ROOT / '.runtime/nuget/packages'), DOTNET_CLI_TELEMETRY_OPTOUT='1')
        build = subprocess.run(['dotnet', 'build', str(project), '-c', 'Release', '--source',
                                str(ROOT / '.runtime/nuget/source'), '-p:NuGetAudit=false'],
                               cwd=ROOT, env=env, capture_output=True, timeout=120)
        assert build.returncode == 0, (build.stdout + build.stderr).decode(errors='replace')
        run = subprocess.run(['dotnet', str(work / 'bin/Release/net10.0/SchedulerProbe.dll'), str(work)],
                             cwd=ROOT, env=env, capture_output=True, timeout=45)
        assert run.returncode == 0, (run.stdout + run.stderr).decode(errors='replace')
        print('PASS: Core scheduler lifecycle, selected instance, one host, stop, shutdown and artifacts')


if __name__ == '__main__':
    main()
