using System.Text.Json.Nodes;
using Alas.UI.Overview;
using Alas.UI.ViewModels;

namespace Alas.UI.Headless;

internal static class ResourceSelectionChecks
{
    public static void Run()
    {
        var store = new MemoryResourceSelectionStore();
        store.Write("one", "[\"Pt\",\"Oil\",\"Pt\",\"unknown\"]");
        var selection = new ResourceSelection(store);
        selection.SetInstance("one");
        Check(selection.Keys.SequenceEqual(new[] { "Pt", "Oil", "unknown" }), "deduplicate while preserving preference order and unknown resources");
        selection.Observe(JsonNode.Parse("""{"resources":[{"name":"Oil","value":42,"record":"2026-09-24T12:00:00"},{"name":"Coin","value":100,"record":"2026-09-24T12:00:00"}]}""")!.AsObject());
        Check(selection.Selected[0].Card.Value == "—" && selection.Selected[1].Card.Value == "42", "missing observations remain placeholder cards");
        selection.Add("Coin"); selection.Add("invented"); selection.Move("Coin", "Pt"); selection.Remove("unknown");
        Check(store.Read("one") == "[\"Coin\",\"Pt\",\"Oil\"]", "every edit persists immediately and no unobserved resource is invented");
        foreach (string key in selection.Keys.ToArray()) selection.Remove(key);
        var reload = new ResourceSelection(store); reload.SetInstance("one");
        Check(reload.Keys.Count == 0, "empty selection survives reopening");
        reload.SetInstance("two");
        Check(reload.Keys.SequenceEqual(ResourceCardSettingsPanel.DefaultKeys), "new instance uses upstream defaults");
        store.Write("bad", "[1]"); reload.SetInstance("bad");
        Check(reload.Keys.SequenceEqual(ResourceCardSettingsPanel.DefaultKeys), "malformed types use defaults");
        store.Write("broken", "["); reload.SetInstance("broken");
        Check(reload.Keys.Count == 4, "malformed JSON uses defaults");
        var failed = new ResourceSelection(new FailingStore()); failed.SetInstance("fail"); failed.Remove("Oil");
        Check(failed.Keys.Count == 3 && failed.StorageError.Length > 0, "storage denial preserves the current in-memory selection");

        store.Write("overview", "[\"Pt\",\"Oil\"]");
        var overview = new OverviewViewModel(resourceStore: store); overview.SetInstance("overview");
        overview.ApplyState(JsonNode.Parse("""{"active":{"status":"idle"},"overview":{"instance":"overview","resources":[{"name":"Oil","value":42,"record":"2026-09-24T12:00:00"}]}}""")!.AsObject());
        Check(overview.Resources.Count == 2 && overview.Resources[1].Value == "42", "overview consumes selected keys rather than a hardcoded list");
        overview.Selection.Move("Oil", "Pt");
        Check(overview.Resources[0].Value == "42", "selection order immediately updates rendered card data");
        overview.SetInstance("two");
        Check(overview.Resources.Count == 4 && overview.Resources.All(card => card.Value == "—"), "switching instance clears old observations while loading preferences");
        VerifyStableProjection();
        VerifyPreferenceNotifications();
        Console.WriteLine("PASS: resource preferences persist immediately, preserve upstream order and isolate instances");
    }

