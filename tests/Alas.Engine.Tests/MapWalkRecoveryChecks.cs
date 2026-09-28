using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapWalkRecoveryChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        var state = new CampaignState(new MapDefinition("C1", "SP -- --", [], [], []));
        state.InitializeMapData(new());
        state.Fleet1Location = new(1, 1);
        var configuration = new CampaignConfiguration { HasFleetStep = true, Fleet1Step = 1, EmotionMode = CampaignEmotionMode.Ignore };
        state.RefreshFleetPaths(configuration);
        var camera = new Camera();
        var probe = new Probe();
        var movement = new MapMovement(state, configuration, camera,
            () => new(camera, state, _ => ValueTask.FromResult(true), camera.Clock, probe),
            recoverWalk: camera.RecoverAsync);
        var result = await movement.MoveAsync(new(3, 1));
        Check(result.Outcome == MapMoveOutcome.Committed && state.Fleet1Location == new Cell(3, 1),
            "Walk-step recovery did not commit the final position");
        Check(camera.Taps == 3 && camera.Recoveries == 1 && !camera.Suspended,
            "Walk-step recovery did not retry the route one step at a time");
        Check(movement.WalkRecoveries is [{ Phase: "completed", CompletedSteps: 2 }],
            "Walk-step recovery evidence did not retain the completed route");
        RunReport.ValidateWalkRecovery(movement.WalkRecoveries.Single());
        Check(state.BattleCount == 0 && state.MovementInvalidated == false,
            "Walk-step recovery changed battle state or invalidated a successful map");
        Console.WriteLine("Walk-step recovery: actual MapMovement/MapArrivalCheck route retry, confirmed positions and evidence passed.");
    }

    private sealed class Probe : IMapEncounterProbe
    {
        private bool _interrupted;
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token)
            => ValueTask.CompletedTask;
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
        {
            if (_interrupted) return ValueTask.FromResult(MapEncounterKind.None);
            _interrupted = true;
            return ValueTask.FromResult(MapEncounterKind.WalkOutOfStep);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += (long)(seconds * TimestampFrequency);
    }

    private sealed class Camera : IMapArrivalCamera
    {
        public readonly Clock Clock = new();
        public long FrameSequence { get; private set; } = 1;
        public int Taps { get; private set; }
        public int Recoveries { get; private set; }
        public bool Suspended { get; private set; }
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        { Taps++; return ValueTask.CompletedTask; }
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        { FrameSequence++; Clock.Advance(.6); return ValueTask.CompletedTask; }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
            => ValueTask.FromResult(new FleetMarker(true, true));
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default)
            => ValueTask.FromResult(new FleetMarker(true, true));
        public ValueTask RelocalizeAsync(CancellationToken token = default)
        { FrameSequence++; Clock.Advance(.6); Suspended = false; return ValueTask.CompletedTask; }
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default) => ValueTask.CompletedTask;
        public void Suspend() => Suspended = true;
        public void Invalidate() => throw new InvalidOperationException("Successful recovery invalidated the camera");
        public ValueTask RecoverAsync(CancellationToken token)
        { FrameSequence++; Clock.Advance(.6); Recoveries++; Suspended = false; return ValueTask.CompletedTask; }
    }
}
