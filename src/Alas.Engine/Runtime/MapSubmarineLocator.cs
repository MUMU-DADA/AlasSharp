using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record SubmarineObservation(Cell Location, Cell Camera, long FrameSequence, bool Present);
public sealed record SubmarineLocationEvidence(string Method, Cell? Location,
    IReadOnlyList<SubmarineObservation> Observations, Cell? Pending = null);

/// <summary>Native find_submarine/find_all_submarines, including camera searches and rule-based inference.
/// A location inferred from declarations never becomes an observed submarine flag.</summary>
public static class MapSubmarineLocator
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;
    public static readonly CameraSight SearchSight = new(-2, -1, 2, -1);

    public static async ValueTask<SubmarineLocationEvidence> LocateAsync(CampaignState state,
        bool enabled, IMapScanCamera camera, TimeSpan timeout, CancellationToken token = default,
        TimeProvider? clock = null)
    {
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize map data before locating submarines");
        if (state.SubmarineEvidence is not null) throw new InvalidOperationException("Submarine localization belongs to a fresh sortie");
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(timeout, clock ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var observations = new List<SubmarineObservation>();
        SubmarineLocationEvidence Finish(string method, Cell? location)
        {
            linked.Token.ThrowIfCancellationRequested();
            state.SubmarineLocation = location;
            return state.SubmarineEvidence = new(method, location, observations.ToArray());
        }
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var spawns = state.Cells.Where(cell => cell.IsSubmarineSpawnPoint).ToArray();
            if (!enabled) return Finish("disabled", null);
            if (spawns.Length == 0) return Finish("no_spawn", null);
            var observed = state.Cells.Where(cell => cell.IsSubmarine).ToArray();
            if (observed.Length == 1) return Finish("initial_observation", observed[0].Location);
            if (observed.Length == 0)
            {
                if (spawns.Length == 1) return Finish("single_spawn", spawns[0].Location);
                var covered = spawns.SelectMany(cell => state.Covered(cell.Location, [(0, 1)]))
                    .Distinct().Where(cell => cell.IsEnemy || cell.IsFleet || cell.IsSiren || cell.IsBoss).ToArray();
                if (covered.Length == 1)
                    return Finish("covered_spawn", new(covered[0].Location.Column, covered[0].Location.Row - 1));
            }

            var pending = spawns.Select(cell => cell.Location).ToList();
            while (pending.Count > 0)
            {
                linked.Token.ThrowIfCancellationRequested();
                // Like the full scanner, equally near candidates retain declaration order.
                var origin = camera.Position;
                var target = pending.MinBy(cell => Math.Abs((long)cell.Column - origin.Column) + Math.Abs((long)cell.Row - origin.Row));
                state.SubmarineEvidence = new("searching", null, observations.ToArray(), target);
                var result = await camera.InspectSubmarineAsync(target, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (result.Location != target || result.Camera != camera.Position || result.FrameSequence <= 0 ||
                    observations.Count > 0 && result.FrameSequence < observations[^1].FrameSequence)
                    throw new InvalidDataException("Submarine observation has inconsistent location, camera or frame identity");
                observations.Add(result);
                if (result.Present) return Finish("searched_observation", target);
                pending.Remove(target);
            }

            // Upstream shape is a zero-based maximum coordinate, not a cell count.
            var center = new Cell((state.Map.Shape.Column - 1) / 2 + 1, (state.Map.Shape.Row - 1) / 2 + 1);
            var fallback = state.Cells.Where(cell => !cell.IsLand)
                .MinBy(cell => Math.Abs((long)cell.Location.Column - center.Column) + Math.Abs((long)cell.Location.Row - center.Row))
                ?? throw new InvalidDataException("Submarine localization found no non-land cell");
            return Finish("map_center_assumption", fallback.Location);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            state.MovementInvalidated = true;
            state.SubmarineEvidence = new("failed", null, observations.ToArray(), state.SubmarineEvidence?.Pending);
            if (error is OperationCanceledException && !token.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException("Submarine localization exceeded its time limit", error);
            throw;
        }
    }
}
