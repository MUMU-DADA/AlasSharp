using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Tasks;

/// <summary>Direct port of module/freebies/data_key.py. No Python task or plan execution.</summary>
public sealed class DataKeyTask : ITaskRunner
{
    public static readonly SourceFile Source = new("module/freebies/data_key.py", "8fbc8dfa83a04d015dae2602f6eb85ce7f818e23a021dfc5101e9929cb2f13b5");
    public string Kind => "data_key";
    public bool RequiresActions => true;
    public void Validate(JsonObject? input) { TaskInput.Fields(input, "forceCollect"); _ = TaskInput.Boolean(input, "forceCollect"); }
    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities)
        => capabilities.HasOcrModels ? [] : ["ocr_models_unconfigured"];
    public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
    {
        bool force = TaskInput.Boolean(request.Input, "forceCollect");
        var ui = context.Driver;
        await context.Navigator.EnsureAsync("page_archives", context.Timeout, token: token);
        TaskResult result;
        if (await ui.AppearsAsync(UiAssets.Freebies.DATA_KEY_COLLECTED, ButtonOffset.Expand(20, 20), token: token))
            result = new(request.Id, Kind, TaskOutcome.Succeeded, "already_collected", new JsonObject { ["collectionExecuted"] = false });
        else
        {
            var observed = await ui.ReadTextAsync(new OcrRequest(UiAssets.Freebies.OCR_DATA_KEY.For(ui.Server).Area!.Value.Area,
                "azur_lane", OcrValues.CounterAlphabet, 255, 247, 247, 64), token);
            var count = OcrValues.Counter(observed.Text);
            var evidence = new JsonObject { ["inventoryText"] = observed.Text, ["inventoryFrame"] = observed.FrameSequence,
                ["current"] = count.Current.ToString(), ["remaining"] = count.Remaining.ToString(), ["total"] = count.Total.ToString(),
                ["forceCollect"] = force, ["collectionExecuted"] = false };
            if (!force && count.Remaining <= 0)
                result = new(request.Id, Kind, TaskOutcome.Skipped, count.Total == 0 ? "inventory_unreadable_or_zero" : "inventory_full", evidence);
            else
            {
                await CollectAsync(ui, context.Popups, token);
                evidence["collectionExecuted"] = true;
                evidence["meaning"] = "archives_and_collected_observed";
                result = new(request.Id, Kind, TaskOutcome.Succeeded, "collected", evidence);
            }
        }
        // Native run clears both checkers even when no collection was needed.
        ui.ClearInterval(UiAssets.Ui.WAR_ARCHIVES_CHECK);
        ui.ClearInterval(UiAssets.Ui.CAMPAIGN_MENU_CHECK);
        return result;
    }
    private static async ValueTask CollectAsync(IUiDriver ui, IPopupHandler popups, CancellationToken token)
    {
        bool skip = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!skip) await ui.ScreenshotAsync(token);
            skip = false;
            if (await ui.AppearsAsync(UiAssets.Freebies.DATA_KEY_COLLECT, ButtonOffset.Expand(20, 20), 3, threshold: 30, token: token))
            { await ui.ClickAsync(UiAssets.Freebies.DATA_KEY_COLLECT, token); continue; }
            if (await ui.AppearsAsync(UiAssets.Combat.GET_ITEMS_1, ButtonOffset.Vertical(20), 3, token: token))
            { await ui.ClickAsync(UiAssets.Freebies.DATA_KEY_COLLECT, token); continue; }
            if (await popups.ConfirmAsync(token)) continue;
            if (await ui.AppearsAsync(UiAssets.Ui.CAMPAIGN_MENU_GOTO_WAR_ARCHIVES, ButtonOffset.Expand(20, 20), 3, threshold: 30, token: token))
            { await ui.ClickAsync(UiAssets.Ui.CAMPAIGN_MENU_GOTO_WAR_ARCHIVES, token); continue; }
            if (await ui.AppearsAsync(UiAssets.Ui.WAR_ARCHIVES_CHECK, ButtonOffset.Expand(20, 20), token: token) &&
                await ui.AppearsAsync(UiAssets.Freebies.DATA_KEY_COLLECTED, ButtonOffset.Expand(20, 20), token: token)) return;
        }
    }
}
