using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class DataKeyChecks
{
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        int checks = 0;
        var source = DataKeyTask.Source;
        if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) != source.Sha256)
            throw new InvalidOperationException("DataKey upstream source drifted");
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, $"data-key-{server}.json");
            var reference = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_data_key_reference.py"), upstream, server.ToString().ToLowerInvariant(), output], TimeSpan.FromSeconds(90));
            if (reference.ExitCode != 0) throw new InvalidOperationException("Native DataKey failed: " + reference.Error);
            foreach (var expected in JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsArray())
            {
                var sample = expected!["sample"]!;
                var driver = new DataKeyDriver(server, sample);
                TaskResult? result = null;
                string? error = null;
                try
                {
                    var task = new DataKeyTask();
                    result = await task.RunAsync(new("key", task.Kind, new JsonObject { ["forceCollect"] = sample["force"]!.GetValue<bool>() }),
                        new(driver, driver, driver, TimeSpan.FromSeconds(5)), default);
                }
                catch (IOException) { error = "OSError"; }
                catch (TimeoutException) { error = "TimeoutError"; }
                bool? collected = result?.Evidence?["collectionExecuted"]?.GetValue<bool>();
                if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(driver.Events), expected["events"]) ||
                    error != expected["error"]?.GetValue<string>() || collected != expected["collected"]?.GetValue<bool>())
                {
                    await File.WriteAllTextAsync(Path.Combine(artifacts, "data-key-mismatch.json"),
                        new JsonObject { ["expected"] = expected.DeepClone(), ["events"] = JsonSerializer.SerializeToNode(driver.Events),
                            ["collected"] = collected, ["error"] = error }.ToJsonString());
                    throw new InvalidOperationException($"Native DataKey differs: {server}/{sample["name"]}; inspect data-key-mismatch.json");
                }
                if (result is not null && (collected == true && result.Outcome != TaskOutcome.Succeeded ||
                    result.Outcome == TaskOutcome.Succeeded && collected == false && result.Reason != "already_collected"))
                    throw new InvalidOperationException("DataKey task conclusion overstated its observation");
                checks++;
            }
        }
        Console.WriteLine($"Native DataKey: {checks} task traces passed across four servers; scripted observations/actions, no collection on a device.");
    }

    private sealed class DataKeyDriver(GameServer server, JsonNode sample) : AppearanceProbe(server, null), IPageNavigator, IPopupHandler
    {
        public List<object[]> Events { get; } = [];
        private int _index;
        private bool Positive(string name) => sample["frames"]![_index]!.AsArray().Any(n => n!.GetValue<string>() == name);
        public ValueTask<NavigationObservation> EnsureAsync(string destination, TimeSpan timeout, bool skipFirstScreenshot = true, CancellationToken token = default)
        { Events.Add(["navigate", destination]); return ValueTask.FromResult(new NavigationObservation(destination, true)); }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = 0.85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            Events.Add(["appear", asset.Name, new[] { offset.Left, offset.Top, offset.Right, offset.Bottom }, interval, similarity, threshold]);
            return ValueTask.FromResult(Positive(asset.Name));
        }
        public override ValueTask ScreenshotAsync(CancellationToken token)
        {
            Events.Add(["screenshot"]);
            if (++_index >= sample["frames"]!.AsArray().Count) throw new TimeoutException("scripted frames exhausted");
            return ValueTask.CompletedTask;
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            Events.Add(["click", asset.Name]);
            if (sample["fail"]!.GetValue<bool>()) throw new IOException("synthetic click failure");
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> ConfirmAsync(CancellationToken token) { Events.Add(["confirm"]); return ValueTask.FromResult(Positive("confirm")); }
        public override ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token)
        {
            Events.Add(["ocr"]);
            if (request != new OcrRequest(UiAssets.Freebies.OCR_DATA_KEY.For(Server).Area!.Value.Area,
                "azur_lane", OcrValues.CounterAlphabet, 255, 247, 247, 64)) throw new InvalidOperationException("DataKey OCR parameters changed");
            return ValueTask.FromResult(new OcrObservation(_index + 1, sample["text"]!.GetValue<string>(), null));
        }
        public override void ClearInterval(AssetRule asset) => Events.Add(["clear", asset.Name]);
    }
}
