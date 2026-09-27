using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules;

public enum RoadBlockKind { Blocked, Potential, First }

/// <summary>Native RoadGrids groups: a single blocked group obstructs this road.</summary>
public sealed record RoadDefinition(ImmutableArray<ImmutableArray<Cell>> Groups)
{
    public static readonly SourceFile Source = MapScanner.SelectionSource;
    public RoadDefinition Combine(RoadDefinition other)
    {
        Validate(); other.Validate();
        return new(Groups.SelectMany(first => other.Groups.Select(second => first.Concat(second).Distinct().ToImmutableArray())).ToImmutableArray());
    }
    private void Validate()
    {
        if (Groups.IsDefault || Groups.Any(group => group.IsDefault)) throw new InvalidDataException("Road groups must be initialized");
    }
    public IReadOnlyList<CellState> Select(CampaignState state, bool potential = false)
        => Select(state, potential ? RoadBlockKind.Potential : RoadBlockKind.Blocked);

    public IReadOnlyList<CellState> Select(CampaignState state, RoadBlockKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Validate();
        var selected = new List<CellState>();
        foreach (var group in Groups)
        {
            var cells = group.Select(cell => state[cell]).ToArray();
            if (kind is RoadBlockKind.Potential or RoadBlockKind.First)
            {
                if (cells.Any(cell => cell.IsFleet || cell.IsCleared)) continue;
                if (kind == RoadBlockKind.First || cells.Length - cells.Count(cell => cell.IsEnemy) == 1)
                    selected.AddRange(cells.Where(cell => cell.IsEnemy));
            }
            else if (cells.All(cell => cell.IsEnemy)) selected.AddRange(cells);
        }
        return selected.Distinct().ToArray();
    }
}
