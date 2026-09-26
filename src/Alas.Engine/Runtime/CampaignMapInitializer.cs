using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record CampaignMapReady(IMapScanCamera Camera, MapScanResult Scan, Cell Fleet1, Cell? Fleet2);

/// <summary>One-sortie data load, initial scan and fleet/path localization.</summary>
public static class CampaignMapInitializer
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;

    public static async ValueTask<CampaignMapReady> InitializeAsync(CampaignState state,
        CampaignConfiguration configuration,
        Func<CampaignState, CancellationToken, ValueTask<IMapScanCamera>> createCamera,
        TimeSpan scanTimeout, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(createCamera);
        if (scanTimeout <= TimeSpan.Zero || scanTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(scanTimeout));
        if (configuration.Submarine != 0)
            throw new NotSupportedException("Submarine positioning is not yet ported to campaign initialization");
        if (state.Map.SwipePreset is not null)
            throw new NotSupportedException("Initial map edge scan requires the compiled swipe preset");

        state.InitializeMapData(new(configuration.IsClearMode, configuration.PoorMapData,
            configuration.HasWall, configuration.HasPortal, configuration.HasLandBased,
            configuration.HasMaze, configuration.HasFortress, configuration.HasBouncingEnemy));
        var camera = await createCamera(state, token);
        if (!state.Contains(camera.Position))
            throw new InvalidDataException("Initial camera position is outside the declared map");
        await camera.EnsureEdgesAsync(skipFirstUpdate: true, token);
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
        var fleet1 = current[0].Location;
        Cell? fleet2 = configuration.Fleet2 == 0 ? null : fleets.Single(grid => grid.Location != fleet1).Location;
        state.FleetIndex = 1;
        state.Fleet1Location = fleet1;
        state.Fleet2Location = fleet2;
        state.Paths.ComputeFleetCosts([new(1, fleet1), new(2, fleet2)], fleet1, configuration.HasAmbush);
        return new(camera, scan, fleet1, fleet2);
    }
}