    private static void VerifyStableProjection()
    {
        var store = new MemoryResourceSelectionStore();
        store.Write("stable", "[\"Oil\",\"ActionPoint\",\"custom\"]");
        var overview = new OverviewViewModel(resourceStore: store);
        overview.SetInstance("stable");
        var state = JsonNode.Parse("""
            {"active":{"status":"idle"},"overview":{"instance":"stable","resources":[
              {"name":"Oil","value":42,"limit":100,"record":"2026-09-24T12:00:00"},
              {"name":"ActionPoint","value":20,"total":80,"record":"2026-09-24T12:00:00"},
              {"name":"custom","label":"自定义资源","value":7,"record":"2026-09-24T12:00:00"},
              {"name":"Coin","value":50,"record":"2026-09-24T12:00:00"}]}}
            """)!.AsObject();
        overview.ApplyState(state);
        var selection = overview.Selection;
        var selected = selection.Selected;
        var available = selection.Available;
        var cards = overview.Resources.ToArray();
        var events = new List<string?>();
        int collectionEvents = 0;
        selection.PropertyChanged += (_, args) => events.Add(args.PropertyName);
        overview.Resources.CollectionChanged += (_, _) => collectionEvents++;
        for (int i = 0; i < 5; i++) overview.ApplyState((JsonObject)state.DeepClone());
        overview.ApplyState(state);
        state["overview"]!["phase"] = "only unrelated metadata changed";
        overview.ApplyState(state);
        Check(ReferenceEquals(selection.Selected, selected) && ReferenceEquals(selection.Available, available)
            && cards.Zip(overview.Resources).All(pair => ReferenceEquals(pair.First, pair.Second))
            && events.Count == 0 && collectionEvents == 0,
            "equal, cloned and metadata-only snapshots retain resource identities without notifications");

        var rows = state["overview"]!["resources"]!.AsArray();
        void SelectedChange(Action change, Func<bool> expected, string label)
        {
            var previousSelected = selection.Selected;
            var previousAvailable = selection.Available;
            events.Clear(); collectionEvents = 0;
            change(); overview.ApplyState(state);
            Check(!ReferenceEquals(selection.Selected, previousSelected)
                && ReferenceEquals(selection.Available, previousAvailable)
                && events.SequenceEqual(new[] { nameof(ResourceSelection.Selected) })
                && collectionEvents == overview.Resources.Count + 1 && expected(), label);
        }
        SelectedChange(() => rows[0]!["value"] = 43d, () => overview.Resources[0].Value == "43",
            "in-place value changes update the selected cards once");
        SelectedChange(() => rows[0]!["limit"] = 200d, () => overview.Resources[0].Limit == "/ 200",
            "limit suffix changes remain visible");
        SelectedChange(() => rows[1]!["total"] = 90d, () => overview.Resources[1].Limit == "/ 总行动力 90",
            "action point total suffix changes remain visible");
        SelectedChange(() => rows[0]!["record"] = "2026-09-24T13:00:00", () => overview.Resources[0].Foot.EndsWith("13:00:00"),
            "record time changes update card footers");
        SelectedChange(() => rows[2]!["label"] = "新资源名称", () => overview.Resources[2].Name == "新资源名称",
            "unknown resource labels remain live");
        SelectedChange(() => rows[0]!["record"] = "2020-01-01T00:00:00", () => overview.Resources[0].Value == "—" && overview.Resources[0].Limit is null,
            "unrecorded resources clear displayed values and suffixes");

        selected = selection.Selected; available = selection.Available; cards = overview.Resources.ToArray();
        events.Clear(); collectionEvents = 0;
        rows[3]!["value"] = 51d; overview.ApplyState(state);
        Check(ReferenceEquals(selection.Selected, selected) && !ReferenceEquals(selection.Available, available)
            && selection.Available.Single().Card.Value == "51"
            && events.SequenceEqual(new[] { nameof(ResourceSelection.Available) }) && collectionEvents == 0,
            "unselected resource changes update only available choices");
        available = selection.Available; events.Clear();
        rows.Add(new JsonObject { ["name"] = "Gem", ["value"] = 5d, ["record"] = "2026-09-24T12:00:00" });
        overview.ApplyState(state);
        Check(ReferenceEquals(selection.Selected, selected) && !ReferenceEquals(selection.Available, available)
            && selection.Available.Select(choice => choice.Key).SequenceEqual(new[] { "Coin", "Gem" }) && collectionEvents == 0,
            "available membership changes do not rebuild selected cards");
        var coin = rows[3]; rows.RemoveAt(3); rows.Add(coin); overview.ApplyState(state);
        Check(selection.Available.Select(choice => choice.Key).SequenceEqual(new[] { "Gem", "Coin" }) && collectionEvents == 0,
            "available ordering follows the observation without rebuilding selected cards");

        events.Clear(); collectionEvents = 0;
        selection.Move("custom", "Oil");
        Check(overview.Resources[0].Name == "新资源名称" && overview.Resources[0].TintIndex == 0
            && overview.Resources[1].Name == "石油" && overview.Resources[1].TintIndex == 1
            && events.Contains(nameof(ResourceSelection.Selected)) && events.Contains(nameof(ResourceSelection.Keys))
            && collectionEvents == overview.Resources.Count + 1,
            "selection reordering updates rendered order and tint exactly once");
        selected = selection.Selected; collectionEvents = 0; events.Clear();
        overview.SetInstance("other");
        Check(!ReferenceEquals(selected, selection.Selected) && selection.Available.Count == 0
            && selection.Keys.SequenceEqual(ResourceCardSettingsPanel.DefaultKeys)
            && overview.Resources.Count == 4 && overview.Resources.All(card => card.Value == "—") && collectionEvents == 5,
            "instance switches discard previous observation and load new preferences once");
        overview.SetInstance("stable"); overview.ApplyState(state);
        Check(selection.Keys.SequenceEqual(new[] { "custom", "Oil", "ActionPoint" }) && overview.Resources[0].Value == "7",
            "returning to an instance restores its saved order and fresh observation");
        foreach (string key in selection.Keys.ToArray()) selection.Remove(key);
        selected = selection.Selected; available = selection.Available; events.Clear(); collectionEvents = 0;
        overview.ApplyState((JsonObject)state.DeepClone());
        Check(overview.Resources.Count == 0 && ReferenceEquals(selected, selection.Selected)
            && ReferenceEquals(available, selection.Available) && events.Count == 0 && collectionEvents == 0,
            "equal snapshots preserve an intentionally empty selection");
    }

