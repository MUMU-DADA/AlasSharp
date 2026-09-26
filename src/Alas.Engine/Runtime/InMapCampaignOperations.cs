using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Device/vision composition for an already-entered map; business decisions remain in C#.</summary>
public interface ICampaignInMapHost
{
    ValueTask<bool> VerifyInMapAsync(CancellationToken token);
    ValueTask<IMapScanCamera> CreateCameraAsync(CampaignState state,
        CampaignConfiguration configuration, CancellationToken token);
    CampaignMapCombat CreateCombat(IMapScanCamera camera, CampaignConfiguration configuration);
}

public sealed record CampaignResumeResult(CampaignLoopExit Exit, int BattleCount, MapArrivalResult? StageReturn);
public interface ICampaignExecutionService
{
    ValueTask<CampaignResumeResult> ResumeInMapAsync(CampaignRule rule, CancellationToken token);
}

/// <summary>Runs the compiled campaign loop after the user has completed stage and fleet preparation.</summary>
public sealed class InMapCampaignOperations(ICampaignInMapHost host, CampaignState state,
    CampaignConfiguration configuration, CancellationToken token) : ICampaignOperations
{
    private CampaignMapCombat? _combat;
    private bool _entered;
    public MapArrivalResult? StageReturn => _combat?.StageReturn;

    public ValueTask CheckEmotionAsync(int battles)
    {
        if (battles < 0) throw new ArgumentOutOfRangeException(nameof(battles));
        // This entry mode starts after the user has completed emotion and fleet preparation.
        return ValueTask.CompletedTask;
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
        return ValueTask.CompletedTask;
    }

    public async ValueTask InitializeMapAsync(MapDefinition definition)
    {
        if (!_entered || !ReferenceEquals(state.Map, definition))
            throw new InvalidOperationException("Map initialization requires the verified campaign declaration");
        var ready = await CampaignMapInitializer.InitializeAsync(state, configuration,
            (map, ct) => host.CreateCameraAsync(map, configuration, ct), TimeSpan.FromMinutes(2), token);
        _combat = host.CreateCombat(ready.Camera, configuration);
    }

    private CampaignMapCombat Combat => _combat ??
        throw new InvalidOperationException("Campaign map has not been initialized");
    public ValueTask<bool> ClearEnemyAsync() => Combat.ClearEnemyAsync(token);
    public ValueTask<bool> ClearBossAsync() => Combat.ClearBossAsync(token);
    public ValueTask<bool> BruteClearBossAsync() => throw Missing("brute boss search");
    public ValueTask<bool> BreakSirenCaughtAsync() => state.Cells.Any(grid => grid.IsCaughtBySiren)
        ? throw Missing("fleet siren rescue") : ValueTask.FromResult(false);
    public ValueTask<bool> ClearMysteriesAsync() => Combat.ClearMysteriesAsync(token);
    public ValueTask<bool> PickUpAmmoAsync() => state.AmmoCount > 0 && state.Cells.Any(grid => grid.IsAmmo)
        ? throw Missing("ammo pickup") : ValueTask.FromResult(false);
    public ValueTask<bool> ClearSirenAsync() => state.Cells.Any(grid => grid.IsSiren || grid.IsFortress)
        ? throw Missing("siren and fortress targeting") : ValueTask.FromResult(false);
    public ValueTask<bool> ClearAnyEnemyBySecondFleetCostAsync() => throw Missing("movable enemy second-fleet targeting");
    public ValueTask<bool> ClearBouncingEnemyAsync() => state.Cells.Any(grid => grid.MayBouncingEnemy)
        ? throw Missing("bouncing enemy movement") : ValueTask.FromResult(false);
    public ValueTask<bool> ClearMechanismAsync() => state.Cells.Any(grid => grid.IsMechanismTrigger)
        ? throw Missing("land mechanism interaction") : ValueTask.FromResult(false);
    public ValueTask RefocusBossAsync((int X, int Y)? preset) => throw Missing("boss camera refocus");
    public ValueTask ResetLevelsAsync() => throw Missing("auto-search level reset");
    public ValueTask ReadLevelsAsync() => throw Missing("auto-search level read");
    public ValueTask AutoSearchMoveAsync() => throw Missing("auto-search movement");
    public ValueTask AutoSearchCombatAsync(int fleetIndex) => throw Missing("auto-search combat");
    public ValueTask WithdrawAsync() => throw Missing("campaign withdrawal");

    private static NotSupportedException Missing(string operation)
        => new($"C# campaign resume has not ported {operation}");
}
