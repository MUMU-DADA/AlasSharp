using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record FleetExchangeEvidence(int From, int To, long HealthSequence, bool InputCompleted);
public sealed record EmergencyRepairEvidence(long FrameSequence, long? HealthSequence, bool Confirmation,
    ImageStabilityEvidence? Stability);
public sealed record CombatHealthEvidence(IReadOnlyList<FleetExchangeEvidence> Exchanges,
    IReadOnlyList<EmergencyRepairEvidence> RepairClicks, int CapturedFrames);

/// <summary>HP balance and repair gates in native combat_preparation order; all decisions run in C#.</summary>
public sealed class CombatHealthPreparation(IUiDriver ui, Func<ScreenFrame> current, ImageStability stability,
    Func<FleetHealthSnapshot?> health, FleetHealthOptions options, bool fleetLock,
    Func<PixelPoint, PixelPoint, CancellationToken, ValueTask> drag)
{
    public static readonly SourceFile Source = CombatFlow.Source;
    private readonly List<FleetExchangeEvidence> _exchanges = [];
    private readonly List<EmergencyRepairEvidence> _repairs = [];
    private int _frames;
    public CombatHealthEvidence Evidence => new(_exchanges.ToArray(), _repairs.ToArray(), _frames);

    public async ValueTask BalanceAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = options.Weights();
        if (!options.UseHpBalance || fleetLock) return;
        var snapshot = health() ?? throw new InvalidOperationException("Read fleet HP before balancing");
        var order = FleetBalanceRules.ExpectedOrder(snapshot.Weighted.Skip(3).ToArray(), options);
        foreach (var (from, to) in FleetBalanceRules.ExchangeSteps(order, minitouch: false))
        {
            _exchanges.Add(new(from, to, snapshot.FrameSequence, false));
            await drag(FleetBalanceRules.ScoutPositions[from], FleetBalanceRules.ScoutPositions[to], token);
            _exchanges[^1] = _exchanges[^1] with { InputCompleted = true };
            await ui.DelayAsync(TimeSpan.FromSeconds(.5), token);
        }
    }

    public async ValueTask<bool> HandleRepairAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options.UseHpBalance || !options.UseEmergencyRepair) return false;
        if (await ui.AppearsAsync(UiAssets.Combat.EMERGENCY_REPAIR_CONFIRM, ButtonOffset.Vertical(30),
                interval: 3, threshold: 30, token: token))
        {
            await ui.ClickAsync(UiAssets.Combat.EMERGENCY_REPAIR_CONFIRM, token);
            _repairs.Add(new(current().Sequence, health()?.FrameSequence, true, null));
            return true;
        }
        if (!await ui.AppearsAsync(UiAssets.Combat.BATTLE_PREPARATION, ButtonOffset.Expand(20, 20), token: token) ||
            !await ui.AppearsAsync(UiAssets.Combat.EMERGENCY_REPAIR_AVAILABLE, token: token)) return false;
        while (true)
        {
            long previous = current().Sequence;
            await ui.ScreenshotAsync(token); _frames++;
            if (current().Sequence <= previous) throw new InvalidDataException("Repair reused a stale frame");
            if (!await ui.AppearsAsync(UiAssets.Combat.MAIN_FLEET_POWER_ZERO,
                    ButtonOffset.Expand(20, 20), token: token)) break;
        }
        var area = UiAssets.Combat.MAIN_FLEET_POWER_ZERO.For(ui.Server).Area
            ?? throw new InvalidDataException("Fleet power area is missing");
        var stable = await stability.WaitAsync(area.Area, token);
        _frames += stable.CapturedFrames;
        if (!await ui.AppearsAsync(UiAssets.Combat.EMERGENCY_REPAIR_AVAILABLE, token: token)) return false;
        var hp = health();
        if (hp is null || !FleetBalanceRules.NeedsEmergencyRepair(hp.Weighted, options)) return false;
        await ui.ClickAsync(UiAssets.Combat.EMERGENCY_REPAIR_AVAILABLE, token);
        ui.ClearInterval(UiAssets.Combat.EMERGENCY_REPAIR_CONFIRM);
        _repairs.Add(new(current().Sequence, hp.FrameSequence, false, stable));
        return true;
    }
}
