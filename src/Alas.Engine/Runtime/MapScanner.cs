using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>C# camera movement and visual observations, with no upstream business RPC.
/// Implementations must honor cancellation during movement, observation and recovery.</summary>
public interface IMapScanCamera
{
    Cell Position { get; }
    ValueTask FocusAsync(Cell destination, CancellationToken token);
    ValueTask CenterAsync(double tolerance, CancellationToken token);
    ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token);
    ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token);
}

/// <summary>Scan evidence only. Exhausting cameras or finding all spawns is not a sortie result.</summary>
public sealed record MapScanResult(IReadOnlyList<Cell> Visited, int RejectedViews, bool StoppedEarly,
    IReadOnlyList<Cell> Predictions);
public sealed record FleetScanOptions(bool HasDecoyEnemy = false, bool Fleet2Enabled = false);

/// <summary>Port of Camera.full_scan: nearest camera, required views, conflict recovery, then prediction.</summary>
public sealed class MapScanner(CampaignState state, IMapScanCamera camera, TimeProvider? clock = null)
{
    public static readonly SourceFile Source = new("module/map/camera.py",
        "22de5cd3ab4b3083b55dc979132ec6f8542bd920c639eb0e9505ad9c9b284275");
    public static readonly SourceFile SelectionSource = new("module/map/map_grids.py",
        "28508b77fd38ce6a67e9fa7137c46b4d75806ff170d19a4b7bc54c96a6663746");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<MapScanResult> ScanAsync(MapProgress progress, TimeSpan timeout,
        IEnumerable<Cell>? queue = null, IEnumerable<Cell>? mustScan = null,
        MapScanMode mode = MapScanMode.Normal, FleetScanOptions? fleet = null, CancellationToken token = default)
    {
        progress.Validate();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (fleet?.HasDecoyEnemy == true && mode == MapScanMode.Normal) mode = MapScanMode.Decoy;
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        var pending = queue?.ToList() ?? [];
        if (pending.Count == 0) pending.AddRange(state.Map.Cameras);
        var required = mustScan?.ToHashSet() ?? [];
        if (required.Count > 0) pending = pending.Concat(required).Distinct().ToList();
        if (pending.Any(cell => !state.Contains(cell))) throw new ArgumentOutOfRangeException(nameof(queue), "Scan camera lies outside map");
        if (!state.PoorMapData && state.SpawnStack.IsEmpty)
            throw new InvalidOperationException("Initialize spawn data before scanning a map");
        using var deadline = new CancellationTokenSource(timeout, clock ?? TimeProvider.System);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        bool entered = false;
        try
        {
            await _gate.WaitAsync(limit.Token);
            entered = true;
            limit.Token.ThrowIfCancellationRequested();
            state.ResetCurrentFleet();
            var visited = new List<Cell>();
            int rejected = 0;
            bool stoppedEarly = false;
            while (pending.Count > 0)
            {
                limit.Token.ThrowIfCancellationRequested();
                if (state.MissingIsNone(progress, mode) && !pending.Any(required.Contains))
                {
                    stoppedEarly = true;
                    break;
                }
                // Native chooses by Manhattan distance. Ties have no declared order;
                // preserve queue order for deterministic, equally near choices.
                var origin = camera.Position;
                var target = pending.MinBy(cell => Math.Abs((long)cell.Column - origin.Column) + Math.Abs((long)cell.Row - origin.Row));
                await camera.FocusAsync(target, limit.Token);
                limit.Token.ThrowIfCancellationRequested();
                await camera.CenterAsync(0.25, limit.Token);
                limit.Token.ThrowIfCancellationRequested();
                var observation = await camera.ObserveAsync(mode, limit.Token);
                limit.Token.ThrowIfCancellationRequested();
                if (observation.Mode != mode) throw new InvalidDataException("Camera observation scan mode differs from the request");
                if (observation.Camera != camera.Position) throw new InvalidDataException("Camera observation position differs from the current camera");
                var result = state.ApplyObservation(observation);
                if (!result.Accepted)
                {
                    rejected++;
                    await camera.EnsureEdgesAsync(skipFirstUpdate: false, limit.Token);
                    continue;
                }
                visited.Add(target);
                pending.Remove(target);
            }
            limit.Token.ThrowIfCancellationRequested();
            var predictions = state.PredictMissing(progress, mode);
            if (fleet is not null)
            {
                // Fleet.full_scan runs this after the camera's census and prediction.
                if (fleet.Fleet2Enabled && state.Fleet2Location is null)
                    state.Fleet2Location = state.Cells.FirstOrDefault(g => g.IsFleet && !g.IsCurrentFleet)?.Location;
                foreach (var location in new[] { state.Fleet1Location, state.Fleet2Location })
                    if (location is { } cell && state.Contains(cell))
                    {
                        var grid = state[cell];
                        if (!(grid.MayBoss && grid.IsCaughtBySiren)) grid.WipeOut();
                    }
            }
            return new(visited.AsReadOnly(), rejected, stoppedEarly, predictions);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Map scan exceeded its time limit", error);
        }
        finally { if (entered) _gate.Release(); }
    }
}
