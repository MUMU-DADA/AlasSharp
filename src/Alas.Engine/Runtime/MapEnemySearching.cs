using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record EnemySearchWaitResult(bool AnimationObserved, bool TimedOut, bool CombatLoading,
    int CapturedFrames, long FrameSequence);

/// <summary>EnemySearchingHandler's map wait. This observes animation, never a battle result.</summary>
public sealed class MapEnemySearching(IUiDriver ui, IStoryHandler story,
    Func<CancellationToken, ValueTask<bool>> handleInStage,
    Func<CancellationToken, ValueTask<bool>> guildPopup,
    Func<CancellationToken, ValueTask<bool>> urgentCommission,
    Func<CancellationToken, ValueTask<bool>> combatLoading, Func<long> frameSequence,
    Func<CancellationToken, ValueTask<bool>>? eventAnimation = null)
{
    public static readonly SourceFile Source = MapUiRecovery.StageSource;
    public static async ValueTask<bool> AppearsAsync(IUiDriver driver, CancellationToken token)
        => await driver.AppearsAsync(UiAssets.Handler.IN_MAP, token: token) &&
            await driver.AppearsAsync(UiAssets.Handler.MAP_ENEMY_SEARCHING, ButtonOffset.Expand(5, 5),
                preprocessing: TemplatePreprocessing.Luma, token: token);

    public async ValueTask<EnemySearchWaitResult> WaitAsync(TimeSpan timeout, CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: token))
            throw new InvalidDataException("Enemy-search waiting requires an observed map");
        using var deadline = new CancellationTokenSource(timeout, ui.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds); limit.Reset();
        var wait = new IntervalTimer(ui.Clock, 5);
        bool appeared = false;
        int frames = 0;
        long sequence = frameSequence();
        if (sequence <= 0) throw new InvalidDataException("Enemy-search waiting requires a numbered frame");
        async ValueTask Capture()
        {
            linked.Token.ThrowIfCancellationRequested();
            await ui.ScreenshotAsync(linked.Token);
            if (frameSequence() <= sequence) throw new InvalidDataException("Enemy-search waiting reused a stale frame");
            sequence = frameSequence(); frames++;
        }
        void Extend() { wait = new(ui.Clock, 10); wait.Reset(); }
        EnemySearchWaitResult Result(bool timedOut = false, bool loading = false) => new(appeared, timedOut, loading, frames, sequence);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (limit.Reached()) throw new TimeoutException("Enemy-search waiting exceeded its deadline");
                await Capture();
                if (eventAnimation is not null && await eventAnimation(linked.Token)) continue;
                if (await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: linked.Token)) { if (!wait.Started) wait.Reset(); }
                else wait.Reset();
                // Native stage handling throws CampaignEnd, which carries no settlement proof.
                if (await handleInStage(linked.Token)) return Result();
                if (await combatLoading(linked.Token)) return Result(loading: true);
                if (await ui.AppearsAsync(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT, ButtonOffset.Expand(250, 30), interval: 2, token: linked.Token))
                {
                    await ui.ClickAsync(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT, linked.Token);
                    ui.ResetInterval(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT);
                    Extend(); continue;
                }
                // Native handle_vote_popup is a no-op since 2023.
                if (await story.StorySkipAsync(linked.Token))
                { await story.EnsureNoStoryAsync(true, linked.Token); Extend(); }
                if (await guildPopup(linked.Token)) { Extend(); continue; }
                if (await urgentCommission(linked.Token)) { Extend(); continue; }
                if (await AppearsAsync(ui, linked.Token)) appeared = true;
                else if (appeared)
                {
                    await ui.DelayAsync(TimeSpan.FromSeconds(1.2), linked.Token);
                    await ui.DelayAsync(TimeSpan.FromSeconds(.3), linked.Token);
                    await Capture();
                    return Result();
                }
                if (wait.Reached()) return Result(timedOut: true);
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Enemy-search waiting exceeded its deadline", error); }
    }
}

/// <summary>Combat.is_combat_loading's luma match within the native loading strip.</summary>
public sealed class CombatLoadingProbe(IVision vision, AssetFiles assets, GameServer server, Func<ScreenFrame> frame)
{
    public static readonly SourceFile Source = MapEncounterProbe.CombatSource;
    public async ValueTask<bool> ObserveAsync(CancellationToken token)
    {
        var captured = frame();
        var variant = UiAssets.Template.TEMPLATE_COMBAT_LOADING.For(server);
        var png = await assets.ReadAsync(variant, token);
        var found = await vision.MatchAsync(captured, new(png, new(0, 620, 1280, 70), .85,
            Preprocessing: TemplatePreprocessing.Luma), token);
        if (found.FrameSequence != captured.Sequence || !double.IsFinite(found.Similarity))
            throw new InvalidDataException("Combat-loading match returned an invalid frame or score");
        return found.Similarity > .85;
    }
}
