using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class MapWalkInterruptionChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed record Sample(string Method, bool Locked, string[][] Scenes, int RetirementSeconds = 0);
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var samples = new List<Sample>();
        foreach (bool locked in new[] { false, true })
        for (int mask = 0; mask < 64; mask++)
        {
            string[] names = ["$map", "$loading", "BATTLE_PREPARATION", "BATTLE_PREPARATION_WITH_OVERLAY", "AUTOMATION_CONFIRM_CHECK", "AUTOMATION_CONFIRM"];
            samples.Add(new("appearance", locked, [names.Where((_, i) => (mask & 1 << i) != 0).ToArray()]));
        }
        foreach (bool locked in new[] { false, true })
        foreach (int delay in new[] { 0, 1, 5, 9 })
        foreach (string end in new[] { "BATTLE_PREPARATION", "$loading", "BATTLE_PREPARATION_WITH_OVERLAY" })
        foreach (bool emotion in new[] { false, true })
        {
            var scenes = Enumerable.Range(0, delay).Select(_ => new[] { "MAP_OFFENSIVE" }).ToList();
            if (emotion) scenes.Add(["$emotion"]);
            scenes.Add(["$retire"]); scenes.Add([]);
            scenes.Add(end == "$loading" && !locked ? ["BATTLE_PREPARATION"] :
                end == "BATTLE_PREPARATION_WITH_OVERLAY" ? [end, "AUTOMATION_CONFIRM_CHECK", "AUTOMATION_CONFIRM"] : [end]);
            samples.Add(new("offensive", locked, scenes.ToArray()));
        }
        foreach (int wait in new[] { 0, 78, 79 })
        foreach (int emotion in new[] { 1, 4, 8 })
        {
            var scenes = Enumerable.Range(0, wait + 1).Select(_ => new[] { "$map" }).ToList();
            for (int i = 0; i < emotion; i++) scenes.Add(["$emotion"]);
            scenes.Add(["$map", "$marker"]);
            samples.Add(new("walk", true, scenes.ToArray()));
        }
        foreach (int delay in new[] { 0, 5, 9 })
        foreach (int seconds in new[] { 0, 30, 60 })
        foreach (string end in new[] { "$loading", "BATTLE_PREPARATION" })
        {
            List<string[]> scenes = [["$map"], ["$open"], ["$retire"]];
            for (int i = 0; i < delay; i++) scenes.Add(["MAP_OFFENSIVE"]);
            scenes.Add(["$emotion"]); scenes.Add([end]);
            samples.Add(new("walk", true, scenes.ToArray(), seconds));
        }
        samples.Add(new("walk", false, [["$map"], ["$map", "$marker"]]));
        string input = Path.Combine(artifacts, "input.json"), output = Path.Combine(artifacts, "native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, TaskQueue.Json));
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_walk_interruptions_reference.py"), upstream, input, output], TimeSpan.FromMinutes(1));
        Check(process.ExitCode == 0, "Native interruption reference failed: " + process.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { MapWalkInterruptions.Source, CombatAppearance.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Interruption source drift: " + source.Path);
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i]; var ui = new Replay(sample); var expected = native["results"]![i]!;
            var interruptions = new MapWalkInterruptions(ui, () => ui.Sequence, ui, ui.Combat);
            JsonNode? result = null;
            if (sample.Method == "appearance") result = JsonValue.Create(await ui.Combat.AppearsAsync(default));
            else if (sample.Method == "offensive") await interruptions.OffensiveAsync(default);
            else
            {
                var arrival = ui.Arrival(interruptions);
                var arrived = await arrival.TapAndCheckAsync(new(2, 1));
                result = JsonValue.Create(arrived.Outcome == MapArrivalOutcome.MarkerConfirmed ? "arrived" :
                    arrived is { Outcome: MapArrivalOutcome.MapInterrupted, Encounter: MapEncounterKind.Combat } ? "combat" : "unexpected");
                Check(arrived.RetryTaps == 0 && arrived.WalkTimeouts.IsEmpty, "Interruption did not reset the walk timer");
                foreach (var evidence in interruptions.Evidence) RunReport.ValidateWalkInterruption(evidence);
            }
            Check(JsonNode.DeepEquals(result, expected["result"]) && ui.Frames == expected["frames"]!.GetValue<int>() &&
                ui.Trace.SequenceEqual(expected["trace"]!.AsArray().Select(x => x!.GetValue<string>())),
                $"Native {i}/{sample.Method} differs: frames={ui.Frames}/{expected["frames"]}, result={result}/{expected["result"]}; " +
                $"C#={string.Join(',', ui.Trace)} native={expected["trace"]}");
        }
        await FailuresAsync();
        await MapViewChecks.SharedInterruptionImageAsync(upstream);
        await SessionAsync(python, upstream, artifacts);
        Console.WriteLine($"Fleet-lock interruptions: {samples.Count} native combat_appear/map_offensive/_goto traces, failures and real-session artifacts passed; synthetic I/O only.");
    }

    private sealed class Replay(Sample sample) : AppearanceProbe(GameServer.Cn, null), ICampaignInterruptions, IMapArrivalCamera
    {
        public int Frames { get; private set; }
        public long Sequence => Failure == "stale" ? 1 : Frames + 1;
        public long FrameSequence => Sequence;
        public List<string> Trace { get; } = [];
        public string? Failure { get; set; }
        public bool Invalidated { get; private set; }
        public bool DelayRetirement { get; init; }
        public CancellationTokenSource? Cancel { get; init; }
        private string[] Scene => sample.Scenes[Math.Min(Frames, sample.Scenes.Length - 1)];
        public CombatAppearance Combat => new(this, sample.Locked, LoadingAsync);
        public MapArrivalCheck Arrival(MapWalkInterruptions interruptions)
        {
            var state = new CampaignState(new MapDefinition("B1", "SP --", [], [], []));
            state.InitializeMapData(new()); state.Fleet1Location = new(1, 1);
            return new(this, state, token => AppearsAsync(UiAssets.Handler.IN_MAP, token: token), Clock,
                new MapEncounterProbe(this, false, combat: Combat), walkInterruptions: sample.Locked ? interruptions.HandleAsync : null);
        }
        public override ValueTask ScreenshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure == "capture") throw new IOException("Synthetic interruption screenshot failure");
            Frames++; Time.Advance(.25);
            if (Frames == 10) Cancel?.Cancel();
            if (Frames > 700) throw new TimeoutException("Synthetic interruption stalled");
            return ValueTask.CompletedTask;
        }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (interval > 0 && !Timer(asset, interval, true).Reached()) return ValueTask.FromResult(false);
            if (asset == UiAssets.Combat.BATTLE_PREPARATION) Check(offset == ButtonOffset.Expand(30, 20), "Preparation offset drift");
            if (asset == UiAssets.Combat.BATTLE_PREPARATION_WITH_OVERLAY || asset == UiAssets.Combat.AUTOMATION_CONFIRM_CHECK)
                Check(threshold == 30, "Overlay detection threshold drift");
            if (asset == UiAssets.Combat.AUTOMATION_CONFIRM) Check(offset == ButtonOffset.Expand(20, 20) && threshold == 10, "Confirmation detection drift");
            bool found = Scene.Contains(asset == UiAssets.Handler.IN_MAP ? "$map" : asset.Name);
            if (found && interval > 0) Timer(asset).Reset();
            return ValueTask.FromResult(found);
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (Failure == "click") throw new IOException("Synthetic offensive click failure"); Trace.Add($"click:{asset.Name}:{Frames}"); return ValueTask.CompletedTask; }
        public override void ResetInterval(AssetRule asset, double seconds = 3)
        { Trace.Add($"reset:{asset.Name}:{Frames}"); base.ResetInterval(asset, seconds); }
        public void Configure(RetirementOptions retirement, CampaignEmotionMode emotion) => throw new NotSupportedException();
        public async ValueTask<bool> RetirementAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Trace.Add($"retirement:{Frames}");
            if (Failure == "retirement") throw new IOException("Synthetic retirement failure");
            bool found = Scene.Contains("$retire"); if (found) Time.Advance(sample.RetirementSeconds);
            if (found && DelayRetirement) await Task.Delay(80, token);
            return found;
        }
        public ValueTask<bool> LowEmotionAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Trace.Add($"emotion:{Frames}"); return ValueTask.FromResult(Scene.Contains("$emotion")); }
        private ValueTask<bool> LoadingAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Trace.Add($"loading:{Frames}"); return ValueTask.FromResult(Scene.Contains("$loading")); }
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Trace.Add($"click:grid:{Frames}"); return ValueTask.CompletedTask; }
        public ValueTask RefreshImageAsync(CancellationToken token = default) => ScreenshotAsync(token);
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
            => ValueTask.FromResult(new FleetMarker(Scene.Contains("$marker"), Scene.Contains("$marker")));
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask RelocalizeAsync(CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default) => throw new NotSupportedException();
        public void Suspend() { }
        public void Invalidate() => Invalidated = true;
    }
}
