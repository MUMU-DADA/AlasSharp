using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignObjectiveChecks
{
    private static async Task StoreChecksAsync(string artifacts)
    {
        var identity = new EmotionDeviceIdentity("offline-device", "org.example.game", GameServer.Cn);
        string root = Path.Combine(artifacts, "store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "profiles"));
        JsonObject profile = new()
        {
            ["device"] = new JsonObject { ["serial"] = identity.Serial, ["package"] = identity.Package, ["server"] = "cn" },
            ["dashboard"] = new JsonObject(),
            ["campaign"] = new JsonObject
            {
                ["emotion"] = new JsonObject
                {
                    ["fleets"] = new JsonArray(
                        new JsonObject { ["value"] = 100, ["recordedAt"] = "2026-01-01 00:00:00", ["control"] = "keep_exp_bonus", ["recovery"] = "not_in_dormitory", ["oath"] = false },
                        new JsonObject { ["value"] = 100, ["recordedAt"] = "2026-01-01 00:00:00", ["control"] = "keep_exp_bonus", ["recovery"] = "not_in_dormitory", ["oath"] = false })
                },
                ["achievement"] = new JsonObject { ["event"] = "event_fixture", ["stage"] = "B1", ["enabled"] = true }
            },
            ["extra"] = new JsonObject { ["value"] = 17 }
        };
        string path = Path.Combine(root, "profiles/fixture.json");
        await File.WriteAllTextAsync(path, profile.ToJsonString());
        var workspace = new EngineProfileStore(root);
        var store = workspace.CampaignStopStore("fixture", identity);
        var snapshot = await store.ReadAsync(default);
        Check(snapshot is { Folder: "event_fixture", Stage: "B1", Enabled: true }, "Engine achievement profile read differs");
        await store.SaveAsync(snapshot, "B2", default);
        var raw = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Check(raw["campaign"]!["achievement"]!["stage"]!.GetValue<string>() == "B2" &&
            raw["extra"]!["value"]!.GetValue<int>() == 17, "Stage advance overwrote unrelated profile values");
        await Rejects<EngineProfileException>(() => store.SaveAsync(snapshot, null, default).AsTask());
        File.Copy(path, Path.Combine(root, "profiles/second.json"));
        var before = await File.ReadAllTextAsync(path);
        var other = workspace.CampaignStopStore("second", identity);
        await other.SaveAsync(await other.ReadAsync(default), null, default);
        Check(await File.ReadAllTextAsync(path) == before, "Stop mutated another instance");
        await store.SaveAsync(await store.ReadAsync(default), null, default);
        raw = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Check(raw["campaign"]!["achievement"]!["enabled"]!.GetValue<bool>() == false &&
            raw["campaign"]!["achievement"]!["stage"]!.GetValue<string>() == "B2",
            "Stop changed the wrong Engine achievement state");
        var changed = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        changed["device"]!["serial"] = "different-device";
        await File.WriteAllTextAsync(path, changed.ToJsonString());
        before = await File.ReadAllTextAsync(path);
        await Rejects<EngineProfileException>(() => store.SaveAsync(snapshot, "B3", default).AsTask());
        Check(await File.ReadAllTextAsync(path) == before, "Device identity conflict partially wrote profile state");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var otherSnapshot = await other.ReadAsync(default);
        await Rejects<OperationCanceledException>(() => other.SaveAsync(otherSnapshot, "B3", cancel.Token).AsTask());
    }
}
