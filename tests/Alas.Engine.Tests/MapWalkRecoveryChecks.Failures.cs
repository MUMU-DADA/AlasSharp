using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapWalkRecoveryChecks
{
    private static async Task FailuresAsync(string upstream)
    {
        var mazeState = new CampaignState(new MapDefinition("E1", "SP -- -- -- --", [], [], []));
        mazeState.InitializeMapData(new()); mazeState.Fleet1Location = new(1, 1);
        var mazeConfig = new CampaignConfiguration { HasFleetStep = true, HasMaze = true, HasAmbush = false };
        mazeState.RefreshFleetPaths(mazeConfig); mazeState.Rounds.Initialize(mazeConfig);
        var mazeCamera = new ReplayCamera(mazeState, new("E1", "SP -- -- -- --", 4, 1, "raw", [1]));
        var mazeMove = new MapMovement(mazeState, mazeConfig, mazeCamera, () => new(mazeCamera, mazeState,
            _ => ValueTask.FromResult(true), mazeCamera.Clock, mazeCamera), recoverWalk: mazeCamera.RecoverAsync);
        bool redispatched = false;
        try { await mazeMove.MoveAsync(new(5, 1)); } catch (MapEnemyMovedException) { redispatched = true; }
        Check(redispatched && mazeState.Fleet1Location == new Cell(4, 1) && !mazeState.MovementInvalidated &&
            mazeMove.WalkRecoveries is [{ Phase: "redispatched", CompletedSteps: 3 }], "Recovery lost the committed maze-phase step or continued its obsolete route");
        RunReport.ValidateWalkRecovery(mazeMove.WalkRecoveries.Single());
        foreach (string failure in new[] { "missing", "transport", "cancel", "stale", "blocked" })
        {
            var state = new CampaignState(new MapDefinition("C1", "SP -- --", [], [], []));
            state.InitializeMapData(new()); state.Fleet1Location = new(1, 1);
            var config = new CampaignConfiguration { HasFleetStep = true, HasAmbush = false };
            state.RefreshFleetPaths(config);
            var camera = new ReplayCamera(state, new("C1", "SP -- --", 3, 1, "raw", [1]));
            async ValueTask Recover(CancellationToken token)
            {
                Check(state.Fleet1Location == new Cell(1, 1) && state.BattleCount == 0 && !state.MovementInvalidated,
                    "Step rejection committed arrival before recovery");
                if (failure == "transport") throw new IOException("Synthetic recovery failure");
                if (failure == "cancel") throw new OperationCanceledException();
                if (failure == "stale") return;
                await camera.RecoverAsync(token);
                state[new(2, 1)].IsLand = true; state.RefreshFleetPaths(config);
            }
            var move = new MapMovement(state, config, camera, () => new(camera, state,
                _ => ValueTask.FromResult(true), camera.Clock, camera), recoverWalk: failure == "missing" ? null : Recover);
            bool rejected = false;
            try { await move.MoveAsync(new(3, 1)); }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException or NotSupportedException or CampaignScriptException)
            { rejected = true; }
            Check(rejected && state.MovementInvalidated && camera.Invalidated && camera.Taps.Count == 1 && state.Fleet1Location == new Cell(1, 1),
                "Recovery failure continued or committed movement: " + failure);
            foreach (var evidence in move.WalkRecoveries) RunReport.ValidateWalkRecovery(evidence);
        }
        var assets = new AssetFiles(Path.Combine(upstream, "assets"));
        foreach (bool enabled in new[] { false, true })
        foreach (bool combat in new[] { false, true })
        {
            var ui = new MessageUi(combat ? "combat" : "disappear");
            var walk = new MapWalkStep(ui, ui, new MessageVision("disappear"), assets, () => ui.Frame);
            var probe = new MapEncounterProbe(ui, false, walkStep: enabled ? walk : null);
            Check(await probe.InspectAsync(1, default) == (combat ? MapEncounterKind.Combat : enabled ? MapEncounterKind.WalkOutOfStep : MapEncounterKind.None),
                "Step message bypassed configuration or preempted combat");
        }
        foreach (string scenario in new[] { "disappear", "stale", "timeout", "cancel", "wrong_cv", "wrong_frame" })
        {
            var ui = new MessageUi(scenario); var vision = new MessageVision(scenario);
            var walk = new MapWalkStep(ui, ui, vision, assets, () => ui.Frame);
            bool failed = false;
            try
            {
                bool found = await walk.ObserveAsync(scenario == "wrong_frame" ? 2 : 1, default);
                Check(found, "Walk message was not observed");
                await walk.ClearAsync(default);
            }
            catch (Exception error) when (error is InvalidDataException or TimeoutException or OperationCanceledException) { failed = true; }
            Check(failed == (scenario != "disappear"), "Message failure boundary differed: " + scenario);
        }
        var interruption = new MapArrivalResult(MapArrivalOutcome.WalkOutOfStep, 2, 1, MapEncounterKind.WalkOutOfStep);
        var valid = new WalkRecoveryEvidence(1, new(1, 1), new(3, 1), "completed", interruption, [new(2, 1), new(3, 1)], 2, 3);
        foreach (var corrupt in new[] { valid with { Steps = default }, valid with { Phase = "unknown" }, valid with { RecoveredFrame = 2 },
            valid with { CompletedSteps = 1 }, valid with { Interruption = interruption with { FreshFrames = 0 } }, valid with { Fleet = 3 } })
        {
            bool rejected = false;
            try { RunReport.ValidateWalkRecovery(corrupt); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Corrupt walk evidence passed validation");
        }
    }

    private sealed class MessageUi(string scenario) : AppearanceProbe(GameServer.Cn, null), IMapUiObservations
    {
        public ScreenFrame Frame { get; private set; } = new(1, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
            => ValueTask.FromResult(scenario == "combat" && asset == UiAssets.Combat.BATTLE_PREPARATION);
        public override ValueTask ScreenshotAsync(CancellationToken token)
        {
            if (scenario == "cancel") throw new OperationCanceledException();
            Time.Advance(5);
            if (scenario != "stale") Frame = Frame with { Sequence = Frame.Sequence + 1 };
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> InfoBarCountAsync(CancellationToken token)
            => ValueTask.FromResult(scenario == "disappear" && Frame.Sequence > 1 ? 0 : 1);
        public ValueTask<bool> HasStageEntranceAsync(CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class MessageVision(string scenario) : IVision
    {
        public ValueTask<TemplateObservation> MatchAsync(ScreenFrame frame, TemplateRequest request, CancellationToken token = default)
        {
            Check(request.Similarity == .85 && request.Transform == new PixelTransform(64, .75), "Walk message transform drifted");
            return ValueTask.FromResult(new TemplateObservation(scenario == "wrong_cv" ? 0 : frame.Sequence, true, 1, new(0, 0)));
        }
        public ValueTask<MeanColorObservation> MeanColorAsync(ScreenFrame frame, PixelArea area, CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ScreenFrame frame, ColorBandRequest request, CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask<OcrObservation> ReadTextAsync(ScreenFrame frame, OcrRequest request, CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
