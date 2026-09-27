using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class EmotionChecks
{
    private static readonly FleetEmotionSettings Normal = new(EmotionControl.PreventGreenFace, EmotionRecovery.NotInDormitory, false);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static int[] Numbers(JsonNode node) => node.AsArray().Select(n => n!.GetValue<int>()).ToArray();
    private static DateTimeOffset Instant(double seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);
    private static double Seconds(DateTimeOffset at) => (at - DateTimeOffset.UnixEpoch).TotalSeconds;

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        string output = Path.Combine(artifacts, "native-emotion.json");
        var native = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_emotion_reference.py"), upstream, output], TimeSpan.FromSeconds(60));
        Check(native.ExitCode == 0, "Native emotion oracle failed: " + native.Error);
        var data = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        Check(data["source"]!.GetValue<string>() == EmotionRules.Source.Sha256, "Native emotion source drifted");
        Check(data["configSource"]!.GetValue<string>() == ConfigWorkspace.EmotionBindingSource.Sha256, "Native config binding source drifted");
        foreach (var sample in data["arithmetic"]!.AsArray())
        {
            var settings = new FleetEmotionSettings(sample!["control"]!.GetValue<string>() switch
            {
                "keep_exp_bonus" => EmotionControl.KeepExpBonus, "prevent_green_face" => EmotionControl.PreventGreenFace,
                "prevent_yellow_face" => EmotionControl.PreventYellowFace, _ => EmotionControl.PreventRedFace
            }, sample["recovery"]!.GetValue<string>() switch
            {
                "not_in_dormitory" => EmotionRecovery.NotInDormitory, "dormitory_floor_1" => EmotionRecovery.DormitoryFloor1,
                _ => EmotionRecovery.DormitoryFloor2
            }, sample["oath"]!.GetValue<bool>());
            var now = Instant(sample["now"]!.GetValue<double>());
            int value = EmotionRules.Recover(sample["value"]!.GetValue<int>(), Instant(sample["record"]!.GetValue<double>()), now, settings);
            Check(value == sample["current"]!.GetValue<int>() && settings.Speed == sample["speed"]!.GetValue<int>() &&
                settings.Maximum == sample["maximum"]!.GetValue<int>() && settings.Limit == sample["limit"]!.GetValue<int>(),
                "Recovery/clamping differs from native: " + sample);
            if (sample["recovered"] is null)
                await Rejects<ArgumentException>(() => Task.FromResult(EmotionRules.RecoveredAt(value, 2, now, settings)));
            else Check(Seconds(EmotionRules.RecoveredAt(value, sample["expected"]!.GetValue<int>(), now, settings)) ==
                sample["recovered"]!.GetValue<double>(), "Recovery time/floor division differs: " + sample);
        }
        foreach (var sample in data["traces"]!.AsArray())
        {
            var clock = new Clock(Instant(1700000040));
            var store = new MemoryStore(clock, 40, 44);
            var emotion = new CampaignEmotion(store, clock, (time, token) =>
            {
                token.ThrowIfCancellationRequested();
                store.Trace.Add(new("sleep", Seconds(clock.Now), [], null, time.TotalSeconds));
                clock.Now += time; return ValueTask.CompletedTask;
            });
            bool twice = sample!["double"]!.GetValue<bool>();
            var entry = await emotion.CheckEntryAsync(sample["battles"]!.GetValue<int>(),
                FleetRoles.Parse(sample["order"]!.GetValue<string>()), default, twice, sample["requested"]!.GetValue<bool>());
            Check((entry.DeferredUntil is not null) == sample["deferred"]!.GetValue<bool>(), "Entry delay differs");
            Compare(store.Trace, sample["entry"]!);
            store.Trace.Clear();
            await emotion.WaitAsync(1, default, twice); await emotion.ReduceAsync(1, 1, default, twice);
            await emotion.WaitAsync(2, default, twice); await emotion.ReduceAsync(2, 2, default, twice);
            Compare(store.Trace, sample["combat"]!);
            Check(emotion.TotalReduced == sample["totalReduced"]!.GetValue<int>() && emotion.Evidence.All(e => e.Saved),
                "Native wait/reduce evidence or total differs");
        }
        await StateChecksAsync();
        await ConfigChecksAsync(data["bindings"]!.AsArray(), artifacts);
        await ProductChecksAsync(python, upstream, artifacts);
        Console.WriteLine($"Emotion: {data["arithmetic"]!.AsArray().Count} native arithmetic cases, " +
            $"{data["traces"]!.AsArray().Count} entry/wait/reduce traces, {data["bindings"]!.AsArray().Count} native bindings; " +
            "persistence conflicts, cancellation, device isolation, queue/session and simulated ADB checks passed. No live-device result.");
    }

    private static void Compare(List<Trace> actual, JsonNode expected)
    {
        Check(actual.Count == expected.AsArray().Count, "Native record/sleep count differs");
        foreach (var (a, e) in actual.Zip(expected.AsArray()))
            Check(a.Operation == e!["operation"]!.GetValue<string>() && a.At == e["at"]!.GetValue<double>() &&
                (e["values"] is null || a.Values.SequenceEqual(Numbers(e["values"]!))) &&
                a.Target == e["target"]?.GetValue<double>() && a.Sleep == e["seconds"]?.GetValue<double>(),
                "Native record/wait timing differs: " + e);
    }

    private static async Task StateChecksAsync()
    {
        var clock = new Clock(Instant(1700000040));
        var store = new MemoryStore(clock, 0, 119);
        var emotion = new CampaignEmotion(store, clock);
        await emotion.ReduceAsync(1, 1, default);
        Check(store.State.Fleets[0].Value == -2 && emotion.TotalReduced == 2, "Reduction was clamped before next update");
        await emotion.ReduceAsync(1, 2, default);
        Check(store.State.Fleets[0].Value == -2 && store.State.Fleets[1].Value == 119, "Next update lost negative clamping or the other fleet");
        store.Fail = true;
        await Rejects<IOException>(() => emotion.ReduceAsync(2, 3, default).AsTask());
        Check(emotion.Evidence.Last() is { Operation: "reduce", Saved: false, Fleet: 2 } && emotion.TotalReduced == 4,
            "Failed persistence was reported as saved or erased partial evidence");
        store.Fail = false;
        var battle = emotion.ForBattle(2, () => 4);
        await battle.ReduceAsync(default);
        await Rejects<InvalidOperationException>(() => battle.ReduceAsync(default).AsTask());
        using var cancel = new CancellationTokenSource();
        emotion = new(store, clock, (_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; });
        await Rejects<OperationCanceledException>(() => emotion.WaitAsync(1, cancel.Token).AsTask());
        Check(emotion.Evidence is [{ Operation: "wait", Saved: true }], "Cancelled wait lost the pre-wait record");
        await Rejects<OperationCanceledException>(() => emotion.ReduceAsync(1, 5, cancel.Token).AsTask());
        Check(emotion.Evidence.Count == 1, "Cancelled pre-observation call incurred a cost");
        foreach (var mode in Enum.GetValues<CampaignEmotionMode>())
            Check(EmotionRules.ParseMode(mode.Name()) == mode && mode.Calculates() == mode.Name().Contains("calculate", StringComparison.Ordinal) &&
                mode.Ignores() == mode.Name().Contains("ignore", StringComparison.Ordinal), "Emotion mode differs from native");
        await Rejects<ArgumentException>(() => Task.FromResult(EmotionRules.ParseMode("invalid")));
    }

    private static JsonObject Defaults(int value = 119, DateTimeOffset? at = null)
    {
        var emotion = new JsonObject();
        for (int i = 1; i <= 2; i++)
        {
            string p = "Fleet" + i;
            emotion[p + "Value"] = value; emotion[p + "Record"] = Local(at ?? Instant(1700000040));
            emotion[p + "Control"] = "prevent_green_face"; emotion[p + "Recover"] = "not_in_dormitory"; emotion[p + "Oath"] = false;
        }
        return new JsonObject
        {
            ["Alas"] = new JsonObject { ["Emulator"] = new JsonObject { ["Serial"] = "offline-replay", ["PackageName"] = "org.example.game" } },
            ["Main"] = new JsonObject { ["Emotion"] = emotion,
                ["Scheduler"] = new JsonObject { ["NextRun"] = "2020-01-01 00:00:00" }, ["Unrelated"] = new JsonObject { ["Keep"] = 7 } }
        };
    }
    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static async Task<(ConfigWorkspace Workspace, string Root)> FixtureAsync(string artifacts, JsonObject template, JsonObject? raw = null)
    {
        string root = Path.Combine(artifacts, "config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "config/template.json"), template.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(root, "config/fixture.json"), (raw ?? new JsonObject { ["Alas"] = new JsonObject() }).ToJsonString());
        return (new(root), root);
    }

    private static async Task ConfigChecksAsync(JsonArray bindings, string artifacts)
    {
        foreach (var sample in bindings)
        {
            var template = sample!["data"]!.DeepClone().AsObject();
            string task = sample["task"]!.GetValue<string>();
            var defaults = Defaults();
            var fields = template[task]!["Emotion"]!.AsObject();
            foreach (var pair in defaults["Main"]!["Emotion"]!.AsObject())
                if (!fields.ContainsKey(pair.Key)) fields[pair.Key] = pair.Value!.DeepClone();
            template[task]!["Scheduler"] = defaults["Main"]!["Scheduler"]!.DeepClone();
            var (workspace, root) = await FixtureAsync(artifacts, template);
            var store = workspace.EmotionStore("fixture", task);
            var state = await store.ReadAsync(default);
            Check(state.Fleets.Select(f => f.Value).SequenceEqual(Numbers(sample["values"]!)), "Native field owner read differs");
            var now = Instant(1700000040.75);
            var saved = await store.SaveAsync(state, [17, 23], now, now.AddHours(2), default);
            var raw = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config/fixture.json")))!;
            for (int i = 0; i < 2; i++)
                Check(raw[sample["owners"]![i]!.GetValue<string>()]!["Emotion"]!["Fleet" + (i + 1) + "Value"]!.GetValue<int>() == (i == 0 ? 17 : 23),
                    "Emotion was saved to task instead of its native field owner");
            Check(saved.Fleets.All(f => f.RecordedAt == Instant(1700000040)) &&
                raw[task]!["Scheduler"]!["NextRun"]!.GetValue<string>() == Local(now.AddHours(2)), "Local second-level records or NextRun differ");
        }
        var setup = await FixtureAsync(artifacts, Defaults());
        var identity = new EmotionDeviceIdentity("offline-replay", "org.example.game", GameServer.Cn);
        var first = setup.Workspace.EmotionStore("fixture", "Main", identity);
        var other = new ConfigWorkspace(setup.Root).EmotionStore("fixture", "Main", identity);
        var expected = await first.ReadAsync(default);
        string path = Path.Combine(setup.Root, "config/fixture.json");
        var rawData = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        rawData["Extra"] = new JsonObject { ["Keep"] = 91 };
        await File.WriteAllTextAsync(path, rawData.ToJsonString());
        var savedState = await first.SaveAsync(expected, [100, 98], Instant(1700000041.999), null, default);
        Check(setup.Workspace.Get("fixture").Values["Extra"]!["Keep"]!.GetValue<int>() == 91 &&
            setup.Workspace.Get("fixture").Values["Main"]!["Unrelated"]!["Keep"]!.GetValue<int>() == 7, "Emotion save erased unrelated edits/defaults");
        await Rejects<ConfigWorkspaceException>(() => other.SaveAsync(expected, [80, 80], Instant(1700000042), null, default).AsTask());
        File.Copy(path, Path.Combine(setup.Root, "config/second.json"));
        var second = setup.Workspace.EmotionStore("second", "Main", identity);
        await second.SaveAsync(await second.ReadAsync(default), [75, 76], Instant(1700000043), null, default);
        Check((await first.ReadAsync(default)).Fleets.Select(f => f.Value).SequenceEqual([100, 98]), "Another instance changed emotion state");
        await Rejects<ConfigWorkspaceException>(() => setup.Workspace.EmotionStore("fixture", "Main", identity with { Serial = "different-device" }).ReadAsync(default).AsTask());
        rawData = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        rawData["Alas"]!["Emulator"] = new JsonObject { ["Serial"] = "changed-device", ["PackageName"] = identity.Package };
        await File.WriteAllTextAsync(path, rawData.ToJsonString());
        await Rejects<ConfigWorkspaceException>(() => first.SaveAsync(savedState, [90, 90], Instant(1700000044), null, default).AsTask());
        Check(JsonNode.Parse(await File.ReadAllTextAsync(path))!["Main"]!["Emotion"]!["Fleet1Value"]!.GetValue<int>() == 100,
            "Device identity conflict partially wrote records");
        await Rejects<ArgumentException>(() => Task.FromResult(setup.Workspace.EmotionStore("fixture", "../Main")));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var beforeCancel = await second.ReadAsync(default);
        await Rejects<OperationCanceledException>(() => second.SaveAsync(beforeCancel, [1, 1], Instant(1), null, cancel.Token).AsTask());
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed record Trace(string Operation, double At, int[] Values, double? Target = null, double? Sleep = null);
    private sealed class MemoryStore(Clock clock, int first, int second) : IEmotionStore
    {
        public EmotionSnapshot State { get; private set; } = new("0", [new(first, clock.Now, Normal), new(second, clock.Now, Normal)]);
        public bool Fail { get; set; }
        public List<Trace> Trace { get; } = [];
        public ValueTask<EmotionSnapshot> ReadAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(State); }
        public ValueTask<EmotionSnapshot> SaveAsync(EmotionSnapshot expected, ImmutableArray<int> values, DateTimeOffset recordedAt,
            DateTimeOffset? nextRun, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("Synthetic persistence failure");
            if (expected.Revision != State.Revision) throw new InvalidOperationException("Synthetic conflict");
            State = new((int.Parse(State.Revision, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture),
                State.Fleets.Select((f, i) => f with { Value = values[i], RecordedAt = recordedAt }).ToImmutableArray());
            Trace.Add(new("record", Seconds(recordedAt), values.ToArray()));
            if (nextRun is { } target) Trace.Add(new("delay", Seconds(recordedAt), [], Seconds(target)));
            return ValueTask.FromResult(State);
        }
    }
}
