using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignObjectiveChecks
{
    private static async Task StoreChecksAsync(string artifacts)
    {
        var identity = new EmotionDeviceIdentity("offline-device", "org.example.game", GameServer.Cn);
        foreach (string owner in new[] { "General", "Alas", "TaskBalancer", "EventGeneral", "Event" })
        {
            string root = Path.Combine(artifacts, "store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var template = JsonNode.Parse("""
                {"Alas":{"Emulator":{"Serial":"offline-device","PackageName":"org.example.game"}},
                 "Event":{"Campaign":{"Event":"event_fixture","Name":"A1"},"Scheduler":{"Enable":true,"NextRun":"kept"}},
                 "Main":{"Campaign":{"Event":"campaign_main","Name":"1-1"},"Scheduler":{"Enable":true}}}
                """)!.AsObject();
            template[owner] ??= new JsonObject();
            template[owner]!["Campaign"] = new JsonObject { ["Name"] = "B1" };
            if (owner == "Event") template[owner]!["Campaign"]!["Event"] = "event_fixture";
            template[owner]!["Scheduler"] = new JsonObject { ["Enable"] = true, ["NextRun"] = "kept" };
            string path = Path.Combine(root, "config/fixture.json");
            await File.WriteAllTextAsync(Path.Combine(root, "config/template.json"), template.ToJsonString());
            await File.WriteAllTextAsync(path, "{\"Alas\":{}}");
            var workspace = new ConfigWorkspace(root);
            var store = workspace.CampaignStopStore("fixture", "Event", identity);
            var snapshot = await store.ReadAsync(default);
            Check(snapshot is { Folder: "event_fixture", Stage: "B1", Enabled: true }, "Native task binding priority differs");
            // Unrelated concurrent writes must survive the selected-field transaction.
            await File.WriteAllTextAsync(path, "{\"Alas\":{},\"Extra\":{\"Value\":17},\"Event\":{\"Emotion\":{\"Fleet1Value\":98}}}");
            await store.SaveAsync(snapshot, "B2", default);
            var raw = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Check(raw[owner]!["Campaign"]!["Name"]!.GetValue<string>() == "B2" &&
                raw["Extra"]!["Value"]!.GetValue<int>() == 17 && raw["Event"]!["Emotion"]!["Fleet1Value"]!.GetValue<int>() == 98,
                "Stage advance overwrote unrelated values or wrong field owner");
            await Rejects<ConfigWorkspaceException>(() => store.SaveAsync(snapshot, null, default).AsTask());
            File.Copy(path, Path.Combine(root, "config/second.json"));
            var before = await File.ReadAllTextAsync(path);
            var other = workspace.CampaignStopStore("second", "Event", identity);
            await other.SaveAsync(await other.ReadAsync(default), null, default);
            Check(await File.ReadAllTextAsync(path) == before, "Stop mutated another instance");
            await store.SaveAsync(await store.ReadAsync(default), null, default);
            raw = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Check(raw[owner]!["Scheduler"]!["Enable"]!.GetValue<bool>() == false &&
                raw[owner]!["Campaign"]!["Name"]!.GetValue<string>() == "B2" &&
                workspace.Get("fixture").Values["Main"]!["Scheduler"]!["Enable"]!.GetValue<bool>(),
                "Stop changed stage or unrelated task instead of the bound scheduler");
            snapshot = await store.ReadAsync(default);
            raw["Alas"] ??= new JsonObject();
            raw["Alas"]!["Emulator"] = new JsonObject { ["Serial"] = "different-device", ["PackageName"] = identity.Package };
            await File.WriteAllTextAsync(path, raw.ToJsonString());
            before = await File.ReadAllTextAsync(path);
            await Rejects<ConfigWorkspaceException>(() => store.SaveAsync(snapshot, "B3", default).AsTask());
            Check(await File.ReadAllTextAsync(path) == before, "Device identity conflict partially wrote campaign state");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Rejects<OperationCanceledException>(() => other.SaveAsync(snapshot, "B3", cancel.Token).AsTask());
        }
    }
}
