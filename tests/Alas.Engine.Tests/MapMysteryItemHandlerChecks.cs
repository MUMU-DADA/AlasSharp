using System.Security.Cryptography;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class MapMysteryItemHandlerChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync(string upstream)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(upstream, MapMysteryItemHandler.Source.Path));
        Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) == MapMysteryItemHandler.Source.Sha256,
            "Native mystery handler source drifted");

        var ui = new Ui();
        var handler = new MapMysteryItemHandler(ui);
        Check((await handler.HandleAsync(MapEncounterKind.Ambush, default)).Continuation ==
            MapEncounterContinuation.Unhandled && ui.Clicks.Count == 0,
            "Mystery handler acted on an unrelated encounter");
        ui.Popup = false;
        Check((await handler.HandleAsync(MapEncounterKind.ItemPopup, default)).Continuation ==
            MapEncounterContinuation.Unhandled && ui.Clicks.Count == 0,
            "Mystery handler clicked without an observed reward popup");

        ui.Popup = true;
        ui.StrategyOpen = true;
        var handled = await handler.HandleAsync(MapEncounterKind.ItemPopup, default);
        Check(handled.Continuation == MapEncounterContinuation.InMap &&
            ui.Clicks.SequenceEqual([UiAssets.Handler.MYSTERY_ITEM.Id, UiAssets.Handler.STRATEGY_OPENED.Id]) &&
            ui.Screenshots == 2 && ui.Delays == 1,
            "Observed item mystery did not close reward and strategy overlays in C#");
    }

    private sealed class Ui : IUiDriver
    {
        public GameServer Server => GameServer.Cn;
        public bool HasFrame => true;
        public TimeProvider Clock => TimeProvider.System;
        public bool Popup { get; set; } = true;
        public bool StrategyOpen { get; set; }
        public List<string> Clicks { get; } = [];
        public int Screenshots { get; private set; }
        public int Delays { get; private set; }
        public ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Screenshots++; return ValueTask.CompletedTask; }
        public ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10,
            TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(asset.Id == UiAssets.Combat.GET_ITEMS_1.Id ? Popup :
                asset.Id == UiAssets.Handler.STRATEGY_OPENED.Id && StrategyOpen);
        }
        public ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Clicks.Add(asset.Id);
            if (asset.Id == UiAssets.Handler.MYSTERY_ITEM.Id) Popup = false;
            if (asset.Id == UiAssets.Handler.STRATEGY_OPENED.Id) StrategyOpen = false;
            return ValueTask.CompletedTask;
        }
        public ValueTask DelayAsync(TimeSpan time, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Delays++; return ValueTask.CompletedTask; }
        public ValueTask ClickAreaAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token) => throw new NotSupportedException();
        public void ClearOffset(AssetRule asset) => throw new NotSupportedException();
        public IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false) => throw new NotSupportedException();
        public void ResetInterval(AssetRule asset, double seconds = 3) => throw new NotSupportedException();
        public void ClearInterval(AssetRule asset) => throw new NotSupportedException();
    }
}
