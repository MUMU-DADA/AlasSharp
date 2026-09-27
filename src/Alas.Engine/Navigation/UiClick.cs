using Alas.Engine.Rules;

namespace Alas.Engine.Navigation;

/// <summary>Native UI.ui_click without an additional handler, including time/access-count retry gates.</summary>
public static class UiClick
{
    public static readonly SourceFile Source = UiRecovery.UiSource;
    public static async ValueTask UntilAsync(IUiDriver ui, AssetRule click,
        Func<CancellationToken, ValueTask<bool>> check, ButtonOffset offset, double retrySeconds,
        CancellationToken token)
    {
        var retry = new IntervalTimer(ui.Clock, retrySeconds, (int)(retrySeconds / .5));
        var confirm = new IntervalTimer(ui.Clock, 0);
        confirm.Reset();
        bool first = ui.HasFrame;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!first) await ui.ScreenshotAsync(token);
            first = false;
            if (await check(token)) { if (confirm.Reached()) return; }
            else confirm.Reset();
            if (retry.Reached() && await ui.AppearsAsync(click, offset, token: token))
            { await ui.ClickAsync(click, token); retry.Reset(); }
        }
    }
}
