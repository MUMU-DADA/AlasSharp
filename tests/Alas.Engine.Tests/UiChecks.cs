using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Tests;

internal static class UiChecks
{
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var graph = UpstreamPages.Create();
        int variants = 0, pageRules = 0, routePairs = 0, appearanceCases = 0, navigations = 0, visualCases = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var process = new ProcessRunner();
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, $"native-ui-{server.ToString().ToLowerInvariant()}.json");
            var response = await process.RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_ui_reference.py"), upstream, server.ToString().ToLowerInvariant(), output], TimeSpan.FromSeconds(90));
            Check(response.ExitCode == 0, "Native UI reference failed: " + response.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            var assets = JsonSerializer.SerializeToNode(UiAssets.All.OrderBy(a => a.Id, StringComparer.Ordinal).Select(asset =>
            {
                var variant = asset.For(server);
                return new { asset.Id, asset.Name, kind = asset.Kind.ToString(), area = RectangleValues(variant.Area),
                    color = variant.Color is { } c ? new[] { c.R, c.G, c.B } : null,
                    click = RectangleValues(variant.ClickArea), variant.File, variant.Sha256 };
            }), options);
            Check(JsonNode.DeepEquals(assets, native["assets"]), $"Asset declaration differs for {server}");
            variants += assets!.AsArray().Count;
            var pages = JsonSerializer.SerializeToNode(graph.Pages.Select(page => new
            {
                page.Id, check = page.Check?.Id, links = page.Links.Select(edge => new { edge.Destination, button = edge.Button.Id })
            }), options);
            Check(JsonNode.DeepEquals(pages, native["pages"]), "Page graph declaration differs");
            pageRules += graph.Pages.Length;
            foreach (var destination in graph.Pages)
            {
                var routes = graph.RoutesTo(destination.Id);
                var reference = native["routes"]![destination.Id]!.AsObject();
                Check(routes.Count == reference.Count, "Reachable page set differs");
                foreach (var pair in routes)
                {
                    string current = pair.Key;
                    int distance = 0;
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    while (current != destination.Id)
                    {
                        Check(visited.Add(current), "C# route contains a cycle");
                        current = routes[current].Destination;
                        distance++;
                    }
                    // Native set iteration permits several equal shortest parents. Check distance and declared edge.
                    Check(distance == reference[pair.Key]!["distance"]!.GetValue<int>(), "Shortest route length differs");
                    Check(graph[pair.Key].Links.Any(edge => edge == pair.Value), "Route invented an edge");
                    routePairs++;
                    if (server != GameServer.Cn || destination.Check is null || graph[pair.Key].Check is null) continue;
                    var driver = new NavigationProbe(server, graph, pair.Key, routes);
                    var recovery = new RecoveryProbe();
                    var result = await new UiNavigator(driver, graph, recovery).EnsureAsync(destination.Id, TimeSpan.FromMinutes(1));
                    Check(result.Page == destination.Id &&
                        (driver.CurrentPage == destination.Id || destination.Id == "page_main" && driver.CurrentPage == "page_main_white"), "Navigation claimed wrong page");
                    Check(recovery.AdditionalCalls == 0 && recovery.UnknownCalls == 0, "Known page route invoked synthetic recovery");
                    navigations++;
                }
            }
            foreach (var scenario in native["appearances"]!.AsArray())
            {
                var driver = new AppearanceProbe(server, scenario!["positive"]?.GetValue<string>());
                bool result = await new PageAppearance(driver).AppearsAsync(graph[scenario["page"]!.GetValue<string>()]);
                Check(result == scenario["value"]!.GetValue<bool>(), "Page appearance result differs");
                Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(driver.Calls, options), scenario["calls"]), "Page appearance call order/offset differs");
                appearanceCases++;
            }
            await using var vision = new PythonTemplateVision(python, Path.Combine(AppContext.BaseDirectory, "Imaging", "Worker", "vision_worker.py"));
            var files = new AssetFiles(Path.Combine(upstream, "assets"));
            var matcher = new AssetMatcher(server, vision, files);
            var byId = UiAssets.All.ToDictionary(a => a.Id, StringComparer.Ordinal);
            foreach (var sample in native["visual"]!.AsArray())
            {
                var asset = byId[sample!["asset"]!.GetValue<string>()];
                var frame = new ScreenFrame(++visualCases, DateTimeOffset.UtcNow,
                    await File.ReadAllBytesAsync(Path.Combine(artifacts, sample["image"]!.GetValue<string>())));
                bool color = await matcher.AppearsAsync(frame, asset, ButtonOffset.Color);
                bool matched = await matcher.AppearsAsync(frame, asset, ButtonOffset.Expand(30, 30));
                Check(color == sample["color"]!.GetValue<bool>() && matched == sample["matched"]!.GetValue<bool>(), "Native visual match differs: " + asset.Id);
                Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(RectangleValues(matcher.ClickArea(asset))), sample["click"]), "Native button offset differs: " + asset.Id);
            }
        }
        // Access and elapsed time must both pass, with strict greater-than at the time boundary.
        var clock = new ManualClock();
        var timer = new IntervalTimer(clock, 2, 1);
        Check(timer.Reached(), "Unstarted timer must permit the first attempt");
        timer.Reset(); clock.Advance(2);
        Check(!timer.Reached(), "Timer accepted elapsed-time equality");
        clock.Advance(0.001);
        Check(timer.Reached(), "Timer did not combine count and elapsed time");
        timer.Clear(); Check(timer.Reached(), "Cleared timer did not permit retry");
        Check(AssetMatcher.ColorSimilar(new MeanColorObservation(1, 105, 95, 100), new Rgb(100, 100, 100), 10), "Color equality threshold rejected");
        Check(!AssetMatcher.ColorSimilar(new MeanColorObservation(1, 106, 95, 100), new Rgb(100, 100, 100), 10), "Color positive and negative differences not combined");
        Console.WriteLine($"Native UI declarations: {variants} asset variants / {pageRules} page variants; {routePairs} shortest routes; {appearanceCases} exact appearance traces; {navigations} C# simulated navigations; {visualCases} native visual/color/offset comparisons passed.");
    }
    private static int[]? RectangleValues(Rectangle? rectangle) => rectangle is { } r ? [r.Left, r.Top, r.Right, r.Bottom] : null;
}

