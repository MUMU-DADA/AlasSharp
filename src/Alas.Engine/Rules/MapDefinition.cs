using System.Collections.Immutable;
using System.Globalization;

namespace Alas.Engine.Rules;

/// <summary>One-based, immutable map coordinates; independent of screen pixels.</summary>
public readonly record struct Cell(int Column, int Row)
{
    public static Cell Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        int split = 0, column = 0;
        while (split < value.Length && value[split] is >= 'A' and <= 'Z')
            column = checked(column * 26 + value[split++] - 'A' + 1);
        if (split == 0 || split == value.Length ||
            !int.TryParse(value.AsSpan(split), NumberStyles.None, CultureInfo.InvariantCulture, out int row) || row < 1)
            throw new FormatException($"Invalid map cell: {value}");
        return new Cell(column, row);
    }

    public override string ToString()
    {
        if (Column < 1 || Row < 1) throw new InvalidOperationException("Invalid map cell");
        string label = "";
        for (int n = Column; n > 0; n = (n - 1) / 26)
            label = (char)('A' + (n - 1) % 26) + label;
        return label + Row.ToString(CultureInfo.InvariantCulture);
    }
}

// Tokens describe possibilities, never observations. Preserve ME/Me spelling;
// GridInfo.decode uppercases both, so LowPriorityEnemy has no separate runtime priority.
public enum MapTile { Water, Land, Spawn, Enemy, LowPriorityEnemy, Boss, Mystery, Ammo, SubmarineSpawn, Siren }
public sealed record SpawnWave(int Battle, int Enemy = 0, int Mystery = 0, int Boss = 0);
public sealed record SourceFile(string Path, string Sha256);

public sealed class MapDefinition
{
    public Cell Shape { get; }
    public ImmutableArray<MapTile> Tiles { get; }
    public ImmutableArray<Cell> Cameras { get; }
    public ImmutableArray<Cell> SpawnCameras { get; }
    public ImmutableArray<SpawnWave> Waves { get; }

    public MapDefinition(string shape, string tiles, IEnumerable<string> cameras,
        IEnumerable<string> spawnCameras, IEnumerable<SpawnWave> waves)
    {
        Shape = Cell.Parse(shape);
        var rows = tiles.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rows.Length != Shape.Row) throw new ArgumentException("Map row count does not match shape", nameof(tiles));
        var parsed = ImmutableArray.CreateBuilder<MapTile>();
        foreach (string row in rows)
        {
            var columns = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != Shape.Column) throw new ArgumentException("Map column count does not match shape", nameof(tiles));
            foreach (string token in columns)
                parsed.Add(token switch
                {
                    "--" => MapTile.Water, "++" => MapTile.Land, "SP" => MapTile.Spawn,
                    "ME" => MapTile.Enemy, "Me" => MapTile.LowPriorityEnemy, "MB" => MapTile.Boss,
                    "MM" => MapTile.Mystery, "MA" => MapTile.Ammo, "__" => MapTile.SubmarineSpawn, "MS" => MapTile.Siren,
                    _ => throw new NotSupportedException($"Unported map token: {token}")
                });
        }
        Tiles = parsed.ToImmutable();
        Cameras = cameras.Select(Cell.Parse).ToImmutableArray();
        SpawnCameras = spawnCameras.Select(Cell.Parse).ToImmutableArray();
        foreach (var camera in Cameras.Concat(SpawnCameras)) ValidateCell(camera);
        Waves = waves.ToImmutableArray();
        if (Waves.Any(w => w.Battle < 0 || w.Enemy < 0 || w.Mystery < 0 || w.Boss < 0) ||
            !Waves.Select(w => w.Battle).SequenceEqual(Waves.Select(w => w.Battle).Distinct().Order()))
            throw new ArgumentException("Spawn waves must have unique ascending battle numbers and nonnegative counts", nameof(waves));
    }

    public int ExpectedBattles => Waves.FirstOrDefault(w => w.Boss > 0)?.Battle + 1 ?? 0;
    public int IndexOf(Cell cell)
    {
        ValidateCell(cell);
        return (cell.Row - 1) * Shape.Column + cell.Column - 1;
    }
    private void ValidateCell(Cell cell)
    {
        if (cell.Column < 1 || cell.Row < 1 || cell.Column > Shape.Column || cell.Row > Shape.Row)
            throw new ArgumentOutOfRangeException(nameof(cell), "Cell lies outside map");
    }
}
