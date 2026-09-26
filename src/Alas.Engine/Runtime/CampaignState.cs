using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Owned by one sortie. Declarations never set observed enemy or boss flags.</summary>
public sealed partial class CampaignState
{
    public MapDefinition Map { get; }
    public IReadOnlyList<CellState> Cells { get; }
    public MapPathfinder Paths { get; }
    public int MazeRound { get; private set; } = 9;
    public int BattleCount { get; set; }
    public bool AutoSearch { get; set; }
    public int FleetIndex { get; set; } = 1;
    public CampaignState(MapDefinition map)
    {
        Map = map;
        Mechanisms = map.Mechanisms;
        ActiveWaves = map.Waves;
        Cells = Array.AsReadOnly(map.Tiles.Select((tile, index) =>
        {
            var cell = map.CreateCell(new Cell(index % map.Shape.Column + 1, index / map.Shape.Column + 1), tile);
            cell.Weight = map.Weights[index];
            return cell;
        }).ToArray());
        foreach (var portal in map.Portals) this[portal.From].IsPortal = true;
        Paths = new MapPathfinder(this);
    }
    public CellState this[Cell cell] => Cells[Map.IndexOf(cell)];
    public bool HasBoss => Cells.Any(c => c.IsBoss);
    public bool HasNonBossEnemy => Cells.Any(c => !c.IsBoss && (c.IsEnemy || c.IsSiren || c.IsFortress));
    public void ResetMap() { foreach (var cell in Cells) cell.Reset(); }
    public void ResetCurrentFleet() { foreach (var cell in Cells) cell.IsCurrentFleet = false; }
    public void LoadMapData(bool useLoop = false)
    {
        var tiles = useLoop && !Map.LoopTiles.IsEmpty ? Map.LoopTiles : Map.Tiles;
        for (int i = 0; i < Cells.Count; i++) Cells[i].LoadDeclaration(tiles[i]);
    }
    public IReadOnlyList<CellState> Covered(Cell cell, IEnumerable<(int X, int Y)>? offsets = null)
        => (offsets ?? this[cell].CoveredOffsets()).Select(offset => new Cell(cell.Column + offset.X, cell.Row + offset.Y))
            .Where(c => c.Column >= 1 && c.Row >= 1 && c.Column <= Map.Shape.Column && c.Row <= Map.Shape.Row).Select(c => this[c]).ToArray();
    public void LoadMechanisms(bool landBased = false, bool maze = false, bool fortress = false, bool bouncingEnemy = false)
    {
        if (landBased)
            foreach (var mechanism in Mechanisms.LandBased)
            {
                (int x, int y) = mechanism.Direction switch
                { MapDirection.Up => (0, -1), MapDirection.Down => (0, 1), MapDirection.Left => (-1, 0), MapDirection.Right => (1, 0), _ => throw new InvalidOperationException() };
                var triggers = Covered(mechanism.Origin, [(0, -1), (0, 1), (-1, 0), (1, 0)]).Where(g => !g.IsLand).ToArray();
                var blocks = Covered(mechanism.Origin, Enumerable.Range(1, 3).Select(n => (n * x, n * y))).Where(g => !g.IsLand).ToArray();
                foreach (var trigger in triggers) { trigger.IsMechanismTrigger = true; trigger.MechanismTrigger = triggers; trigger.MechanismBlock = blocks; }
                foreach (var block in blocks) block.IsMechanismBlock = true;
            }
        if (maze)
        {
            MazeRound = Mechanisms.Mazes.Length * 3;
            for (int index = 0; index < Mechanisms.Mazes.Length; index++)
            {
                var cells = Mechanisms.Mazes[index].Select(c => this[c]).ToArray();
                foreach (var cell in cells) { cell.IsMaze = true; cell.MazeRound = Array.AsReadOnly(new[] { index * 3, index * 3 + 1, index * 3 + 2 }); }
                foreach (var cell in cells)
                {
                    Paths.ComputeCosts(cell.Location, hasAmbush: false);
                    cell.MazeNearby = Cells.Where(c => !c.IsLand && c.Cost is 1 or 2).ToArray();
                }
            }
        }
        if (fortress)
        {
            foreach (var cell in Mechanisms.FortressEnemies) this[cell].IsFortress = true;
            foreach (var cell in Mechanisms.FortressBlocks) this[cell].IsMechanismBlock = true;
        }
        if (bouncingEnemy)
            foreach (var cell in Mechanisms.BouncingRoutes.SelectMany(g => g)) this[cell].MayBouncingEnemy = true;
    }
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
public sealed class CampaignEndedException(string? message = null) : Exception(message);
public sealed class CampaignScriptException(string message) : Exception(message);
