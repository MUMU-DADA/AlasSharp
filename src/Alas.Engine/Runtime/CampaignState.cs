using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Owned by one sortie. Declarations never set observed enemy or boss flags.</summary>
public sealed class CampaignState
{
    public MapDefinition Map { get; }
    public IReadOnlyList<CellState> Cells { get; }
    public int BattleCount { get; set; }
    public bool AutoSearch { get; set; }
    public int FleetIndex { get; set; } = 1;
    public CampaignState(MapDefinition map)
    {
        Map = map;
        Cells = Array.AsReadOnly(map.Tiles.Select((tile, index) =>
            new CellState(new Cell(index % map.Shape.Column + 1, index / map.Shape.Column + 1), tile) { Weight = 10 }).ToArray());
    }
    public CellState this[Cell cell] => Cells[Map.IndexOf(cell)];
    public bool HasBoss => Cells.Any(c => c.IsBoss);
    public bool HasNonBossEnemy => Cells.Any(c => !c.IsBoss && (c.IsEnemy || c.IsSiren || c.IsFortress));
    public void ResetMap() { foreach (var cell in Cells) cell.Reset(); }
    public void ResetCurrentFleet() { foreach (var cell in Cells) cell.IsCurrentFleet = false; }
}

public sealed record CampaignContext(CampaignState State, CampaignConfiguration Config, ICampaignOperations Operations);

/// <summary>Typed domain operations for the new C# action implementation. No method-name RPC.</summary>
public interface ICampaignOperations
{
    ValueTask<bool> ClearEnemyAsync();
    ValueTask<bool> ClearBossAsync();
    ValueTask<bool> BruteClearBossAsync();
    ValueTask<bool> BreakSirenCaughtAsync();
    ValueTask<bool> ClearMysteriesAsync();
    ValueTask<bool> PickUpAmmoAsync();
    ValueTask<bool> ClearSirenAsync();
    ValueTask<bool> ClearAnyEnemyBySecondFleetCostAsync();
    ValueTask<bool> ClearBouncingEnemyAsync();
    ValueTask<bool> ClearMechanismAsync();
    ValueTask RefocusBossAsync((int X, int Y)? preset);
    ValueTask CheckEmotionAsync(int battles);
    ValueTask EnterMapAsync();
    ValueTask HandleFleetLockAsync();
    ValueTask InitializeMapAsync(MapDefinition definition);
    ValueTask ResetLevelsAsync();
    ValueTask ReadLevelsAsync();
    ValueTask AutoSearchMoveAsync();
    ValueTask AutoSearchCombatAsync(int fleetIndex);
    ValueTask WithdrawAsync();
}

public sealed class MapEnemyMovedException : Exception;
public sealed class CampaignEndedException : Exception;
public sealed class CampaignScriptException(string message) : Exception(message);
