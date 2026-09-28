using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class SubmarineMoveChecks
{
    private static async Task SettingsAsync(JsonArray native)
    {
        foreach (var server in Enum.GetValues<GameServer>())
        foreach (var sample in native)
        {
            int mask = sample!["mask"]!.GetValue<int>();
            var probe = new SettingsProbe(server, mask, mask % 2 == 0 ? -50 : 0);
            var operation = new CampaignAutoSearchSettings(probe, probe, () => probe.Frame).EnsureSubmarineStandbyAsync().AsTask();
            if (mask == 0) await Rejects<TimeoutException>(() => operation); else await operation;
            Check(probe.Clicks.SequenceEqual(sample["clicks"]!.AsArray().Select(n => n!.GetValue<string>())),
                "Standby selection differs from native mask: " + mask);
            Check(probe.SidebarClicks == 1, "Sidebar used the wrong active index or offset");
        }
        var stale = new SettingsProbe(GameServer.Cn, 16, 0) { Stale = true };
        await Rejects<InvalidDataException>(() => new CampaignAutoSearchSettings(stale, stale, () => stale.Frame).EnsureSubmarineStandbyAsync().AsTask());
        Check(stale.Clicks.Count == 0 && stale.SidebarClicks == 0, "Stale sidebar frame caused input");
    }

    private sealed class SettingsProbe(GameServer server, int mask, int offset) : AppearanceProbe(server, null), IImagePatchVision
    {
        public ScreenFrame Frame { get; private set; } = new(1, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
        public bool Stale { get; init; }
        public int SidebarClicks { get; private set; }
        public List<string> Clicks { get; } = [];
        private int _mask = mask;
        private int _active = 1;
        public override ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Time.Advance(.25); if (!Stale) Frame = Frame with { Sequence = Frame.Sequence + 1 }; return ValueTask.CompletedTask; }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset search = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            Check(asset == UiAssets.Map.FLEET_PREPARATION_CHECK && search == ButtonOffset.Expand(20, 80), "Sidebar anchor search changed");
            return ValueTask.FromResult(true);
        }
        public override Rectangle ButtonArea(AssetRule asset) => asset.For(Server).ClickArea!.Value.Offset(0, offset);
        private Rectangle Side(int index) => Server == GameServer.En
            ? new(1178, 171 + offset + index * 53, 1276, 213 + offset + index * 53)
            : new(1185, 155 + offset + index * 111, 1238, 259 + offset + index * 111);
        public override ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        { Check(area == Side(2), "Sidebar ignored native server geometry or matched Y offset"); SidebarClicks++; _active = 3; return ValueTask.CompletedTask; }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            Check(_active == 3 && _mask != 0 && asset == UiAssets.Handler.AUTO_SEARCH_SET_SUB_STANDBY, "Blind setting click");
            Clicks.Add(asset.Name); _mask |= 32; return ValueTask.CompletedTask;
        }
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Check(request.Measure == PatchMeasure.SimilarityCount && request.Processing == PatchProcessing.ColorSimilarity && request.MinimumSimilarity == 225,
                "Sidebar color semantics changed");
            double count;
            if (request.Color == new PixelColor(156, 255, 82))
            {
                int index = Array.FindIndex(CampaignAutoSearchSettings.Settings, asset => ButtonArea(asset).Area == request.Area);
                Check(index >= 0 && _active == 3, "Setting lost fleet button offset");
                count = (_mask & (1 << index)) == 0 ? 20 : 21;
            }
            else
            {
                int index = Enumerable.Range(0, 3).Single(i => Side(i).Area == request.Area);
                count = request.Color == new PixelColor(99, 235, 255) ? (index + 1 == _active ? 51 : 50) : 101;
            }
            return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence, count));
        }
    }
}
