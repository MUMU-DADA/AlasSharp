using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>One sortie's strategy state. Icons and branching are from StrategyHandler, not map-specific overrides.</summary>
public sealed class CampaignStrategy(IUiDriver ui, Func<CancellationToken, ValueTask<FleetFormation?>> observeBuff)
{
    public static readonly SourceFile Source = new("module/handler/strategy.py",
        "0541ce24c093dfd0e74cd2ebb53ec4f007b1b8e0628dfb086f4d4a935733564a");
    private readonly HashSet<int> _fixed = [];
    private readonly UiSwitch _formation = new(ui,
        [new("line_ahead", UiAssets.Handler.FORMATION_1), new("double_line", UiAssets.Handler.FORMATION_2),
         new("diamond", UiAssets.Handler.FORMATION_3)], ButtonOffset.Expand(100, 200));
    private readonly UiSwitch _view = new(ui,
        [new("on", UiAssets.Handler.SUBMARINE_VIEW_ON), new("off", UiAssets.Handler.SUBMARINE_VIEW_OFF)],
        ButtonOffset.Expand(100, 200));
    private readonly UiSwitch _hunt = new(ui,
        [new("on", UiAssets.Handler.SUBMARINE_HUNT_ON), new("off", UiAssets.Handler.SUBMARINE_HUNT_OFF)],
        ButtonOffset.Expand(200, 200));

    public async ValueTask<bool> EnsureAsync(int fleetIndex, CampaignConfiguration configuration,
        TimeSpan timeout, CancellationToken token = default)
    {
        if (fleetIndex is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(fleetIndex));
        var expected = fleetIndex == 1 ? configuration.Fleet1Formation : configuration.Fleet2Formation;
        string formation = FormationName(expected);
        if (!Enum.IsDefined(configuration.SubmarineMode) || configuration.Submarine < 0)
            throw new ArgumentException("Invalid submarine strategy", nameof(configuration));
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        token.ThrowIfCancellationRequested();
        if (_fixed.Contains(fleetIndex)) return false;
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds);
        limit.Reset();
        try
        {
            if (!ui.HasFrame) await ui.ScreenshotAsync(linked.Token);
            if (await observeBuff(linked.Token) == expected && configuration.Submarine == 0)
            { _fixed.Add(fleetIndex); return false; }
            await OpenAsync(limit, linked.Token);
            await _formation.SetAsync(formation, timeout, token: linked.Token);
            if (await _view.ReadAsync(linked.Token) is not null)
                await _view.SetAsync("off", timeout, token: linked.Token);
            if (await _hunt.ReadAsync(linked.Token) is not null)
            {
                bool hunt = configuration.Submarine != 0 && configuration.SubmarineMode is
                    SubmarineMode.HuntOnly or SubmarineMode.HuntAndBoss;
                await _hunt.SetAsync(hunt ? "on" : "off", timeout, token: linked.Token);
            }
            await CloseAsync(limit, linked.Token);
            _fixed.Add(fleetIndex);
            return true;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Campaign strategy did not finish", error); }
    }

    private async ValueTask OpenAsync(IntervalTimer limit, CancellationToken token)
    {
        bool first = true;
        while (true)
        {
            CheckLimit(limit, token);
            if (!first) await ui.ScreenshotAsync(token);
            first = false;
            if (await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED, ButtonOffset.Vertical(200), token: token)) return;
            if (await ui.AppearsAsync(UiAssets.Handler.IN_MAP, interval: 5, token: token) &&
                !await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED, ButtonOffset.Vertical(200), token: token))
            { await ui.ClickAsync(UiAssets.Handler.STRATEGY_OPEN, token); continue; }
            if (await ui.AppearsAsync(UiAssets.Combat.GET_ITEMS_1, ButtonOffset.Vertical(5), token: token))
                await ui.ClickAsync(UiAssets.Combat.GET_ITEMS_1, token);
        }
    }

    private async ValueTask CloseAsync(IntervalTimer limit, CancellationToken token)
    {
        bool first = true;
        while (true)
        {
            CheckLimit(limit, token);
            if (!first) await ui.ScreenshotAsync(token);
            first = false;
            if (await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED, ButtonOffset.Vertical(200), interval: 5, token: token))
            { await ui.ClickAsync(UiAssets.Handler.STRATEGY_OPENED, token); continue; }
            if (!await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED, ButtonOffset.Vertical(200), token: token)) return;
        }
    }

    private static void CheckLimit(IntervalTimer limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (limit.Reached()) throw new TimeoutException("Campaign strategy did not finish");
    }

    public static string FormationName(FleetFormation formation) => formation switch
    {
        FleetFormation.LineAhead => "line_ahead", FleetFormation.DoubleLine => "double_line",
        FleetFormation.Diamond => "diamond", _ => throw new ArgumentOutOfRangeException(nameof(formation))
    };

    public static FleetFormation ParseFormation(string value) => value switch
    {
        "line_ahead" => FleetFormation.LineAhead, "double_line" => FleetFormation.DoubleLine,
        "diamond" => FleetFormation.Diamond, _ => throw new ArgumentException("Unknown fleet formation: " + value)
    };
}

/// <summary>Numeric template matching only; the native priority and formation selection stay in C#.</summary>
public sealed class MapFormationProbe(IVision vision, AssetFiles assets, GameServer server, Func<ScreenFrame> currentFrame)
{
    public async ValueTask<FleetFormation?> ObserveAsync(CancellationToken token = default)
    {
        var frame = currentFrame();
        var area = UiAssets.Handler.MAP_BUFF.For(server).Area ?? throw new InvalidDataException("Missing map buff area");
        foreach (var (template, formation) in new[]
        {
            (UiAssets.Template.TEMPLATE_FORMATION_2, FleetFormation.DoubleLine),
            (UiAssets.Template.TEMPLATE_FORMATION_1, FleetFormation.LineAhead),
            (UiAssets.Template.TEMPLATE_FORMATION_3, FleetFormation.Diamond)
        })
        {
            var result = await vision.MatchAsync(frame, new(await assets.ReadAsync(template.For(server), token),
                area.Area, .85), token);
            if (result.FrameSequence != frame.Sequence) throw new InvalidDataException("Formation belongs to another frame");
            if (result.Matched) return formation;
        }
        return null;
    }
}
