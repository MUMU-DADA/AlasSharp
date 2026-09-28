using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class DeviceWatchdogChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed record Op(string Kind, string Name = "", double Seconds = 0);
    private sealed record Sample(bool Running, Op[] Operations);
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class Application(bool running) : IApplicationHealth
    {
        public int Inspections { get; private set; }
        public bool Fail { get; set; }
        public ValueTask<bool> IsRunningAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Inspections++; if (Fail) throw new IOException("Synthetic application query failure"); return ValueTask.FromResult(running); }
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask RefreshOrientationAsync(CancellationToken token) => ValueTask.CompletedTask;
    }
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var samples = new List<Sample>();
        foreach (bool running in new[] { false, true })
        foreach (string detection in new[] { "", "IN_MAP", "PAUSE", "BATTLE_STATUS_S", "LOGIN_CHECK", "TEMPLATE_MANJUU", "PAUSE_New" })
        foreach (double step in new[] { .25, 1.0, 2.0, 61.0, 181.0 })
        {
            var ops = new List<Op>();
            if (detection.Length > 0) ops.Add(new("observe", detection));
            for (int i = 0; i < 190; i++) { ops.Add(new("time", Seconds: step)); ops.Add(new("capture")); }
            samples.Add(new(running, ops.ToArray()));
        }
        foreach (int count in new[] { 5, 6, 11, 12, 15, 16, 30 })
        foreach (int distinct in new[] { 1, 2, 3, 16 })
        foreach (string reset in new[] { "none", "reset", "controls_reset", "remove" })
        {
            var ops = new List<Op>();
            for (int i = 0; i < count; i++) ops.Add(new("control", "button" + i % distinct));
            if (reset != "none") ops.Add(new(reset, "button0"));
            for (int i = 0; i < count; i++) ops.Add(new("control", "button" + i % distinct));
            samples.Add(new(true, ops.ToArray()));
        }
        var random = new Random(4601);
        for (int sample = 0; sample < 200; sample++)
        {
            var ops = new List<Op>();
            for (int i = 0; i < 300; i++)
            {
                int kind = random.Next(100);
                ops.Add(kind < 40 ? new("capture") : kind < 60 ? new("time", Seconds: random.Next(0, 181)) :
                    kind < 75 ? new("observe", kind % 2 == 0 ? "PAUSE" : "IN_MAP") :
                    kind < 93 ? new("control", "button" + kind % 3) : kind < 96 ? new("wait_reset") :
                    kind < 98 ? new("remove", "button0") : new("reset"));
            }
            samples.Add(new(sample % 2 == 0, ops.ToArray()));
        }
        string input = Path.Combine(artifacts, "watchdog-input.json"), output = Path.Combine(artifacts, "watchdog-native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, TaskQueue.Json));
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_device_watchdog_reference.py"), upstream, input, output], TimeSpan.FromMinutes(2));
        Check(process.ExitCode == 0, "Native device watchdog failed: " + process.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        Check(native["sources"]![DeviceWatchdog.Source.Path]!.GetValue<string>() == DeviceWatchdog.Source.Sha256, "Device source drift");
        int failures = 0;
        for (int sample = 0; sample < samples.Count; sample++)
        {
            var clock = new Clock(); var watchdog = new DeviceWatchdog(clock); var application = new Application(samples[sample].Running);
            var errors = new List<object>(); var removals = new List<int>(); int successful = 0;
            var operations = samples[sample].Operations;
            for (int index = 0; index < operations.Length; index++)
            {
                var op = operations[index];
                try
                {
                    switch (op.Kind)
                    {
                        case "time": clock.Advance(op.Seconds); break;
                        case "observe": watchdog.Observe(op.Name); break;
                        case "capture": await watchdog.BeforeCaptureAsync(application, default); break;
                        case "control": watchdog.BeforeControl(op.Name); successful++; break;
                        case "reset": watchdog.Reset(); break;
                        case "wait_reset": watchdog.ResetWait(); break;
                        case "controls_reset": watchdog.ResetControls(); break;
                        case "remove": removals.Add(watchdog.RemoveControl(op.Name)); break;
                    }
                }
                catch (Exception error) when (error is GameStuckException or GameNotRunningException or GameTooManyClicksException)
                {
                    string kind = error is GameStuckException ? "GameStuckError" : error is GameNotRunningException ? "GameNotRunningError" : "GameTooManyClickError";
                    errors.Add(new { index, kind }); failures++;
                }
            }
            var actual = JsonSerializer.SerializeToNode(new { errors, removals, successful, inspections = application.Inspections });
            Check(JsonNode.DeepEquals(actual, native["results"]![sample]), "Native watchdog trace differs at " + sample + ": " + actual);
            foreach (var record in watchdog.Evidence) RunReport.ValidateDeviceWatchdog(record);
        }
        await BoundariesAsync(python, upstream, artifacts);
        await SessionAsync(python, upstream, artifacts);
        Console.WriteLine($"Device watchdog: {samples.Count} actual native traces and {failures} matching failures; UI/map/drag transport, reset, cancellation and report boundaries passed. Synthetic device only.");
    }
}
