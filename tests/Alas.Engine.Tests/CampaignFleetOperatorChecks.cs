using System.Security.Cryptography;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class CampaignFleetOperatorChecks
{
    public static async Task RunAsync(string upstream)
    {
        var source = CampaignFleetOperator.Source;
        Check(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
            "Native fleet operator source drifted");

        var driver = new Driver();
        var vision = new Patches(driver);
        var op = Operator(driver, vision);
        await op.InitializeAsync();
        Check(driver.Copied.SequenceEqual(["FLEET_1_CHOOSE", "FLEET_1_BAR", "FLEET_1_IN_USE", "FLEET_1_HARD_SATIESFIED"]),
            "Fleet offsets were not copied from the clear button");
        Check(FleetSlot.SecondFor(driver).InUse == UiAssets.Map.FLEET_2_IN_USE, "Default second fleet asset changed");
        driver.OffsetY = -20;
        Check(FleetSlot.SecondFor(driver).InUse == UiAssets.Map.FLEET_2_IN_USE_W15, "W15 second fleet asset was not selected");
        driver.OffsetY = 0;

        driver.Selected.Add(2);
        Check((await op.SelectedAsync()).SequenceEqual([2]) && driver.RowReads == 6,
            "Fleet bar must sample exactly six rows and use sample RGB deviation");
        await op.EnsureAsync(2);
        Check(!driver.BarOpen && driver.ChooseClicks == 2 && driver.AreaClicks == 0,
            "Already selected fleet was not closed without selecting again");
        await op.EnsureAsync(3);
        Check(driver.Selected.Contains(3) && !driver.BarOpen && driver.AreaClicks == 1 && driver.InUse,
            "New fleet was not selected from the open bar");
        Check(driver.LastArea == new Rectangle(1012, 353, 1183, 386), "Fleet row click did not follow the bar offset");
        await Throws<ArgumentOutOfRangeException>(() => op.EnsureAsync(0).AsTask());
        await Throws<ArgumentOutOfRangeException>(() => op.EnsureAsync(7).AsTask());
        Check(driver.AreaClicks == 1, "Invalid fleet index caused a click");

        driver.InUse = false;
        driver.InUseColor = new Rgb(224, 154, 114);
        int deviations = vision.DeviationReads;
        Check(await op.InUseAsync() && vision.DeviationReads == deviations, "Perseus skin shortcut was not applied");
        driver.InUseColor = new Rgb(124, 141, 171);
        Check(await op.InUseAsync() && vision.DeviationReads == deviations, "Akane skin shortcut was not applied");
        driver.InUseColor = null;
        Check(!await op.InUseAsync(), "Empty fleet contrast was read as in-use");
        driver.InUse = true;
        Check(await op.InUseAsync() && vision.DeviationReads == deviations + 2, "Gray sample deviation was not used");

        driver.PopupPending = true;
        driver.InUse = true;
        await op.ClearAsync();
        Check(!driver.InUse && driver.PopupConfirms == 1 && driver.ClearClicks == 1,
            $"Fleet clear did not confirm the transient hard-mode popup: in_use={driver.InUse}, confirms={driver.PopupConfirms}, clears={driver.ClearClicks}");
        driver.InUse = true;
        driver.ClearStalled = true;
        await Throws<TimeoutException>(() => op.ClearAsync().AsTask());
        driver.ClearStalled = false;
        driver.BarStalled = true;
        driver.BarOpen = false;
        await Throws<TimeoutException>(() => op.EnsureAsync(1).AsTask());
        driver.BarStalled = false;
        driver.StaleColor = true;
        await Throws<InvalidDataException>(() => op.InUseAsync().AsTask());
        driver.StaleColor = false;
        vision.Stale = true;
        await Throws<InvalidDataException>(() => op.BarOpenedAsync().AsTask());
        Console.WriteLine("Fleet operator: offsets, six-row parsing, selection, skins, popup clear, stale frames and bounded retries passed offline.");
    }

    private static CampaignFleetOperator Operator(Driver driver, Patches vision)
        => new(driver, vision, () => driver.Frame, FleetSlot.First, driver);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Patches(Driver driver) : IImagePatchVision
    {
        public int DeviationReads { get; private set; }
        public bool Stale { get; set; }
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            double value;
            if (request.Measure == PatchMeasure.StandardDeviation)
            {
                Check(request.Processing == PatchProcessing.Gray && request.Area == driver.ButtonArea(FleetSlot.First.InUse).Area,
                    "Fleet in-use crop changed");
                DeviationReads++;
                value = driver.InUse ? 52 : 6;
            }
            else
            {
                var bar = driver.ButtonArea(FleetSlot.First.Bar);
                Check(request.Measure == PatchMeasure.SimilarityCount && request.Processing == PatchProcessing.Gray &&
                    request.MinimumSimilarity == 169 && request.Area == new PixelArea(bar.Right - 1, bar.Top, 1, bar.Height),
                    "Fleet dropdown brightness request changed");
                value = driver.BarOpen ? bar.Height : 0;
            }
            return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence - (Stale ? 1 : 0), value));
        }
    }

    private sealed class Driver : AppearanceProbe, IPopupHandler
    {
        private long _sequence = 1;
        public Driver() : base(GameServer.Cn, null) { }
        public ScreenFrame Frame => new(_sequence, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
        public List<string> Copied { get; } = [];
        public HashSet<int> Selected { get; } = [];
        public bool BarOpen { get; set; }
        public bool BarStalled { get; set; }
        public bool InUse { get; set; }
        public bool ClearStalled { get; set; }
        public bool PopupPending { get; set; }
        public bool StaleColor { get; set; }
        public Rgb? InUseColor { get; set; }
        public int OffsetY { get; set; }
        public int RowReads { get; private set; }
        public int ChooseClicks { get; private set; }
        public int AreaClicks { get; private set; }
        public int ClearClicks { get; private set; }
        public int PopupConfirms { get; private set; }
        public Rectangle LastArea { get; private set; }
        public override ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); _sequence++; Time.Advance(.5); return ValueTask.CompletedTask; }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Check(offset == ButtonOffset.Bounds(-20, -80, 20, 5), "Fleet search offset changed");
            return ValueTask.FromResult(asset == FleetSlot.First.Clear && !PopupPending);
        }
        public override Rectangle ButtonArea(AssetRule asset)
            => (asset.For(Server).ClickArea ?? throw new InvalidDataException()).Offset(0, OffsetY);
        public override void LoadOffset(AssetRule target, AssetRule reference)
        { Check(reference == FleetSlot.First.Clear, "Fleet offset source changed"); Copied.Add(target.Name); }
        public override ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var inUse = ButtonArea(FleetSlot.First.InUse);
            Rgb color;
            if (area == inUse) color = InUseColor ?? new Rgb(71, 70, 63);
            else
            {
                var bar = ButtonArea(FleetSlot.First.Bar);
                int row = (area.Top - bar.Top) / 42 + 1;
                Check(area.Left == bar.Left && area.Right == bar.Right && row is >= 1 and <= 6 &&
                    area.Bottom - area.Top == 33, "Fleet row crop exceeded upstream bar geometry");
                RowReads++;
                color = Selected.Contains(row) ? new Rgb(200, 100, 0) : new Rgb(100, 100, 100);
            }
            return ValueTask.FromResult(new MeanColorObservation(_sequence - (StaleColor ? 1 : 0), color.R, color.G, color.B));
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (asset == FleetSlot.First.Choose) { ChooseClicks++; if (!BarStalled) BarOpen = !BarOpen; }
            else if (asset == FleetSlot.First.Clear) { ClearClicks++; if (!ClearStalled) InUse = false; }
            else throw new InvalidOperationException("Unexpected fleet button");
            return ValueTask.CompletedTask;
        }
        public override ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LastArea = area;
            AreaClicks++;
            int row = (area.Top - ButtonArea(FleetSlot.First.Bar).Top) / 42 + 1;
            Selected.Add(row);
            InUse = true;
            BarOpen = false;
            return ValueTask.CompletedTask;
        }
        public override ValueTask DelayAsync(TimeSpan time, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Time.Advance(time.TotalSeconds); return ValueTask.CompletedTask; }
        public ValueTask<bool> ConfirmAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!PopupPending) return ValueTask.FromResult(false);
            PopupPending = false;
            PopupConfirms++;
            return ValueTask.FromResult(true);
        }
    }
}
