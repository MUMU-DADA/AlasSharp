using Alas.Engine.Rules;

namespace Alas.Engine.Navigation;

/// <summary>Domain recovery must be implemented in C#. There is deliberately no no-op or Python default.</summary>
public interface IUiRecovery
{
    ValueTask<bool> AdditionalAsync(bool getShip, CancellationToken token);
    ValueTask CheckUnknownPageAsync(bool checkApplication, bool checkOrientation, CancellationToken token);
    void ResetIntervalsAfterClick(AssetRule button);
}

public sealed record NavigationObservation(string Page, bool Switched);

/// <summary>Port of UI.ui_get_current_page/ui_goto/ui_ensure. Page parents are scoped to one navigation.</summary>
public sealed class UiNavigator(IUiDriver driver, PageGraph graph, IUiRecovery recovery)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PageAppearance _appearance = new(driver);
    private string? _current;

    public async ValueTask<NavigationObservation> EnsureAsync(string destination, TimeSpan timeout,
        bool skipFirstScreenshot = true, CancellationToken token = default)
    {
        var target = graph[destination];
        if (target.Check is null) throw new NotSupportedException("Navigation requires a recognizable destination");
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _gate.WaitAsync(token);
        using var deadline = new CancellationTokenSource(timeout, driver.Clock);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            _current = null;
            var current = await GetCurrentAsync(skipFirstScreenshot, limit.Token);
            if (current == destination) return new NavigationObservation(destination, false);
            var routes = graph.RoutesTo(destination);
            foreach (var page in graph.Pages)
                if (page.Check is not null) driver.ClearInterval(page.Check);
            bool skip = true, islandPageDetected = false;
            while (true)
            {
                limit.Token.ThrowIfCancellationRequested();
                driver.ClearOffset(UiAssets.Ui.GOTO_MAIN);
                if (!skip) await driver.ScreenshotAsync(limit.Token);
                skip = false;
                if (await _appearance.AppearsAsync(target, token: limit.Token))
                {
                    _current = destination;
                    return new NavigationObservation(destination, true);
                }
                bool clicked = false;
                foreach (var page in graph.Pages)
                {
                    if (page.Check is null || !routes.TryGetValue(page.Id, out var edge)) continue;
                    // Upstream uses the raw page checker here, not ui_page_appear's aliases.
                    if (!await driver.AppearsAsync(page.Check, ButtonOffset.Expand(30, 30), interval: 5, token: limit.Token)) continue;
                    _current = page.Id;
                    islandPageDetected = page.IsIsland || graph[edge.Destination].IsIsland;
                    await driver.ClickAsync(edge.Button, limit.Token);
                    recovery.ResetIntervalsAfterClick(edge.Button);
                    clicked = true;
                    break;
                }
                if (!clicked) await recovery.AdditionalAsync(!islandPageDetected, limit.Token);
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Navigation did not observe destination {destination}; last recognized page: {_current ?? "unknown"}", error);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<string> GetCurrentAsync(bool skipFirst, CancellationToken token)
    {
        var timeout = new IntervalTimer(driver.Clock, 10, count: 20);
        var orientation = new IntervalTimer(driver.Clock, 5);
        timeout.Reset();
        bool checkApplication = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!skipFirst || !driver.HasFrame) await driver.ScreenshotAsync(token);
            skipFirst = false;
            if (timeout.Reached()) throw new InvalidOperationException("Current game page is unknown");
            foreach (var page in graph.Pages)
                if (page.Check is not null && await _appearance.AppearsAsync(page, token: token))
                    return _current = page.Id;
            bool handled = false;
            foreach (var home in new[] { UiAssets.Ui.GOTO_MAIN, UiAssets.UiWhite.GOTO_MAIN_WHITE, UiAssets.Raid.RPG_HOME })
                if (await driver.AppearsAsync(home, ButtonOffset.Expand(30, 30), interval: 2, token: token))
                {
                    await driver.ClickAsync(home, token);
                    handled = true;
                    break;
                }
            if (handled || await recovery.AdditionalAsync(true, token)) { timeout.Reset(); continue; }
            bool checkOrientation = orientation.Reached();
            await recovery.CheckUnknownPageAsync(checkApplication, checkOrientation, token);
            checkApplication = false;
            if (checkOrientation) orientation.Reset();
        }
    }
}
