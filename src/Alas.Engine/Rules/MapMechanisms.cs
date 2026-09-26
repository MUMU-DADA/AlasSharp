using System.Collections.Immutable;

namespace Alas.Engine.Rules;

public enum MapDirection { Up, Down, Left, Right }
public sealed record LandMechanism(Cell Origin, MapDirection Direction);

/// <summary>Immutable source declarations. Instance references are bound within each CampaignState.</summary>
public sealed class MapMechanisms
{
    public ImmutableArray<LandMechanism> LandBased { get; }
    public ImmutableArray<ImmutableArray<Cell>> Mazes { get; }
    public ImmutableArray<Cell> FortressEnemies { get; }
    public ImmutableArray<Cell> FortressBlocks { get; }
    public ImmutableArray<ImmutableArray<Cell>> BouncingRoutes { get; }
    internal IEnumerable<Cell> Cells => LandBased.Select(m => m.Origin).Concat(Mazes.SelectMany(g => g))
        .Concat(FortressEnemies).Concat(FortressBlocks).Concat(BouncingRoutes.SelectMany(g => g));

    public MapMechanisms(IEnumerable<LandMechanism>? landBased = null, IEnumerable<IEnumerable<Cell>>? mazes = null,
        IEnumerable<Cell>? fortressEnemies = null, IEnumerable<Cell>? fortressBlocks = null,
        IEnumerable<IEnumerable<Cell>>? bouncingRoutes = null)
    {
        LandBased = landBased?.ToImmutableArray() ?? [];
        if (LandBased.Any(m => !Enum.IsDefined(m.Direction))) throw new ArgumentException("Unknown mechanism direction", nameof(landBased));
        Mazes = mazes?.Select(g => g.ToImmutableArray()).ToImmutableArray() ?? [];
        FortressEnemies = fortressEnemies?.ToImmutableArray() ?? [];
        FortressBlocks = fortressBlocks?.ToImmutableArray() ?? [];
        BouncingRoutes = bouncingRoutes?.Select(g => g.ToImmutableArray()).ToImmutableArray() ?? [];
    }
}
