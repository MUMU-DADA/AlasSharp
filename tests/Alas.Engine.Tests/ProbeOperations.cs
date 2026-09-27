using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal sealed record Scenario(string Rule, string Operation = "dispatch", int BattleCount = 0,
    bool Poor = false, bool ClearAll = false, bool Movable = false, string Cells = "empty",
    string? TrueOperation = null, bool CombatReturn = true, bool HandleError = false,
    bool AutoSearch = false, string? Signal = null, int SignalCount = 1, string SignalOperation = "clear_enemy",
    bool Advance = false, bool Accessible = true, string? BossCells = null,
    int Fleet2 = 0, int? BossFleet = null, string? FirstFleet = null, string? SecondFleet = null, int FirstScale = 0, string? Mysteries = null,
    int MysteryCount = 0, int CollectedMysteries = 0);
internal sealed record ProbeResult(string[] Calls, object? Value, string? Exception, int BattleCount, double[]? Weights);

/// <summary>Synthetic terminal actions only; the compiled rule and native oracle each own their control flow.</summary>
internal sealed class ProbeOperations(Scenario scenario) : ICampaignOperations
{
    public CampaignState State { get; set; } = null!;
    public List<string> Calls { get; } = [];
    private int _signals;
    private bool Call(string operation)
    {
        Calls.Add(operation);
        if (operation == scenario.SignalOperation && _signals++ < scenario.SignalCount)
        {
            if (scenario.Signal == "moved") throw new MapEnemyMovedException();
            if (scenario.Signal == "moved_after_battle") { State.BattleCount++; throw new MapEnemyMovedException(); }
            if (scenario.Signal == "ended") throw new CampaignEndedException();
            if (scenario.Signal == "error") throw new IOException("synthetic device failure");
        }
        bool combat = operation is "clear_enemy" or "clear_boss" or "brute_clear_boss";
        if (combat && scenario.Advance) State.BattleCount++;
        return operation == scenario.TrueOperation || (combat && scenario.CombatReturn);
    }
    private ValueTask<bool> Result(string operation) => ValueTask.FromResult(Call(operation));
    private ValueTask Void(string operation) { Call(operation); return ValueTask.CompletedTask; }
    public ValueTask<bool> ClearEnemyAsync() => Result("clear_enemy");
    public ValueTask<bool> ClearFilterEnemyAsync(EnemyFilter filter, int preserve = 0)
    { Call($"enemy_filter:{filter.Expression}:{preserve}"); return Result("clear_filter_enemy"); }
    public ValueTask<bool> ClearEnemyAsync(EnemySelection selection) { Selection(selection); return ClearEnemyAsync(); }
    private void Selection(EnemySelection selection)
        => Call($"enemy_selection:{string.Join(',', selection.Scales.IsDefault ? [] : selection.Scales)}:{(selection.Strongest ? 1 : 0)}:{(selection.Weakest ? 1 : 0)}");
    public ValueTask SwitchFleetAsync(int fleet) { State.FleetIndex = fleet; return Void("fleet_boss:" + fleet); }
    private void Roads(IReadOnlyList<RoadDefinition> roads)
        => Call("roads:" + string.Join('|', roads.Select(road => string.Join('/', road.Groups.Select(group =>
            string.Join(',', group.Select(cell => cell.ToString()).Distinct().Order(StringComparer.Ordinal)))))));
    public ValueTask<bool> PushSecondFleetForwardAsync() => Result("fleet_2_push_forward");
    public ValueTask<bool> PositionSecondFleetAsync(IReadOnlyList<Cell> cells, IReadOnlyList<RoadDefinition> roads)
    {
        Call("step_on:" + string.Join(',', cells));
        Roads(roads);
        return Result("fleet_2_step_on");
    }
    public ValueTask<bool> RescueSecondFleetAsync(Cell destination) => Result("fleet_2_rescue:" + destination);
    public bool CheckAccessibility(Cell cell, int? fleet = null)
    { Call($"check_access:{cell}:{fleet}"); return scenario.Accessible; }
    public async ValueTask<bool> ClearBossForFleetAsync(int fleet)
    { await Void("fleet_boss:" + fleet); return await ClearBossAsync(); }
    public ValueTask<bool> ClearRoadblocksAsync(IReadOnlyList<RoadDefinition> roads, bool potential = false)
    {
        Roads(roads);
        return Result(potential ? "clear_potential_roadblocks" : "clear_roadblocks");
    }
    public ValueTask<bool> ClearRoadblocksAsync(IReadOnlyList<RoadDefinition> roads, EnemySelection selection, bool potential = false)
    { Roads(roads); Selection(selection); return Result(potential ? "clear_potential_roadblocks" : "clear_roadblocks"); }
    public ValueTask<bool> ClearBossAsync() => Result("clear_boss");
    public ValueTask<bool> ClearFirstRoadblocksAsync(IReadOnlyList<RoadDefinition> roads)
    { Roads(roads); return Result("clear_first_roadblocks"); }
    public ValueTask<bool> BruteClearBossAsync() => Result("brute_clear_boss");
    public ValueTask<bool> BreakSirenCaughtAsync() => Result("fleet_2_break_siren_caught");
    public ValueTask<bool> ClearMysteriesAsync()
    {
        bool result = Call("clear_all_mystery");
        State.MysteryCount += scenario.CollectedMysteries;
        return ValueTask.FromResult(result);
    }
    public ValueTask<bool> ClearMysteriesAsync(IReadOnlyList<Cell>? ignore, bool nearby = false)
    { Call($"mystery_selection:{(nearby ? 1 : 0)}:{(ignore is null ? "null" : string.Join(',', ignore))}"); return ClearMysteriesAsync(); }
    public ValueTask ClearMysteryAsync(Cell destination)
    {
        Call("chosen_mystery:" + destination);
        if (State.FleetIndex == 2) State.Fleet2Location = destination; else State.Fleet1Location = destination;
        State[destination].IsMystery = false;
        return ValueTask.CompletedTask;
    }
    public ValueTask<bool> PickUpAmmoAsync() => Result("pick_up_ammo");
    public ValueTask<bool> ClearSirenAsync() => Result("clear_siren");
    public ValueTask<bool> ClearAnyEnemyBySecondFleetCostAsync() => Result("clear_any_enemy:cost_2");
    public ValueTask<bool> ClearBouncingEnemyAsync() => Result("clear_bouncing_enemy");
    public ValueTask<bool> ClearMechanismAsync(IReadOnlyList<Cell>? grids = null) => Result("clear_mechanism");
    public ValueTask RefocusBossAsync((int X, int Y)? preset) => Void(preset is { } p ? $"refocus:{p.X},{p.Y}" : "refocus:null");
    public ValueTask CheckEmotionAsync(int battles) => Void($"check_emotion:{battles}");
    public ValueTask EnterMapAsync() { State.AutoSearch = scenario.AutoSearch; return Void("enter_map:normal"); }
    public ValueTask HandleFleetLockAsync() => Void("handle_map_fleet_lock");
    public ValueTask InitializeMapAsync(MapDefinition definition) => Void("map_init");
    public ValueTask ResetLevelsAsync() => Void("lv_reset");
    public ValueTask ReadLevelsAsync() => Void("lv_get");
    public ValueTask AutoSearchMoveAsync() => Void("auto_search_moving");
    public ValueTask AutoSearchCombatAsync(int fleetIndex) => Void($"auto_search_combat:{fleetIndex}");
    public ValueTask WithdrawAsync() => Void("withdraw");
}
