using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules;

/// <summary>Native RoadGrids groups: a single blocked group obstructs this road.</summary>
public sealed record RoadDefinition(ImmutableArray<ImmutableArray<Cell>> Groups)
{
    public static readonly SourceFile Source = MapScanner.SelectionSource;
    public IReadOnlyList<CellState> Select(CampaignState state, bool potential = false)
    {
        if (Groups.IsDefault || Groups.Any(group => group.IsDefault)) throw new InvalidDataException("Road groups must be initialized");
        var selected = new List<CellState>();
        foreach (var group in Groups)
        {
            var cells = group.Select(cell => state[cell]).ToArray();
            if (potential)
            {
                if (cells.Any(cell => cell.IsFleet || cell.IsCleared)) continue;
                if (cells.Length - cells.Count(cell => cell.IsEnemy) == 1)
                    selected.AddRange(cells.Where(cell => cell.IsEnemy));
            }
            else if (cells.All(cell => cell.IsEnemy)) selected.AddRange(cells);
        }
        return selected.Distinct().ToArray();
    }
}
