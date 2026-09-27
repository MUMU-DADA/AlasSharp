using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Device/vision composition for an already-entered map; business decisions remain in C#.</summary>
public interface ICampaignInMapHost
{
    ValueTask EnsureEmotionAsync(CampaignConfiguration configuration, CancellationToken token)
        => configuration.EmotionMode.Calculates() ? throw new NotSupportedException("Emotion state is unavailable") : ValueTask.CompletedTask;
    ValueTask<bool> VerifyInMapAsync(CancellationToken token);
    ValueTask EnsureFleetLockAsync(bool enabled, CancellationToken token);
    ValueTask<FleetSelection> PrepareInitialFleetAsync(CampaignConfiguration configuration, CancellationToken token);
    ValueTask InitializeHealthAsync(CampaignState state, int fleet, CampaignConfiguration configuration, CancellationToken token);
    ValueTask InitializeLevelsAsync(CampaignState state, int fleet, CampaignConfiguration configuration, CancellationToken token);
    ValueTask<CampaignWithdrawalEvidence> WithdrawAsync(string reason, CancellationToken token);
    ValueTask<IMapScanCamera> CreateCameraAsync(CampaignState state,
        CampaignConfiguration configuration, CancellationToken token);
    CampaignMapCombat CreateCombat(IMapScanCamera camera, CampaignConfiguration configuration);
}

public sealed record CampaignResumeResult(CampaignLoopExit Exit, int BattleCount, MapArrivalResult? StageReturn,
    FleetSelection? InitialFleet = null, IReadOnlyList<AmmoPickupEvidence>? AmmoPickups = null,
    IReadOnlyList<FleetHealthSnapshot>? Health = null, CampaignWithdrawalEvidence? Withdrawal = null,
    FleetLevelEvidence? Levels = null);
public interface ICampaignExecutionService
{
    ValueTask<CampaignResumeResult> ResumeInMapAsync(CampaignRule rule,
        CampaignConfiguration configuration, CancellationToken token);
}

/// <summary>Runs the compiled campaign loop after the user has completed stage and fleet preparation.</summary>
public sealed class InMapCampaignOperations(ICampaignInMapHost host, CampaignState state,
    CampaignConfiguration configuration, CancellationToken token) : ICampaignOperations
{
    private CampaignMapCombat? _combat;
    private bool _entered;
    public MapArrivalResult? StageReturn => _combat?.StageReturn;
    public FleetSelection? InitialFleet { get; private set; }
    public IReadOnlyList<AmmoPickupEvidence> AmmoPickups => _combat?.AmmoPickups ?? [];

    public ValueTask CheckEmotionAsync(int battles)
    {
        _ = configuration.Health.Weights();
        configuration.Levels.Validate();
        if (battles < 0) throw new ArgumentOutOfRangeException(nameof(battles));
        return host.EnsureEmotionAsync(configuration, token);
    }

    public async ValueTask EnterMapAsync()
    {
        if (!await host.VerifyInMapAsync(token))
            throw new InvalidDataException("Campaign resume requires an observed in-map page");
        _entered = true;
    }

    public ValueTask HandleFleetLockAsync()
    {
        if (!_entered) throw new InvalidOperationException("Verify the in-map page before initializing fleets");
        return host.EnsureFleetLockAsync(configuration.UseFleetLock, token);
    }

    public async ValueTask InitializeMapAsync(MapDefinition definition)
    {
        if (!_entered || !ReferenceEquals(state.Map, definition))
            throw new InvalidOperationException("Map initialization requires the verified campaign declaration");
        InitialFleet = await host.PrepareInitialFleetAsync(configuration, token);
        await host.InitializeHealthAsync(state, InitialFleet.LogicalIndex, configuration, token);
        await host.InitializeLevelsAsync(state, InitialFleet.LogicalIndex, configuration, token);
        var ready = await CampaignMapInitializer.InitializeAsync(state, configuration, InitialFleet,
            (map, ct) => host.CreateCameraAsync(map, configuration, ct), TimeSpan.FromMinutes(2), token);
        _combat = host.CreateCombat(ready.Camera, configuration);
    }

    private CampaignMapCombat Combat => _combat ??
        throw new InvalidOperationException("Campaign map has not been initialized");
    public ValueTask<bool> ClearEnemyAsync() => Combat.ClearEnemyAsync(token);
    public ValueTask<bool> ClearBossAsync() => Combat.ClearBossAsync(token);
    public ValueTask<bool> BruteClearBossAsync() => Combat.BruteClearBossAsync(token);
    public ValueTask<bool> BreakSirenCaughtAsync() => state.Cells.Any(grid => grid.IsCaughtBySiren)
        ? throw Missing("fleet siren rescue") : ValueTask.FromResult(false);
    public ValueTask<bool> ClearMysteriesAsync() => Combat.ClearMysteriesAsync(token);
    public ValueTask<bool> PickUpAmmoAsync() => Combat.PickUpAmmoAsync(token);
    public ValueTask<bool> ClearSirenAsync() => Combat.ClearSirenAsync(token);
    public ValueTask<bool> ClearAnyEnemyBySecondFleetCostAsync() => Combat.ClearAnyEnemyBySecondFleetCostAsync(token);
    public ValueTask<bool> ClearBouncingEnemyAsync() => state.Cells.Any(grid => grid.MayBouncingEnemy)
        ? throw Missing("bouncing enemy movement") : ValueTask.FromResult(false);
    public ValueTask<bool> ClearMechanismAsync() => state.Cells.Any(grid => grid.IsMechanismTrigger)
        ? throw Missing("land mechanism interaction") : ValueTask.FromResult(false);
    public ValueTask RefocusBossAsync((int X, int Y)? preset) => throw Missing("boss camera refocus");
    public ValueTask ResetLevelsAsync() => throw Missing("auto-search level reset");
    public ValueTask ReadLevelsAsync() => throw Missing("auto-search level read");
    public ValueTask AutoSearchMoveAsync() => throw Missing("auto-search movement");
    public ValueTask AutoSearchCombatAsync(int fleetIndex) => throw Missing("auto-search combat");
    public async ValueTask WithdrawAsync()
    {
        state.Withdrawal = await host.WithdrawAsync("campaign_error", token);
        throw new CampaignEndedException("Withdraw: campaign error");
    }

    private static NotSupportedException Missing(string operation)
        => new($"C# campaign resume has not ported {operation}");
}
