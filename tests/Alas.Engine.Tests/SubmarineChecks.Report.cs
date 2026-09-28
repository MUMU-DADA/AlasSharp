using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class SubmarineChecks
{
    private static async Task ReportsAsync(string python, string artifacts)
    {
        foreach (string mode in new[] { "found", "assumption", "failed" })
        {
            var options = new EngineSessionOptions("unused-adb", "offline", GameServer.Cn, ".", python);
            var result = await new TaskQueue([new ReportProbe(mode)]).RunAsync(
                [new("localize", "submarine_probe"), new("next", "submarine_probe")], options,
                new(Path.Combine(artifacts, "reports"), ContinueOnFailure: true));
            Check(result.Tasks[0].Outcome == (mode == "failed" ? TaskOutcome.Failed : TaskOutcome.Succeeded) &&
                result.Tasks[1].Outcome == TaskOutcome.Succeeded, "Submarine probe lost its task result or next task");
            var paths = Directory.GetFiles(result.Directory, "submarine-location.json", SearchOption.AllDirectories);
            Check(paths.Length == 1 && RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(),
                "Submarine queue evidence was lost, reused or rejected");
            string original = await File.ReadAllTextAsync(paths[0]);
            File.Delete(paths[0]);
            Check(!RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(),
                "Missing submarine evidence was reported complete");
            foreach (string corruption in new[] { "method", "location", "null-observation", "frame", "match", "different-location" })
            {
                var changed = JsonNode.Parse(original)!.AsObject();
                switch (corruption)
                {
                    case "method": changed["method"] = "searching"; break;
                    case "location": changed["location"] = new JsonObject { ["column"] = 0, ["row"] = 1 }; break;
                    case "null-observation": changed["observations"]!.AsArray()[0] = null; break;
                    case "frame": changed["observations"]![0]!["frameSequence"] = 0; break;
                    case "match":
                        changed["method"] = "searched_observation";
                        changed["location"] = changed["observations"]![0]!["location"]!.DeepClone();
                        changed["pending"] = null;
                        changed["observations"]!.AsArray().Last()!["present"] = false;
                        break;
                    case "different-location":
                        // Structurally valid source, but inconsistent with the task's declared result.
                        changed["location"] = new JsonObject { ["column"] = 1, ["row"] = 2 };
                        if (mode == "found") changed["observations"]!.AsArray().Last()!["location"] = changed["location"]!.DeepClone();
                        break;
                }
                await File.WriteAllTextAsync(paths[0], changed.ToJsonString());
                Check(!RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(),
                    "Invalid submarine evidence was reported complete: " + corruption);
            }
            await File.WriteAllTextAsync(paths[0], original);
        }
        Console.WriteLine("Submarine queue/report: found, inferred and partial failure evidence; next-task isolation and missing/corrupt artifact rejection passed without device I/O.");
    }

    private sealed class ReportProbe(string mode) : ITaskRunner
    {
        public string Kind => "submarine_probe";
        public bool RequiresActions => false;
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            if (request.Id == "next") return new(request.Id, Kind, TaskOutcome.Succeeded, "no_localization");
            var session = context.Campaign as EngineSession ?? throw new InvalidOperationException("Actual session required");
            var execution = session.CreateInMapCampaignExecution(RuleCatalog.Create("campaign_main/campaign_2_1"), new());
            var state = execution.Context.State;
            state.InitializeMapData(new());
            foreach (var cell in state.Cells) cell.IsSubmarineSpawnPoint = false;
            state[new(1, 1)].IsSubmarineSpawnPoint = state[new(2, 1)].IsSubmarineSpawnPoint = true;
            var trace = new JsonArray(new JsonObject { ["location"] = "A1", ["camera"] = "A2", ["present"] = false },
                new JsonObject { ["location"] = "B1", ["camera"] = "A2", ["present"] = mode == "found" });
            // A bad second frame fails after retaining the first completed observation.
            var camera = new Camera(new(1, 1), trace) { Corruption = mode == "failed" ? 4 : 0 };
            var evidence = await MapSubmarineLocator.LocateAsync(state, true, camera, TimeSpan.FromSeconds(3), token);
            return new(request.Id, Kind, TaskOutcome.Succeeded, "localized",
                new() { ["submarine"] = JsonSerializer.SerializeToNode(evidence, TaskQueue.Json) });
        }
    }
}
