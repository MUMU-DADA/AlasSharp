using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class CampaignFleetSetupChecks
{
    public static async Task RunAsync()
    {
        var probe = new Probe();
        var result = await probe.Setup.ApplyAsync(new(1, 2, 0));
        Check(result == new FleetSetupResult(false, true, true, 0) &&
            probe.State(FleetSlot.First).Selected.Contains(1) &&
            probe.State(FleetSlot.Second).Selected.Contains(2) &&
            !probe.State(FleetSlot.Submarine).InUse &&
            probe.Trace.IndexOf("FLEET_2_CLEAR") < probe.Trace.IndexOf("FLEET_1_CHOOSE") &&
            probe.Trace.IndexOf("FLEET_1_CHOOSE") < probe.Trace.IndexOf("FLEET_2_CHOOSE"),
            "Ordinary fleet setup did not preserve upstream clear and selection order");

        probe = new Probe();
        result = await probe.Setup.ApplyAsync(new(1, 0, 1));
        Check(result == new FleetSetupResult(false, true, true, 1) &&
            probe.State(FleetSlot.Submarine).Selected.Contains(1) &&
            !probe.State(FleetSlot.Second).InUse &&
            probe.Trace.IndexOf("SUBMARINE_CHOOSE") < probe.Trace.IndexOf("FLEET_1_CHOOSE"),
            "Submarine fleet was not prepared before the surface fleet");

        probe = new Probe();
        probe.State(FleetSlot.First).Hard = true;
        probe.State(FleetSlot.First).Satisfied = true;
        result = await probe.Setup.ApplyAsync(new(1, 2, 0));
        Check(result == new FleetSetupResult(true, false, true, 0) &&
            !probe.Trace.Contains("FLEET_1_CHOOSE") && !probe.Trace.Contains("FLEET_2_CHOOSE"),
            "Satisfied hard mode changed surface fleets");

        probe = new Probe();
        probe.State(FleetSlot.First).Hard = true;
        await Throws<CampaignHardFleetUnsatisfiedException>(() => probe.Setup.ApplyAsync(new(1, 2, 0)).AsTask());
        Check(probe.Trace.Count == 0, "Unsatisfied hard fleet restriction caused an action");

        probe = new Probe();
        probe.State(FleetSlot.Submarine).Allowed = false;
        result = await probe.Setup.ApplyAsync(new(1, 0, 1));
        Check(result == new FleetSetupResult(false, true, false, 0) && !probe.Trace.Contains("SUBMARINE_CHOOSE"),
            "Unavailable submarine controls were clicked");

        await Throws<ArgumentOutOfRangeException>(() => new Probe().Setup.ApplyAsync(new(0, 2, 0)).AsTask());
        await Throws<ArgumentOutOfRangeException>(() => new Probe().Setup.ApplyAsync(new(1, 7, 0)).AsTask());
        await Throws<ArgumentOutOfRangeException>(() => new Probe().Setup.ApplyAsync(new(1, 0, 3)).AsTask());
        foreach (bool hard in new[] { false, true })
        foreach (bool clear in new[] { false, true })
        foreach (bool available in new[] { false, true })
        {
            probe = new Probe();
            probe.State(FleetSlot.First).Hard = hard; probe.State(FleetSlot.First).Satisfied = hard;
            probe.State(FleetSlot.Submarine).Allowed = available;
            result = await probe.Setup.ApplyAsync(new(1, 0, 1, SubmarineMode.BossOnly, clear));
            Check(result.SubmarineStandby == (!available ? null : clear ? "confirmed" : "unavailable_clear_mode"),
                "Fleet setup lost standby confirmation or native locked-setting assumption");
            Check(probe.Trace.Contains("standby") == (available && clear), "Standby bypassed effective submarine availability");
            if (!hard && available && clear) Check(probe.Trace.IndexOf("standby") > probe.Trace.IndexOf("FLEET_1_CHOOSE"),
                "Standby opened before fleet configuration finished");
        }
        Console.WriteLine("Fleet setup: normal, second fleet, submarine, hard restrictions and invalid plans passed offline.");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class SlotState(FleetSlot slot)
    {
        public FleetSlot Slot { get; } = slot;
        public bool Allowed { get; set; } = true;
        public bool Hard { get; set; }
        public bool Satisfied { get; set; }
        public bool InUse { get; set; }
        public bool Open { get; set; }
        public HashSet<int> Selected { get; } = [];
    }

    private sealed class Probe : AppearanceProbe, IPopupHandler, IImagePatchVision
    {
        private long _sequence = 1;
        private readonly SlotState[] _states = [new(FleetSlot.First), new(FleetSlot.Second), new(FleetSlot.Submarine)];
        public Probe() : base(GameServer.Cn, null) { }
        public CampaignFleetSetup Setup => new(this, this, () => new ScreenFrame(_sequence, DateTimeOffset.UnixEpoch,
            ReadOnlyMemory<byte>.Empty), this);
        public List<string> Trace { get; } = [];
        public SlotState State(FleetSlot slot) => _states.Single(state => state.Slot.Clear == slot.Clear);
        public override Rectangle ButtonArea(AssetRule asset)
            => asset.For(Server).ClickArea ?? throw new InvalidDataException("Missing test button");
        public override void LoadOffset(AssetRule target, AssetRule reference)
        { Check(_states.Any(state => state.Slot.Clear == reference), "Fleet offset source changed"); }
        public override ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); _sequence++; Time.Advance(.5); return ValueTask.CompletedTask; }
        public override ValueTask DelayAsync(TimeSpan delay, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Time.Advance(delay.TotalSeconds); return ValueTask.CompletedTask; }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (asset == UiAssets.Map.FLEET_PREPARATION_CHECK)
            {
                Check(offset == ButtonOffset.Expand(20, 80), "Sidebar anchor offset changed");
                Trace.Add("standby");
                return ValueTask.FromResult(true);
            }
            Check(offset == ButtonOffset.Bounds(-20, -80, 20, 5), "Fleet asset search offset changed");
            var state = _states.FirstOrDefault(s => s.Slot.Clear == asset || s.Slot.Advice == asset);
            return ValueTask.FromResult(state is not null && (state.Slot.Clear == asset ? state.Allowed : state.Hard));
        }
        public override ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var inUse = _states.FirstOrDefault(state => ButtonArea(state.Slot.InUse) == area);
            if (inUse is not null) return ValueTask.FromResult(new MeanColorObservation(_sequence, 71, 70, 63));
            var opened = _states.Single(state => state.Open);
            var bar = ButtonArea(opened.Slot.Bar);
            int index = (area.Top - bar.Top) / 42 + 1;
            Check(area.Left == bar.Left && area.Right == bar.Right && index is >= 1 and <= 6,
                "Fleet row observation left the active dropdown");
            var color = opened.Selected.Contains(index) ? new Rgb(200, 100, 0) : new Rgb(100, 100, 100);
            return ValueTask.FromResult(new MeanColorObservation(_sequence, color.R, color.G, color.B));
        }
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            double value;
            if (request.Color is { } color && color == new PixelColor(99, 235, 255)) value = request.Area.Y == 377 ? 51 : 0;
            else if (request.Color == new PixelColor(255, 255, 255)) value = 101;
            else if (request.Color == new PixelColor(156, 255, 82)) value = request.Area == ButtonArea(UiAssets.Handler.AUTO_SEARCH_SET_SUB_STANDBY).Area ? 21 : 0;
            else if (request.Measure == PatchMeasure.RowPeakCount)
            {
                var state = _states.Single(s => ButtonArea(s.Slot.HardSatisfied).Area == request.Area);
                Check(request.Color == new PixelColor(249, 199, 0) && request.PeakHeight == 180 &&
                    request.PeakDistance == 5, "Hard fleet line measurement changed");
                value = state.Satisfied ? 1 : 0;
            }
            else if (request.Measure == PatchMeasure.StandardDeviation)
                value = _states.Single(s => ButtonArea(s.Slot.InUse).Area == request.Area).InUse ? 52 : 6;
            else
            {
                var state = _states.Single(s => ButtonArea(s.Slot.Bar).Top == request.Area.Y);
                value = state.Open ? ButtonArea(state.Slot.Bar).Height : 0;
            }
            return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence, value));
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Trace.Add(asset.Name);
            var state = _states.Single(s => s.Slot.Choose == asset || s.Slot.Clear == asset);
            if (state.Slot.Choose == asset) state.Open = !state.Open;
            else { state.InUse = false; state.Selected.Clear(); }
            return ValueTask.CompletedTask;
        }
        public override ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var state = _states.Single(s => s.Open);
            var bar = ButtonArea(state.Slot.Bar);
            int index = (area.Top - bar.Top) / 42 + 1;
            Trace.Add(state.Slot.Bar.Name + "_" + index);
            state.Selected.Add(index);
            state.InUse = true;
            state.Open = false;
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> ConfirmAsync(CancellationToken token) => ValueTask.FromResult(false);
    }
}
