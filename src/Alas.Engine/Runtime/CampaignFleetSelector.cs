using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record FleetSelection(int LogicalIndex, int DisplayedIndex, int Clicks, long FrameSequence);

/// <summary>MapOperation's physical fleet number and logical mob/boss role mapping.</summary>
public static class FleetRoles
{
    public static readonly SourceFile BossSource = new("module/config/config.py",
        "fdaba7e77c5ffdca9e71a2ded80095a9ce061335d44a30861df9f9cd1963b854");
    public static int BossIndex(CampaignConfiguration configuration)
    {
        _ = Reversed(configuration);
        return configuration.Fleet2 != 0 && configuration.FleetOrder is
            FleetOrder.Fleet1MobFleet2Boss or FleetOrder.Fleet1BossFleet2Mob ? 2 : 1;
    }
    public static bool Reversed(CampaignConfiguration configuration)
    {
        if (!Enum.IsDefined(configuration.FleetOrder) || configuration.Fleet2 < 0)
            throw new ArgumentException("Invalid fleet order or second fleet");
        return configuration.Fleet2 != 0 && configuration.FleetOrder is
            FleetOrder.Fleet1BossFleet2Mob or FleetOrder.Fleet1StandbyFleet2All;
    }
    public static int LogicalIndex(int displayed, CampaignConfiguration configuration)
    {
        if (displayed is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(displayed));
        return Reversed(configuration) ? 3 - displayed : displayed;
    }
    public static string Name(FleetOrder order) => order switch
    {
        FleetOrder.Fleet1MobFleet2Boss => "fleet1_mob_fleet2_boss",
        FleetOrder.Fleet1BossFleet2Mob => "fleet1_boss_fleet2_mob",
        FleetOrder.Fleet1AllFleet2Standby => "fleet1_all_fleet2_standby",
        FleetOrder.Fleet1StandbyFleet2All => "fleet1_standby_fleet2_all",
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };
    public static FleetOrder Parse(string value) => value switch
    {
        "fleet1_mob_fleet2_boss" => FleetOrder.Fleet1MobFleet2Boss,
        "fleet1_boss_fleet2_mob" => FleetOrder.Fleet1BossFleet2Mob,
        "fleet1_all_fleet2_standby" => FleetOrder.Fleet1AllFleet2Standby,
        "fleet1_standby_fleet2_all" => FleetOrder.Fleet1StandbyFleet2All,
        _ => throw new ArgumentException("Unknown fleet order: " + value)
    };
}

/// <summary>Fleet selection uses native assets, role mapping, story/stage priority and retry timing.
/// Unlike native unknown/timeout assumptions, a successful selection requires a fresh observed number.</summary>
public sealed class CampaignFleetSelector(IUiDriver ui, IStoryHandler story,
    Func<CancellationToken, ValueTask<bool>> handleInStage, Func<long> frameSequence, Random? random = null)
{
    public static readonly SourceFile Source = MapUiRecovery.PreparationSource;
    private readonly Random _random = random ?? Random.Shared;

    public async ValueTask<FleetSelection> SelectAsync(int index, CampaignConfiguration configuration,
        TimeSpan timeout, CancellationToken token = default)
    {
        if (index is not (1 or 2) || index == 2 && configuration.Fleet2 == 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        _ = FleetRoles.Reversed(configuration);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds);
        var progress = new IntervalTimer(ui.Clock, 5, count: 10);
        limit.Reset(); progress.Reset();
        bool first = ui.HasFrame;
        int clicks = 0;
        long lastFrame = first ? frameSequence() : 0;
        if (first && lastFrame <= 0) throw new InvalidDataException("Fleet selection requires a numbered screenshot");
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (limit.Reached()) throw new TimeoutException("Fleet selection exceeded its time limit");
                if (!first)
                {
                    await ui.ScreenshotAsync(linked.Token);
                    if (frameSequence() <= lastFrame) throw new InvalidDataException("Fleet selection received a stale frame");
                    lastFrame = frameSequence();
                }
                first = false;
                if (progress.Reached()) throw new TimeoutException("Fleet number or switch did not reach the requested role");
                if (await story.StorySkipAsync(linked.Token)) { progress.Reset(); continue; }
                if (await handleInStage(linked.Token)) { progress.Reset(); continue; }
                int? displayed = await ReadDisplayedAsync(linked.Token);
                if (displayed is null) continue;
                int current = FleetRoles.LogicalIndex(displayed.Value, configuration);
                if (current == index) return new(current, displayed.Value, clicks, lastFrame);
                if (!await ui.AppearsAsync(UiAssets.Map.SWITCH_OVER, token: linked.Token)) continue;
                await ui.ClickAsync(UiAssets.Map.SWITCH_OVER, linked.Token);
                clicks++;
                await ui.DelayAsync(TimeSpan.FromSeconds(1 + _random.NextDouble() * .5), linked.Token);
                progress.Reset();
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Fleet selection exceeded its time limit", error); }
    }

    public async ValueTask<FleetSelection> InitializeAsync(CampaignConfiguration configuration,
        TimeSpan timeout, CancellationToken token = default)
    {
        // Preserve map_control_init -> handle_fleet_reverse -> fleet_set order.
        // A reversed setup may finish on logical fleet 2 when the first call
        // actually switched. Keep the observed role instead of relabeling it.
        if (FleetRoles.Reversed(configuration))
        {
            var reverse = await SelectAsync(2, configuration, timeout, token);
            if (reverse.Clicks > 0) return reverse;
        }
        return await SelectAsync(1, configuration, timeout, token);
    }

    private async ValueTask<int?> ReadDisplayedAsync(CancellationToken token)
    {
        if (await ui.AppearsAsync(UiAssets.Map.FLEET_NUM_1, ButtonOffset.Expand(20, 20), token: token)) return 1;
        if (await ui.AppearsAsync(UiAssets.Map.FLEET_NUM_2, ButtonOffset.Expand(20, 20), token: token)) return 2;
        return null;
    }
}
