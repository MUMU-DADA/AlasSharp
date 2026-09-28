using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class SubmarineCallChecks
{
    private static async Task ReportsAsync(string python, string upstream, string artifacts)
    {
        await using (var session = new EngineSession(new("unused-adb", "offline", GameServer.Cn, Path.Combine(upstream, "assets"), python)))
            foreach (var mode in new[] { SubmarineMode.BossOnly, SubmarineMode.HuntAndBoss })
                await Rejects<NotSupportedException>(() => session.ResumeInMapAsync(RuleCatalog.Create("campaign_main/campaign_13_1"),
                    new() { Submarine = 1, SubmarineMode = mode, EmotionMode = CampaignEmotionMode.Ignore }, default).AsTask());
        foreach (int fleet in new[] { 0, 1 })
        foreach (var mode in new[] { SubmarineMode.DoNotUse, SubmarineMode.HuntOnly, SubmarineMode.EveryCombat })
        {
            var options = new EngineSessionOptions("unused-adb", "offline", GameServer.Cn, Path.Combine(upstream, "assets"), python);
            var result = await new TaskQueue([new CallProbe(fleet, mode)]).RunAsync(
                [new("call", "call_probe"), new("next", "call_probe")], options, new(Path.Combine(artifacts, "reports")));
            Check(result.Tasks.All(t => t.Outcome == TaskOutcome.Succeeded), "Session construction probe failed");
            var paths = Directory.GetFiles(result.Directory, "submarine-calls.json", SearchOption.AllDirectories);
            Check(paths.Length == 1 && Complete(), "Session call evidence was missing or leaked across task boundaries");
            string original = await File.ReadAllTextAsync(paths[0]);
            var saved = JsonSerializer.Deserialize<SubmarineCallEvidence[]>(original, TaskQueue.Json)!;
            Check(saved is [{ State: "not_started", Attempts.Count: 0 }] && saved[0].Mode == (fleet == 0 ? SubmarineMode.DoNotUse : mode),
                "Session ignored the actual fleet or submarine configuration");
            File.Delete(paths[0]); Check(!Complete(), "Missing call file passed report validation");
            // Validator fixtures are synthetic: they exercise the artifact contract, not device observations.
            var valid = new SubmarineCallEvidence(SubmarineMode.EveryCombat, "called_observed", 1, 3, 3, [new(2, false, true)]);
            foreach (var item in new[] { valid, valid with { State = "failed" },
                valid with { State = "failed", ObservedFrame = null, Attempts = [new(2, true, false)] },
                valid with { State = "battle_ended_unconfirmed", ObservedFrame = null },
                valid with { State = "window_expired", ObservedFrame = null },
                new(SubmarineMode.HuntOnly, "disabled", 1, 1, null, []) })
            {
                await Save(item); Check(Complete(), "Consistent synthetic call record was rejected: " + item.State);
            }
            foreach (SubmarineCallEvidence? item in new SubmarineCallEvidence?[] {
                null, valid with { Mode = (SubmarineMode)99 }, valid with { Mode = SubmarineMode.HuntOnly },
                valid with { State = "waiting" }, valid with { StartedFrame = 4 }, valid with { ObservedFrame = 2 },
                valid with { ObservedFrame = null }, valid with { Attempts = [new(4, true, true)] },
                valid with { Attempts = [new(2, true, false)] }, valid with { State = "failed", Attempts = [new(1, true, false), new(2, true, true)] },
                valid with { Attempts = [new(2, true, true), new(2, true, true)] }, valid with { Attempts = null! } })
            {
                await Save(item); Check(!Complete(), "Corrupt call record was accepted: " + item);
            }
            await File.WriteAllTextAsync(paths[0], original);
            bool Complete() => RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>();
            Task Save(SubmarineCallEvidence? item) => File.WriteAllTextAsync(paths[0], JsonSerializer.Serialize(new[] { item },
                new JsonSerializerOptions(TaskQueue.Json) { RespectNullableAnnotations = false }));
        }
        Console.WriteLine("Actual EngineSession factory/queue: mode wiring, disabled fleet and next-task isolation passed; synthetic report corruption checks passed without device I/O.");
    }

    private sealed class CallProbe(int fleet, SubmarineMode mode) : ITaskRunner
    {
        public string Kind => "call_probe";
        public bool RequiresActions => false;
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            if (request.Id != "next")
            {
                var session = context.Campaign as EngineSession ?? throw new InvalidOperationException("Actual session required");
                _ = session.CreateCampaignCombatFlow(new CampaignState(RuleCatalog.Create("campaign_main/campaign_13_1").Map),
                    new() { Submarine = fleet, SubmarineMode = mode, EmotionMode = CampaignEmotionMode.Ignore });
            }
            return ValueTask.FromResult(new TaskResult(request.Id, Kind, TaskOutcome.Succeeded, "factory_probe"));
        }
    }
}
