using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal sealed record Scenario(string Rule, string Operation = "dispatch", int BattleCount = 0,
    bool Poor = false, bool ClearAll = false, bool Movable = false, string Cells = "empty",
    string? TrueOperation = null, bool CombatReturn = true, bool HandleError = false,
    bool AutoSearch = false, string? Signal = null, int SignalCount = 1, string SignalOperation = "clear_enemy",
    bool Advance = false);
internal sealed record ProbeResult(string[] Calls, object? Value, string? Exception, int BattleCount);

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
    public ValueTask<bool> ClearBossAsync() => Result("clear_boss");
    public ValueTask<bool> BruteClearBossAsync() => Result("brute_clear_boss");
    public ValueTask<bool> BreakSirenCaughtAsync() => Result("fleet_2_break_siren_caught");
    public ValueTask<bool> ClearMysteriesAsync() => Result("clear_all_mystery");
    public ValueTask<bool> PickUpAmmoAsync() => Result("pick_up_ammo");
    public ValueTask<bool> ClearSirenAsync() => Result("clear_siren");
    public ValueTask<bool> ClearAnyEnemyBySecondFleetCostAsync() => Result("clear_any_enemy:cost_2");
    public ValueTask<bool> ClearBouncingEnemyAsync() => Result("clear_bouncing_enemy");
    public ValueTask<bool> ClearMechanismAsync() => Result("clear_mechanism");
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
