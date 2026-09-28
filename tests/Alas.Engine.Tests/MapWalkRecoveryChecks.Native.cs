using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class MapWalkRecoveryChecks
{
    private sealed record Sample(string Target, string Tiles, int Step, int Fleet, string Kind, int[] Reject, bool BeforeReject = false);

    public static async Task NativeAsync(string python, string upstream, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var cases = new List<Sample>();
        foreach (int length in new[] { 3, 5, 8 })
        foreach (int step in new[] { 2, 3, 4 })
        foreach (int fleet in new[] { 1, 2 })
        foreach (string kind in new[] { "raw", "mystery", "enemy", "siren", "boss" })
        foreach (int[] reject in new int[][] { [], [1], [1, 2], [2] })
            cases.Add(new(new Cell(length, 1).ToString(), "SP " + string.Join(' ', Enumerable.Repeat("--", length - 2)) +
                " " + (kind == "raw" ? "--" : kind == "mystery" ? "MM" : kind == "boss" ? "MB" : "ME"), step, fleet, kind, reject));
        foreach (string kind in new[] { "mystery", "enemy", "siren", "boss" })
            cases.Add(new("C1", "SP -- " + (kind == "mystery" ? "MM" : kind == "boss" ? "MB" : "ME"), 3, 1, kind, [1], true));
        string input = Path.Combine(artifacts, "walk-input.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(cases, TaskQueue.Json));
        var assets = new AssetFiles(Path.Combine(upstream, "assets"));
        await using var vision = new PureVisionWorker(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        int fixtures = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server + ".json");
            var process = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_walk_recovery_reference.py"), upstream,
                    server.ToString().ToLowerInvariant(), input, output], TimeSpan.FromMinutes(2));
            Check(process.ExitCode == 0, "Native walk replay failed: " + process.Error);
            var reference = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var source in new[] { CampaignState.InitializationSource, CampaignState.SpawnSource, MapWalkStep.Source, MapAmbushInfo.Source })
                Check(reference["sources"]![source.Path]!.GetValue<string>() == source.Sha256 &&
                    Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
                    "Walk recovery source drift: " + source.Path);
            if (server == GameServer.Cn)
                for (int i = 0; i < cases.Count; i++) await CompareAsync(cases[i], reference["results"]![i]!);
            var info = new Info();
            foreach (var fixture in reference["fixtures"]!.AsArray())
            {
                var frame = new ScreenFrame(++fixtures, DateTimeOffset.UnixEpoch,
                    await File.ReadAllBytesAsync(Path.Combine(artifacts, fixture!["image"]!.GetValue<string>())));
                var ui = new AppearanceProbe(server, null);
                var walk = new MapWalkStep(ui, info, vision, assets, () => frame);
                info.Count = 1;
                Check(await walk.ObserveAsync(frame.Sequence, default) == fixture["matched"]!.GetValue<bool>(), "Walk message differs from native CV");
                var observation = await vision.MatchAsync(frame, new(await assets.ReadAsync(UiAssets.Template.TEMPLATE_MAP_WALK_OUT_OF_STEP.For(server)),
                    UiAssets.Handler.INFO_BAR_DETECT.For(server).Area!.Value.Area, .85, Transform: new(64, .75)));
                Check(Math.Abs(observation.Similarity - fixture["score"]!.GetValue<double>()) < .00001, "Walk message preprocessing changed numeric CV output");
                info.Count = 0;
                Check(!await walk.ObserveAsync(frame.Sequence, default), "Walk message ignored missing info-bar gate");
            }
        }
        await ReportsAsync(python, upstream, artifacts);
        await MapViewChecks.WalkRecoveryCameraAsync(upstream);
        await FailuresAsync(upstream);
        Console.WriteLine($"Walk recovery: {cases.Count} actual native goto/_goto routes and counters, {fixtures} original-asset CV fixtures across four servers passed; synthetic device only.");
    }

    private static async Task CompareAsync(Sample sample, JsonNode native)
    {
        var state = new CampaignState(new MapDefinition(sample.Target, sample.Tiles, [], [], []));
        state.InitializeMapData(new()); state.FleetIndex = sample.Fleet;
        if (sample.Fleet == 1) state.Fleet1Location = new(1, 1); else state.Fleet2Location = new(1, 1);
        var destination = Cell.Parse(sample.Target);
        var target = state[destination];
        target.IsEnemy = sample.Kind == "enemy";
        target.IsSiren = sample.Kind == "siren";
        target.IsBoss = sample.Kind == "boss";
        target.IsMystery = sample.Kind == "mystery";
        var config = new CampaignConfiguration { HasAmbush = false, HasFleetStep = true, Fleet1Step = sample.Step, Fleet2Step = sample.Step,
            Fleet2 = sample.Fleet == 2 ? 2 : 0, EmotionMode = CampaignEmotionMode.Ignore };
        state.RefreshFleetPaths(config);
        var camera = new ReplayCamera(state, sample);
        var move = new MapMovement(state, config, camera, () => new(camera, state, _ => ValueTask.FromResult(true), camera.Clock, camera, camera),
            recoverWalk: camera.RecoverAsync);
        bool error = false;
        try
        {
            var route = state.Paths.FindRoute(destination, sample.Step, false);
            for (int i = 0; i < route.Waypoints.Count; i++)
            {
                bool last = i == route.Waypoints.Count - 1;
                var result = !last || sample.Kind == "raw" ? await move.VisitAsync(route.Waypoints[i]) :
                    sample.Kind == "mystery" ? await move.CollectMysteryAsync(route.Waypoints[i]) :
                    await move.FightAsync(route.Waypoints[i], expectation: sample.Kind == "boss" ? MapCombatExpectation.Boss :
                        sample.Kind == "siren" ? MapCombatExpectation.Siren : MapCombatExpectation.Enemy);
                Check(result.Outcome == MapMoveOutcome.Committed, "Native successful walk was not committed");
            }
        }
        catch (MapWalkException) { error = true; }
        string label = JsonSerializer.Serialize(sample, TaskQueue.Json);
        Check(error == native["error"]!.GetValue<bool>() && camera.Recoveries == native["recoveries"]!.GetValue<int>(), "Recovery count/error differs: " + label);
        Check(camera.Taps.Select(c => c.ToString()).SequenceEqual(native["taps"]!.AsArray().Select(n => n!.GetValue<string>())), "Recovery taps differ: " + label);
        Check((sample.Fleet == 1 ? state.Fleet1Location : state.Fleet2Location).ToString() == native["fleet"]!.GetValue<string>() &&
            state.BattleCount == native["battle"]!.GetValue<int>() && state.SirenCount == native["siren"]!.GetValue<int>() &&
            state.MysteryCount == native["mystery"]!.GetValue<int>() && state.FleetAmmo == native["ammo"]!.GetValue<int>(), "Recovery accounting differs: " + label);
        foreach (var evidence in move.WalkRecoveries) RunReport.ValidateWalkRecovery(evidence);
        Check(state.MovementInvalidated == error && camera.Invalidated == error, "Recovery left the wrong usability state: " + label);
    }

    private sealed class Info : IMapUiObservations
    {
        public int Count { get; set; }
        public ValueTask<int> InfoBarCountAsync(CancellationToken token) => ValueTask.FromResult(Count);
        public ValueTask<bool> HasStageEntranceAsync(CancellationToken token) => throw new InvalidOperationException("Unexpected stage recognition");
    }

    private sealed class ReplayCamera(CampaignState state, Sample sample) : IMapArrivalCamera, IMapEncounterProbe, IMapEncounterHandler
    {
        public Clock Clock { get; } = new();
        public long FrameSequence { get; private set; } = 1;
        public List<Cell> Taps { get; } = [];
        public int Recoveries { get; private set; }
        public bool Invalidated { get; private set; }
        public bool Suspended { get; private set; }
        private bool _interaction;
        private bool _delivered;
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Check(!Invalidated && !Suspended, "Walk used unavailable camera"); return ValueTask.CompletedTask; }
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Taps.Add(destination); _interaction = false; return ValueTask.CompletedTask; }
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); FrameSequence++; Clock.Advance(.25); return ValueTask.CompletedTask; }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default) => ValueTask.FromResult(new FleetMarker(true, true));
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default) => ValueTask.FromResult(new FleetMarker(true, true));
        public ValueTask RelocalizeAsync(CancellationToken token = default)
        { Suspended = false; return RefreshImageAsync(token); }
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default) => ValueTask.CompletedTask;
        public void Suspend() => Suspended = true;
        public void Invalidate() => Invalidated = true;
        public ValueTask RecoverAsync(CancellationToken token)
        { Recoveries++; return RelocalizeAsync(token); }
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
        {
            bool rejected = sample.Reject.Contains(Taps.Count);
            if (rejected && (!sample.BeforeReject || _delivered)) return ValueTask.FromResult(MapEncounterKind.WalkOutOfStep);
            if (_interaction || _delivered || Taps[^1].ToString() != sample.Target || sample.Kind == "raw") return ValueTask.FromResult(MapEncounterKind.None);
            _interaction = true;
            _delivered = true;
            return ValueTask.FromResult(sample.Kind == "mystery" ? MapEncounterKind.ItemPopup : MapEncounterKind.Combat);
        }
        public ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
        {
            if (encounter == MapEncounterKind.ItemPopup) return ValueTask.FromResult(new MapEncounterHandling(MapEncounterContinuation.InMap));
            Check(state.EncounterExpectedBoss == (sample.Kind == "boss"), "Recovery lost boss combat context");
            return ValueTask.FromResult(new MapEncounterHandling(MapEncounterContinuation.InMap,
                new CombatFlowResult(CombatReturn.InMap, new(CombatRank.S, CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_S.Id), true, false, 1)));
        }
    }
}
