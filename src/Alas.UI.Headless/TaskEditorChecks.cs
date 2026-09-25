using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Alas.UI.TaskEditor;

namespace Alas.UI.Headless;

/// <summary>
/// Offline contract checks for the shared task editor. The regular headless executable may invoke
/// Verify() from its own harness; this class deliberately has no device, window, or HTTP dependency.
/// </summary>
public static class TaskEditorChecks
{
    /// <summary>Must be called on the Avalonia UI thread inside a HeadlessUnitTestSession.</summary>
    public static void VerifyControls()
    {
        var backend = new FakeBackend();
        var model = new TaskEditorViewModel { Backend = backend, AutoSave = false };
        model.Load("instance-a", "Daily", Schema(), Config("instance-a", "r1", 1, "safe", "return true"));
        var view = new TaskEditorView { Model = model };
        var window = new Window { Width = 900, Height = 800, Content = view };
        window.Show();
        Pump();
        try
        {
            Click(window, Find<Button>(view, "ConfigJump_General"));
            var count = Find<TextBox>(view, "Field_Daily.General.Count");
            count.Focus(); count.SelectAll(); window.KeyTextInput("7"); Pump();
            Check(count.Text == "7", "real keyboard input updated field");
            Click(window, Find<Button>(view, "TaskConfigSave"));
            Check(backend.Saves == 1 && !model.HasChanges, "real save click submitted values");
            Click(window, Find<Button>(view, "TaskConfigRun"));
            Check(model.ConfirmRun, "real run click opened confirmation");
            Click(window, Find<Button>(view, "TaskConfigCancelRun"));
            Check(!model.ConfirmRun, "real cancel click closed confirmation");
            VerifyStorageVisibility();
        }
        finally { window.Close(); }
    }

    public static async Task Verify(CancellationToken cancellationToken = default)
    {
        var backend = new FakeBackend();
        var model = new TaskEditorViewModel { Backend = backend };
        model.AutoSave = false;
        var schema = new JsonObject
        {
            ["translations"] = new JsonObject
            {
                ["Task.Daily.name"] = "每日任务",
                ["General._info.name"] = "常规",
                ["General.Count.name"] = "数量",
                ["General.Count.help"] = "范围帮助",
                ["General.Mode.name"] = "模式",
                ["General.Script.name"] = "脚本",
            },
            ["menu"] = new JsonObject
            {
                ["Group"] = new JsonObject { ["page"] = "tool", ["tasks"] = new JsonArray("Daily") },
            },
            ["args"] = new JsonObject
            {
                ["Daily"] = new JsonObject
                {
                    ["Scheduler"] = new JsonObject { ["Command"] = new JsonObject { ["value"] = "Daily", ["display"] = "hide" } },
                    ["General"] = new JsonObject
                    {
                        ["Count"] = new JsonObject { ["type"] = "int", ["value"] = 1, ["validate"] = new JsonArray(1, 10) },
                        ["Mode"] = new JsonObject { ["type"] = "select", ["value"] = "safe", ["option"] = new JsonArray("safe", "fast") },
                        ["Script"] = new JsonObject { ["mode"] = "restricted_lua", ["type"] = "textarea", ["value"] = "return true" },
                    },
                },
            },
        };
        var config = Config("instance-a", "r1", 1, "safe", "return true");
        model.Load("instance-a", "Daily", schema, config);
        Check(model.Groups.Count == 2 && model.Fields.Count() == 4, "all schema fields loaded");
        var count = model.Fields.Single(f => f.Argument == "Count");
        count.SetText("bad");
        Check(!model.CanSave && count.Error.Length > 0, "invalid number blocks save");
        count.SetText("7");
        var mode = model.Fields.Single(f => f.Argument == "Mode");
        mode.SelectOption(mode.Options.Single(o => o.Label == "fast"));
        Check(model.CanSave, "valid number and option allow save");
        Check(await model.SaveAsync(cancellationToken), "save delegates to backend: " + model.Error + " / " + model.EditStatus);
        Check(backend.Saves == 1 && backend.LastChanges.Count == 2, "only changed fields are sent");
        var script = model.Fields.Single(f => f.Argument == "Script");
        script.SetText("return false");
        Check(model.CanRun == false && model.CanSave == false, "Lua draft is separate from ordinary save");
        await model.CheckScriptAsync(script, cancellationToken);
        Check(model.CanRun == false && model.CanSave == false, "Lua validation does not replace apply");
        Check(await model.ApplyScriptAsync(script, cancellationToken), "Lua apply uses a single config patch");
        model.RequestRun();
        Check(model.ConfirmRun, "run requires explicit confirmation");
        Check(await model.ConfirmRunAsync(cancellationToken) && backend.Runs == 1, "confirmed run delegates to backend");

        backend.ThrowConflict = true;
        count.SetText("8");
        Check(!await model.SaveAsync(cancellationToken) && model.HasConflicts, "backend conflict preserves draft");
        model.Fields.Single(f => f.HasConflict).ResolveConflict(true);
        Check(!model.HasConflicts && model.HasChanges, "conflict requires explicit resolution");

        var toolSchema = (JsonObject)schema.DeepClone();
        toolSchema["args"]!["Daily"]!.AsObject().Remove("Scheduler");
        var toolModel = new TaskEditorViewModel { Backend = backend, AutoSave = false };
        toolModel.Load("instance-a", "Daily", toolSchema, config);
        Check(toolModel.IsTool && toolModel.CanRun, "upstream tools without Scheduler.Command can be submitted");
        toolModel.RequestRun();
        Check(await toolModel.ConfirmRunAsync(cancellationToken) && backend.Runs == 2,
              "tool submission uses the same selected instance/task capability");
        toolSchema["menu"] = new JsonObject();
        toolModel.Load("instance-a", "Daily", toolSchema, config);
        Check(!toolModel.CanRun, "config-only pages cannot run");
    }

