using System.Collections.Immutable;
using Alas.Engine.Rules;

namespace Alas.Engine.Navigation;

public sealed record UiSettingOption(string Name, Rectangle Area, AssetRule? Asset = null);
public sealed record UiSettingGroup(string Name, string Default, ImmutableArray<UiSettingOption> Options);

/// <summary>Native Setting defaults and multi-selection. A timeout never authorizes a destructive consumer.</summary>
public sealed class UiSetting(IUiDriver ui, ImmutableArray<UiSettingGroup> groups,
    Func<UiSettingOption, CancellationToken, ValueTask<bool>> active, bool resetFirst = true, bool needDeselect = false)
{
    public static readonly SourceFile Source = new("module/ui/setting.py",
        "e4fee63c405176900fa9131ef481b0e09eb7eaf4ccb055422f2207fd66de69b3");
    public async ValueTask SetAsync(IReadOnlyDictionary<string, IReadOnlyList<string>?> required, CancellationToken token)
    {
        foreach (var (name, choices) in required)
        {
            var group = groups.SingleOrDefault(g => g.Name == name) ?? throw new ArgumentException("Unknown setting group");
            if (choices is not null && choices.Any(choice => !group.Options.Any(option => option.Name == choice)))
                throw new ArgumentException("Unknown setting option");
        }
        if (resetFirst) await ExecuteAsync(new Dictionary<string, IReadOnlyList<string>?>(), token);
        await ExecuteAsync(required, token);
    }

    private async ValueTask ExecuteAsync(IReadOnlyDictionary<string, IReadOnlyList<string>?> required, CancellationToken token)
    {
        var desired = groups.SelectMany(group =>
        {
            IReadOnlyList<string>? options = required.TryGetValue(group.Name, out var values) ? values : [group.Default];
            return options is null ? [] : group.Options.Select(option => (Option: option, Enabled: options.Contains(option.Name)));
        }).ToArray();
        var timeout = new IntervalTimer(ui.Clock, 10, 20);
        var retry = new IntervalTimer(ui.Clock, 1, 2);
        timeout.Reset();
        bool first = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!first) await ui.ScreenshotAsync(token);
            first = false;
            if (timeout.Reached()) throw new TimeoutException("Setting options were not confirmed");
            // Native show_active_buttons is logging only. Do not double the CV requests.
            var clicks = new List<UiSettingOption>();
            foreach (var (option, enabled) in desired)
            {
                // Inactive options cannot affect this pass when deselection is disabled.
                if (!enabled && !needDeselect) continue;
                bool observed = await active(option, token);
                if (enabled && !observed || needDeselect && !enabled && observed) clicks.Add(option);
            }
            if (clicks.Count == 0) return;
            if (!retry.Reached()) continue;
            foreach (var option in clicks)
                if (option.Asset is { } asset) await ui.ClickAsync(asset, token);
                else await ui.ClickAreaAsync(option.Area, token);
            retry.Reset();
        }
    }
}
