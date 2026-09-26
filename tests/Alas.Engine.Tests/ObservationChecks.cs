using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class ObservationChecks
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static string Key(string name) => name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
    private static readonly Dictionary<string, PropertyInfo> Fields = typeof(CellState).GetProperties().ToDictionary(p => Key(p.Name));
    private static readonly Dictionary<string, PropertyInfo> ObservationFields = typeof(CellObservation).GetProperties().ToDictionary(p => Key(p.Name));

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        foreach (var source in new[] { CellState.Source, MapPathfinder.Source })
            if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) != source.Sha256)
                throw new InvalidOperationException("Observation upstream source drifted: " + source.Path);
        string output = Path.Combine(artifacts, "native-observation.json");
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_observation_reference.py"), upstream, output], TimeSpan.FromSeconds(90));
        if (process.ExitCode != 0) throw new InvalidOperationException("Native observation reference failed: " + process.Error);
        var cases = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsArray();
        int views = 0, accepted = 0, rejected = 0, ignored = 0;
        foreach (var item in cases)
        {
            var sample = item!["sample"]!;
            // Test-only reflection compiles the oracle's input predicates; product rules use C# lambdas.
            var rules = sample["ignored"]!.AsArray().Select(r => new IgnoredPrediction(Cell.Parse(r!["cell"]!.GetValue<string>()),
                observation => r["state"]!.AsObject().All(p => JsonNode.DeepEquals(p.Value,
                    JsonSerializer.SerializeToNode(ObservationFields[Key(p.Key)].GetValue(observation)))))).ToArray();
            var state = new CampaignState(new MapDefinition(sample["shape"]!.GetValue<string>(), sample["tiles"]!.GetValue<string>(), [], [], [], ignoredPredictions: rules));
            foreach (var patch in sample["before"]!.AsArray())
                foreach (var pair in patch!["state"]!.AsObject())
                {
                    var property = Fields[Key(pair.Key)];
                    property.SetValue(state[Cell.Parse(patch["cell"]!.GetValue<string>())], pair.Value?.Deserialize(property.PropertyType, Json));
                }
            int index = 0;
            foreach (var view in sample["views"]!.AsArray())
            {
                var expected = item["outputs"]![index++]!;
                var cells = view!["cells"]!.AsArray().Select(c => new MapCellObservation(new(c!["local"]![0]!.GetValue<int>(), c["local"]![1]!.GetValue<int>()),
                    c["state"]!.Deserialize<CellObservation>(Json)!)).ToArray();
                var result = state.ApplyObservation(new(cells, Cell.Parse(view["camera"]!.GetValue<string>()),
                    new(view["center"]![0]!.GetValue<int>(), view["center"]![1]!.GetValue<int>()), Enum.Parse<MapScanMode>(view["mode"]!.GetValue<string>(), true)));
                if (result.Accepted != expected["accepted"]!.GetValue<bool>() || result.FailedPredictions != expected["failures"]!.GetValue<int>() ||
                    result.Applied != expected["applied"]!.GetValue<int>() ||
                    !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(result.Ignored.Select(c => c.ToString())), expected["ignored"]) ||
                    !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(result.Outside.Select(c => new[] { c.Column, c.Row })), expected["outside"]))
                    throw new InvalidOperationException($"Map observation decision differs: {sample["name"]}/{index}");
                foreach (var cell in expected["cells"]!.AsArray())
                    foreach (var pair in cell!["values"]!.AsObject())
                    {
                        var actual = state[Cell.Parse(cell["cell"]!.GetValue<string>())];
                        object? value = pair.Key == "str" ? actual.Encode() : Fields[Key(pair.Key)].GetValue(actual);
                        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(value), pair.Value))
                            throw new InvalidOperationException($"Map observation mutation differs: {sample["name"]}/{index}/{cell["cell"]}/{pair.Key}");
                    }
                views++; ignored += result.Ignored.Count;
                if (result.Accepted) accepted++; else rejected++;
            }
        }
        BoundaryChecks();
        Console.WriteLine($"Native view merge: {cases.Count} scenarios / {views} views ({accepted} accepted, {rejected} rejected) / {ignored} ignored predictions; all state fields, camera offsets, submarine correction and invalid-view rejection passed. Synthetic observations only.");
    }

    private static void BoundaryChecks()
    {
        var state = new CampaignState(new MapDefinition("B1", "ME --", [], [], []));
        var first = new MapCellObservation(new(0, 0), new(IsEnemy: true));
        try { state.ApplyObservation(new([first, first], new(1, 1), new(0, 0))); throw new InvalidOperationException("Duplicate local cell accepted"); }
        catch (ArgumentException) { }
        if (state.Cells.Any(c => c.IsEnemy)) throw new InvalidOperationException("Invalid observation partially mutated map");
        try { state.ApplyObservation(new([], new(1, 1), new(0, 0), (MapScanMode)100)); throw new InvalidOperationException("Unknown scan mode accepted"); }
        catch (ArgumentOutOfRangeException) { }
        state.ApplyObservation(new([first], new(1, 1), new(0, 0)));
        state.Paths.InitializeConnections();
        state.Paths.ComputeCosts(new(2, 1), hasAmbush: false);
        if (!state.HasNonBossEnemy || state[new(1, 1)].Cost != 1) throw new InvalidOperationException("Applied observation did not feed authoritative map/path state");
    }
}