    private static void VerifyStorageVisibility()
    {
        var model = new TaskEditorViewModel { AutoSave = false };
        model.Load("instance-a", "Daily", StorageSchema(), StorageConfig("r1", false));
        var view = new TaskEditorView { Model = model };
        var window = new Window { Width = 900, Height = 800, Content = view };
        window.Show();
        Pump();
        try
        {
            var row = Find<Border>(view, "ConfigField_Daily.General.Stored");
            var card = Find<Border>(view, "ConfigGroup_General");
            var link = Find<Button>(view, "ConfigJump_General");
            Check(row.IsVisible && card.IsVisible && link.IsVisible, "non-empty storage is visible on initial load");

            var storage = model.Fields.Single(field => field.Argument == "Stored");
            storage.ClearStorage();
            Pump();
            Check(!row.IsVisible && !card.IsVisible && !link.IsVisible, "clearing storage hides its row, group and navigation");

            storage.Restore();
            Pump();
            Check(row.IsVisible && card.IsVisible && link.IsVisible, "restoring storage re-shows its row, group and navigation");

            model.Reconcile(StorageConfig("r2", true));
            Pump();
            Check(!row.IsVisible && !card.IsVisible && !link.IsVisible, "remote empty storage hides its row, group and navigation");

            model.Reconcile(StorageConfig("r3", false));
            Pump();
            Check(row.IsVisible && card.IsVisible && link.IsVisible, "remote non-empty storage re-shows its row, group and navigation");
        }
        finally { window.Close(); }
    }

    private static JsonObject StorageSchema() => new()
    {
        ["translations"] = new JsonObject
        {
            ["General._info.name"] = "常规", ["General.Stored.name"] = "记录",
        },
        ["menu"] = new JsonObject(),
        ["args"] = new JsonObject { ["Daily"] = new JsonObject {
            ["General"] = new JsonObject
            { ["Stored"] = new JsonObject { ["type"] = "storage", ["value"] = new JsonObject() } } } },
    };

    private static JsonObject StorageConfig(string revision, bool empty) => new()
    {
        ["instance"] = "instance-a", ["revision"] = revision,
        ["values"] = new JsonObject { ["Daily"] = new JsonObject { ["General"] = new JsonObject
        { ["Stored"] = empty ? new JsonObject() : new JsonObject { ["entry"] = "value" } } } },
    };

    private static JsonObject Config(string instance, string revision, int count, string mode, string script) => new()
    {
        ["instance"] = instance, ["revision"] = revision,
        ["values"] = new JsonObject { ["Daily"] = new JsonObject { ["General"] = new JsonObject
        { ["Count"] = count, ["Mode"] = mode, ["Script"] = script } } },
    };

    private static JsonObject Schema() => new()
    {
        ["translations"] = new JsonObject
        {
            ["Task.Daily.name"] = "每日任务", ["General._info.name"] = "常规",
            ["General.Count.name"] = "数量", ["General.Count.help"] = "范围帮助",
        },
        ["menu"] = new JsonObject(),
        ["args"] = new JsonObject { ["Daily"] = new JsonObject {
        ["Scheduler"] = new JsonObject { ["Command"] = new JsonObject { ["value"] = "Daily", ["display"] = "hide" } },
        ["General"] = new JsonObject
        { ["Count"] = new JsonObject { ["type"] = "int", ["value"] = 1, ["validate"] = new JsonArray(1, 10) } } } },
    };

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("TaskEditor: missing control " + name);
    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("TaskEditor: detached control " + control.Name);
        window.MouseMove(point); Pump(); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("TaskEditor: " + message); }

    private sealed class FakeBackend : ITaskEditorBackend
    {
        public int Saves { get; private set; }
        public int Runs { get; private set; }
        public bool ThrowConflict { get; set; }
        public IReadOnlyList<TaskFieldChange> LastChanges { get; private set; } = [];
        public Task<JsonObject> SaveAsync(string instance, string revision, IReadOnlyList<TaskFieldChange> changes, CancellationToken cancellationToken)
        {
            LastChanges = changes; Saves++;
            if (ThrowConflict) throw new TaskEditorConflictException(Config(instance, "remote", 2, "safe", "return true"));
            var countNode = changes.FirstOrDefault(c => c.Path.EndsWith(".Count"))?.Value;
            var count = countNode is null ? 1 : (int)double.Parse(countNode.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
            var mode = changes.FirstOrDefault(c => c.Path.EndsWith(".Mode"))?.Value?.GetValue<string>() ?? "safe";
            var script = changes.FirstOrDefault(c => c.Path.EndsWith(".Script"))?.Value?.GetValue<string>() ?? "return true";
            return Task.FromResult(Config(instance, "next", count, mode, script));
        }
        public Task RunAsync(string instance, string task, CancellationToken cancellationToken) { Runs++; return Task.CompletedTask; }
        public Task<ScriptValidation> ValidateScriptAsync(string instance, string task, string script, CancellationToken cancellationToken) =>
            Task.FromResult(new ScriptValidation(script.Contains("return", StringComparison.Ordinal), []));
    }
}
