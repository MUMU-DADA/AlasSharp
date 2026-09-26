using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Zero-based local view coordinates, independent of one-based campaign cells.</summary>
public readonly record struct ViewCell(int X, int Y);
public sealed record MapCellObservation(ViewCell LocalCell, CellObservation State);
public sealed record MapObservation(IReadOnlyList<MapCellObservation> Cells, Cell Camera, ViewCell LocalCenter,
    MapScanMode Mode = MapScanMode.Normal);
public sealed record MapObservationResult(bool Accepted, int FailedPredictions, int Applied,
    IReadOnlyList<Cell> Ignored, IReadOnlyList<Cell> Outside);

public sealed partial class CampaignState
{
    /// <summary>Port of CampaignMap.update: preflight on copies, then apply only below two failures.</summary>
    public MapObservationResult ApplyObservation(MapObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!Enum.IsDefined(observation.Mode)) throw new ArgumentOutOfRangeException(nameof(observation));
        var ignored = new List<Cell>();
        var outside = new List<Cell>();
        var targets = new List<(Cell Target, CellObservation State)>();
        var seen = new HashSet<ViewCell>();
        // Validate and snapshot the entire view before touching authoritative state.
        foreach (var local in observation.Cells ?? throw new ArgumentException("Observation cells are required", nameof(observation)))
        {
            if (local?.State is null || !seen.Add(local.LocalCell))
                throw new ArgumentException("View cells must have unique coordinates and observations", nameof(observation));
            var target = new Cell(checked(observation.Camera.Column - observation.LocalCenter.X + local.LocalCell.X),
                checked(observation.Camera.Row - observation.LocalCenter.Y + local.LocalCell.Y));
            if (!Contains(target)) { outside.Add(target); continue; }
            if (Map.IgnoredPredictions.Any(rule => rule.Cell == target && rule.Matches(local.State)))
            { ignored.Add(target); continue; }
            targets.Add((target, local.State));
        }
        int failed = 0;
        foreach (var (target, info) in targets)
            if (!this[target].CopyForObservation().Merge(info, observation.Mode, Map.GridBehavior)) failed++;
        if (failed >= 2) return new(false, failed, 0, ignored.AsReadOnly(), outside.AsReadOnly());
        // Native permits one incompatible observation and retains writes made before Merge returned false.
        foreach (var (target, info) in targets) this[target].Merge(info, observation.Mode, Map.GridBehavior);
        if (observation.Mode == MapScanMode.Init) FixupSubmarineFleet();
        return new(true, failed, targets.Count, ignored.AsReadOnly(), outside.AsReadOnly());
    }

    public bool Contains(Cell cell) => cell.Column >= 1 && cell.Row >= 1 && cell.Column <= Map.Shape.Column && cell.Row <= Map.Shape.Row;

    /// <summary>Native init correction for upper submarine markers and fleet/enemy overlap.</summary>
    public void FixupSubmarineFleet()
    {
        foreach (var grid in Cells.Where(c => c.IsFleet && !c.IsSpawnPoint).ToArray())
        {
            var upper = Covered(grid.Location, [(0, -1)]).FirstOrDefault();
            if (upper?.IsSubmarineSpawnPoint == true)
            {
                grid.IsFleet = false;
                grid.IsCurrentFleet = false;
                upper.IsSubmarine = true;
            }
        }
        foreach (var grid in Cells.Where(c => c.IsEnemy && c.IsFleet).ToArray())
        {
            grid.IsFleet = false;
            grid.IsCurrentFleet = false;
        }
    }
}