internal sealed record AppearanceCall(string Asset, int[] Offset, double Interval);
internal class AppearanceProbe(GameServer server, string? positive) : IUiDriver
{
    private readonly Dictionary<string, IntervalTimer> _timers = new(StringComparer.Ordinal);
    protected readonly ManualClock Time = new();
    public List<AppearanceCall> Calls { get; } = [];
    public GameServer Server => server;
    public bool HasFrame { get; private set; }
    public TimeProvider Clock => Time;
    public virtual ValueTask ScreenshotAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); HasFrame = true; Time.Advance(6); return ValueTask.CompletedTask; }
    public virtual ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
        double similarity = 0.85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
        CancellationToken token = default)
    {
        Calls.Add(new AppearanceCall(asset.Id, [offset.Left, offset.Top, offset.Right, offset.Bottom], interval));
        return ValueTask.FromResult(asset.Id == positive);
    }
    public virtual ValueTask ClickAsync(AssetRule asset, CancellationToken token) => throw new InvalidOperationException("Unexpected appearance probe action");
    public virtual ValueTask ClickAreaAsync(Rectangle area, CancellationToken token) => throw new InvalidOperationException("Unexpected rectangle click");
    public virtual ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token) => throw new InvalidOperationException("Unexpected color request");
    public virtual ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token) => throw new InvalidOperationException("Unexpected color bands request");
    public virtual ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token) => throw new InvalidOperationException("Unexpected OCR request");
    public virtual void ClearOffset(AssetRule asset) { }
    public virtual IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false)
    {
        if (!_timers.TryGetValue(asset.Name, out var timer) || renew && timer.Seconds != seconds)
            _timers[asset.Name] = timer = new IntervalTimer(Time, seconds);
        return timer;
    }
    public virtual void ResetInterval(AssetRule asset, double seconds = 3) => Timer(asset, seconds).Reset();
    public virtual void ClearInterval(AssetRule asset) => Timer(asset, 3).Clear();
    public virtual ValueTask DelayAsync(TimeSpan time, CancellationToken token) { Time.Advance(time.TotalSeconds); return ValueTask.CompletedTask; }
}

internal sealed class NavigationProbe(GameServer server, PageGraph graph, string current,
    IReadOnlyDictionary<string, PageEdge> routes) : AppearanceProbe(server, null)
{
    public string CurrentPage { get; private set; } = current;
    public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
        double similarity = 0.85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
        CancellationToken token = default) => ValueTask.FromResult(graph[CurrentPage].Check?.Id == asset.Id);
    public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
    {
        if (!routes.TryGetValue(CurrentPage, out var edge) || edge.Button != asset) throw new InvalidOperationException("Navigation selected an invalid button");
        CurrentPage = edge.Destination;
        return ValueTask.CompletedTask;
    }
}
internal sealed class RecoveryProbe : IUiRecovery
{
    public int AdditionalCalls { get; private set; }
    public int UnknownCalls { get; private set; }
    public ValueTask<bool> AdditionalAsync(bool getShip, CancellationToken token) { AdditionalCalls++; return ValueTask.FromResult(false); }
    public ValueTask CheckUnknownPageAsync(bool checkApplication, bool checkOrientation, CancellationToken token) { UnknownCalls++; return ValueTask.CompletedTask; }
    public void ResetIntervalsAfterClick(AssetRule button) { }
}
internal sealed class ManualClock : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _timestamp;
    public void Advance(double seconds) => _timestamp += (long)Math.Round(seconds * TimestampFrequency);
}
