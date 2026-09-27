using System.Text.Json.Nodes;
using Alas.UI.TaskEditor;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Alas.UI.Headless;

/// <summary>Offline checks for the Engine queue editor contract.</summary>
public static class TaskEditorChecks
{
    public static void VerifyControls()
    {
        var backend = new FakeBackend();
        var model = new TaskEditorViewModel { Backend = backend, AutoSave = false };
        model.LoadEngine("instance-a", "observe", "读取页面状态", new JsonObject());
        var view = new TaskEditorView { Model = model };
        var window = new Window { Width = 900, Height = 800, Content = view };
        window.Show();
        Pump();
        try
        {
            var input = Find<TextBox>(view, "Field_observe.Engine.Input");
            input.Focus(); input.SelectAll(); window.KeyTextInput("{\"count\":7}"); Pump();
            Check(model.HasChanges && model.CanSave, "Engine input accepts JSON edits");
            Click(window, Find<Button>(view, "TaskConfigSave"));
            Check(backend.Saves == 1 && !model.HasChanges, "save writes one Engine queue");
            Click(window, Find<Button>(view, "TaskConfigRun"));
            Check(model.ConfirmRun, "run requires explicit confirmation");
            Click(window, Find<Button>(view, "TaskConfigConfirmRun"));
            Check(backend.Runs == 1 && !model.ConfirmRun, "confirmed run delegates queue");
        }
        finally { window.Close(); }
    }

    public static Task Verify(CancellationToken cancellationToken = default)
    {
        var backend = new FakeBackend();
        var model = new TaskEditorViewModel { Backend = backend, AutoSave = false };
        model.LoadEngine("instance-a", "observe", "读取页面状态", new JsonObject());
        Check(model.Groups.Count == 1 && model.Fields.Single().Argument == "Input", "Engine model has one JSON input");
        model.Fields.Single().SetText("bad");
        Check(!model.CanSave && model.Fields.Single().Error.Length > 0, "invalid JSON blocks queue save");
        model.Fields.Single().SetText("{\"count\":9}");
        Check(model.CanSave, "valid JSON enables queue save");
        return SaveAndRunAsync(model, backend, cancellationToken);
    }

    private static async Task SaveAndRunAsync(TaskEditorViewModel model, FakeBackend backend, CancellationToken token)
    {
        Check(await model.SaveAsync(token), "queue save succeeds");
        Check(backend.Saves == 1 && !model.HasChanges, "saved input is accepted");
        model.RequestRun();
        Check(await model.ConfirmRunAsync(token) && backend.Runs == 1, "queue run succeeds");
    }

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
        public Task<JsonObject> SaveQueueAsync(string instance, string kind, JsonObject input, CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(new JsonObject { ["tasks"] = new JsonArray(new JsonObject
            { ["instance"] = instance, ["kind"] = kind, ["input"] = input.DeepClone() }) });
        }
        public Task RunQueueAsync(string instance, string kind, JsonObject input, CancellationToken cancellationToken)
        { Runs++; return Task.CompletedTask; }
    }
}
