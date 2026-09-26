using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>The image has already received the map UI mask; geometry belongs to this same frame.</summary>
public sealed record MapViewFrame(ScreenFrame Frame, MapViewGeometry Geometry);
public readonly record struct FleetMarker(bool Fleet, bool Current);

public interface IMapSwipeEvidence
{
    ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token);
    /// <summary>Null means at least one patch lies outside the detection mask.</summary>
    ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid,
        MapViewFrame after, VisibleGrid newGrid, CancellationToken token);
}

/// <summary>Native View.predict_swipe decisions. Inputs are raw markers and numeric CV measurements.</summary>
public sealed class MapSwipePredictor(IMapSwipeEvidence evidence)
{
    public async ValueTask<ViewCell?> PredictAsync(MapViewFrame before, MapViewFrame after,
        bool currentFleet = true, bool seaGrids = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var old = before.Geometry; var next = after.Geometry;
        ViewCell Difference(ViewCell a, ViewCell b) => new(
            checked(a.X - b.X - (old.Center.X - next.Center.X)),
            checked(a.Y - b.Y - (old.Center.Y - next.Center.Y)));
        if (currentFleet)
        {
            async ValueTask<List<ViewCell>> Find(MapViewFrame view)
            {
                var cells = new List<ViewCell>();
                foreach (var grid in view.Geometry.Grids)
                {
                    token.ThrowIfCancellationRequested();
                    var marker = await evidence.FleetAsync(view, grid, token);
                    if (marker is { Fleet: true, Current: true }) cells.Add(grid.LocalCell);
                }
                return cells;
            }
            var a = await Find(before); var b = await Find(after);
            token.ThrowIfCancellationRequested();
            if (a.Count == 1 && b.Count == 1) return Difference(a[0], b[0]);
        }
        if (seaGrids)
        {
            var votes = new Dictionary<ViewCell, int>();
            foreach (var a in old.Grids)
                foreach (var b in next.Grids)
                {
                    token.ThrowIfCancellationRequested();
                    double? similarity = await evidence.SimilarityAsync(before, a, after, b, token);
                    if (similarity is { } value && (!double.IsFinite(value) || value is < -1 or > 1))
                        throw new InvalidDataException("Invalid grid similarity");
                    // Native does not first filter by predict_sea().
                    if (similarity > 0.9)
                    {
                        var difference = Difference(a.LocalCell, b.LocalCell);
                        votes[difference] = votes.GetValueOrDefault(difference) + 1;
                    }
                }
            token.ThrowIfCancellationRequested();
            var ranked = votes.OrderByDescending(v => v.Value).Take(2).ToArray();
            if (ranked.Length == 1 || ranked.Length == 2 && ranked[0].Value > ranked[1].Value) return ranked[0].Key;
        }
        return null;
    }
}

/// <summary>Camera coordinates are one-based map cells; view and swipe coordinates are zero-based deltas.</summary>
public sealed class MapCameraState
{
    public static readonly SourceFile Source = MapScanner.Source;
    public Cell Position { get; private set; }
    public MapViewFrame View { get; private set; }
    public ViewCell? PendingSwipe { get; private set; }
    public MapViewFrame? Previous { get; private set; }
    private readonly Cell _shape;

    public MapCameraState(Cell shape, Cell position, MapViewFrame view)
    {
        if (shape.Column < 1 || shape.Row < 1) throw new ArgumentOutOfRangeException(nameof(shape));
        ArgumentNullException.ThrowIfNull(view);
        _shape = shape; Position = CorrectEdges(position, view.Geometry); View = view;
    }

    public ScreenPoint PrepareSwipe(ViewCell requested)
    {
        Previous = View;
        PendingSwipe = requested;
        return new(0.5 - View.Geometry.CenterOffset.X + requested.X, 0.5 - View.Geometry.CenterOffset.Y + requested.Y);
    }

    public async ValueTask UpdateAsync(MapViewFrame view, MapSwipePredictor predictor, bool predict = true,
        bool currentFleet = true, bool seaGrids = false, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        token.ThrowIfCancellationRequested();
        var position = Position;
        bool consumed = Previous is not null && PendingSwipe is { } pending && pending != default;
        if (consumed)
        {
            var swipe = PendingSwipe!.Value;
            if (predict) swipe = await predictor.PredictAsync(Previous!, view, currentFleet, seaGrids, token) ?? swipe;
            position = new(checked(position.Column + swipe.X), checked(position.Row + swipe.Y));
        }
        position = CorrectEdges(position, view.Geometry);
        token.ThrowIfCancellationRequested();
        Position = position; View = view;
        // Native zero-vector refocusing retains pending data and does not predict a displacement.
        if (consumed) { Previous = null; PendingSwipe = null; }
    }

    private Cell CorrectEdges(Cell position, MapViewGeometry geometry)
    {
        // The native names lower/upper denote detected screen edges, not map row order.
        int x = geometry.Edges.Left ? checked(1 + geometry.Center.X) : geometry.Edges.Right
            ? checked(_shape.Column - geometry.Shape.X + geometry.Center.X) : position.Column;
        int y = geometry.Edges.Upper ? checked(_shape.Row - geometry.Shape.Y + geometry.Center.Y) : geometry.Edges.Lower
            ? checked(1 + geometry.Center.Y) : position.Row;
        return new(x, y);
    }
}
