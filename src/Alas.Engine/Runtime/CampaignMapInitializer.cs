using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record CampaignMapReady(IMapScanCamera Camera, MapScanResult Scan, Cell Fleet1, Cell? Fleet2);

/// <summary>One-sortie data load, initial scan and fleet/path localization.</summary>
public static class CampaignMapInitializer
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;

    public static async ValueTask<CampaignMapReady> InitializeAsync(CampaignState state,
        CampaignConfiguration configuration, FleetSelection selected,
        Func<CampaignState, CancellationToken, ValueTask<IMapScanCamera>> createCamera,
        TimeSpan scanTimeout, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(createCamera);
        MapRounds.Validate(configuration);
        if (scanTimeout <= TimeSpan.Zero || scanTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(scanTimeout));
        if (selected.FrameSequence <= 0 || selected.Clicks < 0 || selected.LogicalIndex is not (1 or 2) ||
            selected.DisplayedIndex is not (1 or 2) ||
            selected.LogicalIndex != FleetRoles.LogicalIndex(selected.DisplayedIndex, configuration) ||
            configuration.Fleet2 == 0 && selected.LogicalIndex != 1)
            throw new InvalidDataException("Initial fleet selection has no consistent observed identity");

        state.InitializeMapData(new(configuration.IsClearMode, configuration.PoorMapData,
            configuration.HasWall, configuration.HasPortal, configuration.HasLandBased,
            configuration.HasMaze, configuration.HasFortress, configuration.HasBouncingEnemy));
        var camera = await createCamera(state, token);
        if (!state.Contains(camera.Position))
            throw new InvalidDataException("Initial camera position is outside the declared map");
        ViewCell? preset = state.Map.SwipePreset is { } swipe ? new(swipe.X, swipe.Y) : null;
        await camera.EnsureEdgesAsync(skipFirstUpdate: true, preset, token);
        var scanner = new MapScanner(state, camera);
        var scan = await scanner.ScanAsync(state.Progress, scanTimeout,
            mustScan: state.Map.SpawnCameras, mode: MapScanMode.Init,
            fleet: new FleetScanOptions(Fleet2Enabled: configuration.Fleet2 != 0), token: token);

        var fleets = state.Cells.Where(grid => grid.IsFleet &&
            (state.PoorMapData || grid.IsSpawnPoint)).ToArray();
        var current = fleets.Where(grid => grid.IsCurrentFleet).ToArray();
        if (current.Length != 1 || configuration.Fleet2 == 0 && fleets.Length != 1 ||
            configuration.Fleet2 != 0 && fleets.Length != 2)
            throw new InvalidDataException("Initial scan did not uniquely locate the selected fleet and configured fleets");
        var active = current[0].Location;
        if (configuration.Submarine != 0)
            state.SubmarineLocation = LocateSubmarine(state, camera.Position);
        Cell? other = configuration.Fleet2 == 0 ? null : fleets.Single(grid => grid.Location != active).Location;
        var fleet1 = selected.LogicalIndex == 1 ? active : other!.Value;
        Cell? fleet2 = selected.LogicalIndex == 2 ? active : other;
        state.FleetIndex = selected.LogicalIndex;
        state.Fleet1Location = fleet1;
        state.Fleet2Location = fleet2;
        state.RefreshFleetPaths(configuration);
        state.Rounds.Initialize(configuration);
        return new(camera, scan, fleet1, fleet2);
    }

    /// <summary>Port of Fleet.find_submarine: consume observed spawn markers, then use
    /// the same declaration/coverage fallback before selecting the nearest open cell.</summary>
    private static Cell? LocateSubmarine(CampaignState state, Cell camera)
    {
        var observed = state.Cells.Where(cell => cell.IsSubmarine).Select(cell => cell.Location).ToArray();
        if (observed.Length == 1) return observed[0];

        var spawn = state.Cells.Where(cell => cell.IsSubmarineSpawnPoint).ToArray();
        // Native find_submarine leaves the location empty when this map has no
        // submarine spawn declaration, even if a fleet plan requested support.
        if (spawn.Length == 0) return null;
        if (observed.Length == 0 && spawn.Length == 1)
        {
            spawn[0].IsSubmarine = true;
            return spawn[0].Location;
        }

        if (observed.Length == 0 && spawn.Length > 1)
        {
            var covered = spawn.SelectMany(point => state.Covered(point.Location, [(0, 1)]))
                .Where(cell => cell.IsEnemy || cell.IsFleet || cell.IsSiren || cell.IsBoss)
                .DistinctBy(cell => cell.Location)
                .ToArray();
            if (covered.Length == 1 && covered[0].Location.Row > 1)
            {
                var candidate = new Cell(covered[0].Location.Column, covered[0].Location.Row - 1);
                if (state.Contains(candidate) && state[candidate].IsSubmarineSpawnPoint)
                {
                    state[candidate].IsSubmarine = true;
                    return candidate;
                }
            }
        }

        // Native falls back to the closest non-land cell to the current camera when
        // visual coverage cannot disambiguate multiple spawn points.
        var fallback = state.Cells.Where(cell => !cell.IsLand)
            .OrderBy(cell => Math.Abs((long)cell.Location.Column - camera.Column) +
                              Math.Abs((long)cell.Location.Row - camera.Row))
            .FirstOrDefault();
        if (fallback is null)
            throw new InvalidDataException("Configured submarine has no navigable map cell");
        fallback.IsSubmarine = true;
        return fallback.Location;
    }
}