    private static void VerifyPreferenceNotifications()
    {
        var store = new RecoveringStore();
        var overview = new OverviewViewModel(resourceStore: store);
        overview.SetInstance("saving");
        var selection = overview.Selection;
        var selected = selection.Selected;
        var events = new List<string?>();
        int collections = 0, saves = 0;
        selection.PropertyChanged += (_, args) => events.Add(args.PropertyName);
        selection.SelectionChanged += (_, _) => saves++;
        overview.Resources.CollectionChanged += (_, _) => collections++;
        store.FailWrites = true; selection.RestoreDefault();
        Check(store.Writes == 1 && saves == 1 && selection.StorageError.Length > 0
            && events.SequenceEqual(new[] { nameof(ResourceSelection.StorageError) })
            && ReferenceEquals(selected, selection.Selected) && collections == 0,
            "failed no-op preference saves still notify their error without rebuilding cards");
        events.Clear(); store.FailWrites = false; selection.RestoreDefault();
        Check(store.Writes == 2 && saves == 2 && selection.StorageError == ""
            && events.SequenceEqual(new[] { nameof(ResourceSelection.StorageError) }) && collections == 0,
            "successful retry persists preferences and clears the error even when cards are unchanged");
        events.Clear(); selection.RestoreDefault();
        Check(store.Writes == 3 && saves == 3 && events.Count == 0 && collections == 0,
            "no-op saves retain persistence and SelectionChanged semantics without false display notifications");
        events.Clear(); store.FailReads = true; overview.SetInstance("unreadable");
        Check(selection.StorageError.Length > 0 && events.Contains(nameof(ResourceSelection.StorageError)) && collections == 0,
            "instance read failures still notify errors when default cards are identical");
        events.Clear(); store.FailReads = false; overview.SetInstance("recovered");
        Check(selection.StorageError == "" && events.Contains(nameof(ResourceSelection.StorageError)) && collections == 0,
            "instance recovery clears read errors without unnecessary collection resets");
    }

    private static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); }
    private sealed class RecoveringStore : IResourceSelectionStore
    {
        private readonly MemoryResourceSelectionStore _memory = new();
        public bool FailWrites { get; set; }
        public bool FailReads { get; set; }
        public int Writes { get; private set; }
        public string? Read(string instance) => FailReads ? throw new IOException() : _memory.Read(instance);
        public void Write(string instance, string json)
        {
            Writes++;
            if (FailWrites) throw new IOException();
            _memory.Write(instance, json);
        }
    }
    private sealed class FailingStore : IResourceSelectionStore
    {
        public string? Read(string instance) => throw new IOException();
        public void Write(string instance, string json) => throw new IOException();
    }
}
