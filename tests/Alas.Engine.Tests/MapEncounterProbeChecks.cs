using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class MapEncounterProbeChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        Check(new CampaignConfiguration().HasAmbush,
            "Native MAP_HAS_AMBUSH default was not preserved");
        var ui = new Ui();
        ui.Appearing.Add(UiAssets.Combat.BATTLE_PREPARATION.Id);
        ui.Appearing.Add(UiAssets.Combat.GET_ITEMS_1.Id);
        Check(await new MapEncounterProbe(ui, false).InspectAsync(1, default) == MapEncounterKind.Combat,
            "Combat preparation did not take priority over an item popup");

        ui.Appearing.Clear();
        ui.AirRed = ui.AmbushRed = 100;
        var probe = new MapEncounterProbe(ui, true);
        await probe.InitializeAsync(1, default);
        ui.Sequence = 2; ui.AirRed = 160;
        Check(await probe.InspectAsync(2, default) == MapEncounterKind.AirRaid,
            "Air raid red overlay was not classified before ambush");
        ui.Sequence = 3; ui.AirRed = 100; ui.AmbushRed = 170;
        Check(await probe.InspectAsync(3, default) == MapEncounterKind.Ambush,
            "Ambush red overlay was not classified");
        ui.Sequence = 4; ui.AmbushRed = 100;
        ui.Appearing.Add(UiAssets.Handler.MAP_AMBUSH_EVADE.Id);
        Check(await probe.InspectAsync(4, default) == MapEncounterKind.Ambush,
            "Ambush evade button was not classified after its overlay vanished");

        ui.Appearing.Clear();
        ui.Appearing.Add(UiAssets.Combat.GET_ITEMS_1.Id);
        Check(await probe.InspectAsync(4, default) == MapEncounterKind.ItemPopup,
            "Item popup was mistaken for an ordinary arrival");
        ui.Appearing.Clear();
        Check(await probe.InspectAsync(4, default) == MapEncounterKind.None,
            "Empty map frame produced an encounter");
        ui.AirRed = 150;
        Check(await probe.InspectAsync(4, default) == MapEncounterKind.None,
            "Red overlay below the upstream threshold was classified as an air raid");

        ui.Sequence = 5;
        bool rejected = false;
        try { await probe.InspectAsync(4, default); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Encounter probe accepted a color measurement from another frame");
        ui.AirRed = 247;
        var degenerate = new MapEncounterProbe(ui, true);
        await degenerate.InitializeAsync(5, default);
        rejected = false;
        try { await degenerate.InspectAsync(5, default); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Degenerate red overlay denominator was accepted");

        ui.Sequence = 10; ui.AirRed = ui.AmbushRed = 100;
        var airProbe = new MapEncounterProbe(ui, true);
        await airProbe.InitializeAsync(10, default);
        ui.AirRed = 160;
        foreach (var red in new double[] { 160, 160, 100, 100, 100 }) ui.ScreenshotReds.Enqueue(red);
        int before = ui.Screenshots;
        var airHandler = new MapAirRaidHandler(ui, airProbe, () => ui.Sequence);
        Check((await airHandler.HandleAsync(MapEncounterKind.AirRaid, default)).Continuation == MapEncounterContinuation.InMap &&
              ui.Screenshots - before == 5,
            "Air raid did not wait for a stable disappearance using C# screenshots");
        before = ui.Screenshots;
        Check((await airHandler.HandleAsync(MapEncounterKind.Combat, default)).Continuation == MapEncounterContinuation.Unhandled &&
              ui.Screenshots == before,
            "Unimplemented combat was silently accepted by the air raid handler");

        ui.Sequence = 20; ui.AirRed = ui.AmbushRed = 100;
        airProbe = new MapEncounterProbe(ui, true);
        await airProbe.InitializeAsync(20, default);
        for (int i = 0; i < 16; i++) ui.ScreenshotReds.Enqueue(160);
        before = ui.Screenshots;
        airHandler = new MapAirRaidHandler(ui, airProbe, () => ui.Sequence);
        Check((await airHandler.HandleAsync(MapEncounterKind.AirRaid, default)).Continuation == MapEncounterContinuation.InMap &&
              ui.Screenshots - before >= 11,
            "Air raid timeout no longer follows the upstream 2.5-second wait");
    }

    private sealed class Ui : IUiDriver
    {
        private readonly ClockSource _clock = new();
        public GameServer Server => GameServer.Cn;
        public bool HasFrame => true;
        public TimeProvider Clock => _clock;
        public long Sequence { get; set; } = 1;
        public double AirRed { get; set; }
        public double AmbushRed { get; set; }
        public int Screenshots { get; private set; }
        public Queue<double> ScreenshotReds { get; } = new();
        public HashSet<string> Appearing { get; } = new(StringComparer.Ordinal);
        public ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = 0.85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Appearing.Contains(asset.Id)); }
        public ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            double red = area == UiAssets.Handler.MAP_AIR_RAID.For(Server).Area ? AirRed :
                area == UiAssets.Handler.MAP_AMBUSH.For(Server).Area ? AmbushRed :
                throw new InvalidOperationException("Unexpected color area");
            return ValueTask.FromResult(new MeanColorObservation(Sequence, red, 0, 0));
        }
        public ValueTask ScreenshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Screenshots++; Sequence++; _clock.Advance();
            AirRed = ScreenshotReds.Dequeue();
            return ValueTask.CompletedTask;
        }
        public ValueTask ClickAsync(AssetRule asset, CancellationToken token) => throw new InvalidOperationException("Unexpected click");
        public ValueTask ClickAreaAsync(Rectangle area, CancellationToken token) => throw new InvalidOperationException("Unexpected click");
        public ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token)
            => throw new InvalidOperationException("Unexpected color bands");
        public ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token)
            => throw new InvalidOperationException("Unexpected OCR");
        public void ClearOffset(AssetRule asset) => throw new InvalidOperationException("Unexpected offset");
        public IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false)
            => throw new InvalidOperationException("Unexpected timer");
        public void ResetInterval(AssetRule asset, double seconds = 3) => throw new InvalidOperationException("Unexpected timer");
        public void ClearInterval(AssetRule asset) => throw new InvalidOperationException("Unexpected timer");
        public ValueTask DelayAsync(TimeSpan time, CancellationToken token) => throw new InvalidOperationException("Unexpected delay");
    }
    private sealed class ClockSource : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += TimeSpan.FromSeconds(0.25).Ticks;
    }
}
