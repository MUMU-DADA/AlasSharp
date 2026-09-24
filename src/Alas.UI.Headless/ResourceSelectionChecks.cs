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
        Console.WriteLine("PASS: resource preferences persist immediately, preserve upstream order and isolate instances");
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); }
    private sealed class FailingStore : IResourceSelectionStore
    {
        public string? Read(string instance) => throw new IOException();
        public void Write(string instance, string json) => throw new IOException();
    }
}
