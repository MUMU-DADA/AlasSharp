using Alas.Engine.Rules;

namespace Alas.Engine.Navigation;

/// <summary>Direct port of module/ui/ui.py UI.ui_page_appear. These are upstream rules, not inferred exceptions.</summary>
public sealed class PageAppearance(IUiDriver driver)
{
    public async ValueTask<bool> AppearsAsync(PageRule page, ButtonOffset? offset = null, double interval = 0,
        CancellationToken token = default)
    {
        var search = offset ?? ButtonOffset.Expand(30, 30);
        if (page.Id == "page_main")
        {
            if (await driver.AppearsAsync(UiAssets.UiWhite.MAIN_GOTO_CAMPAIGN_WHITE, search, interval, token: token)) return true;
            return await driver.AppearsAsync(UiAssets.Ui.MAIN_GOTO_FLEET, ButtonOffset.Expand(5, 5), interval, token: token);
        }
        if (driver.Server == GameServer.En && page.Id == "page_academy" &&
            await driver.AppearsAsync(UiAssets.Ui.ACADEMY_GOTO_MUNITIONS, search, interval, token: token)) return true;
        return page.Check is not null && await driver.AppearsAsync(page.Check, search, interval, token: token);
    }
}

/// <summary>Returns observations from one frame, without claiming navigation or running recovery actions.</summary>
public sealed class PageObserver(IUiDriver driver, PageGraph graph)
{
    public async ValueTask<IReadOnlyList<string>> ObserveAsync(CancellationToken token = default)
    {
        await driver.ScreenshotAsync(token);
        var appearance = new PageAppearance(driver);
        var matches = new List<string>();
        foreach (var page in graph.Pages)
            if (page.Check is not null && await appearance.AppearsAsync(page, token: token)) matches.Add(page.Id);
        return matches;
    }
}
