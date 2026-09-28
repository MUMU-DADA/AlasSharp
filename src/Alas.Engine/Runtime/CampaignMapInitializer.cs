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
        SubmarineRules.RequireSupported(configuration);
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
        Cell? other = configuration.Fleet2 == 0 ? null : fleets.Single(grid => grid.Location != active).Location;
        var fleet1 = selected.LogicalIndex == 1 ? active : other!.Value;
        Cell? fleet2 = selected.LogicalIndex == 2 ? active : other;
        await MapSubmarineLocator.LocateAsync(state, configuration.Submarine != 0, camera, scanTimeout, token);
        state.FleetIndex = selected.LogicalIndex;
        state.Fleet1Location = fleet1;
        state.Fleet2Location = fleet2;
        state.RefreshFleetPaths(configuration);
        state.Rounds.Initialize(configuration);
        return new(camera, scan, fleet1, fleet2);
    }
}
