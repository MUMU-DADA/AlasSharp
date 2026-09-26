using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Image capture, UI masking and polygon/edge detection only; no movement or map business logic.</summary>
public interface IMapViewSource
{
    ValueTask<MapViewFrame> CaptureAsync(CancellationToken token);
}

/// <summary>Pixel gesture transport. The C# camera supplies geometry and avoidance regions.</summary>
public interface IMapSwipeInput
{
    ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token);
}
public sealed record MapSwipeGesture(ScreenPoint Pixels, PixelArea Box,
    IReadOnlyList<PixelArea>? PreferredEnds, IReadOnlyList<PixelArea>? ForbiddenAreas);
public enum MapControlMethod { Adb, Minitouch, MaaTouch }

public sealed record MapCameraRules
{
    public static readonly SourceFile Source = GridRecognitionRules.Source;
    public double CenterTolerance { get; init; } = 0.2;
    public double SwipeDrop { get; init; } = 0.25;
    public ScreenPoint Multiply { get; init; } = new(1.064, 1.084);
    public ScreenPoint MultiplyMinitouch { get; init; } = new(1.029, 1.048);
    public ScreenPoint MultiplyMaaTouch { get; init; } = new(0.999, 1.017);
    public bool Predict { get; init; } = true;
    public bool PredictCurrentFleet { get; init; } = true;
    public bool PredictSeaGrids { get; init; }
    public bool Optimize { get; init; } = true;
    public string EdgeCorner { get; init; } = "";
    public void Validate()
    {
        if (!double.IsFinite(CenterTolerance) || CenterTolerance is < 0 or > 0.5 ||
            !double.IsFinite(SwipeDrop) || SwipeDrop is < 0 or > 0.5 || EdgeCorner is null ||
            new[] { Multiply, MultiplyMinitouch, MultiplyMaaTouch }.Any(p =>
                !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X <= 0 || p.Y <= 0))
            throw new ArgumentException("Invalid camera configuration");
    }
}

/// <summary>C# scan-camera implementation. Detection and pixel gestures are injected I/O boundaries.
/// A failed action/update invalidates this instance; uncertain physical state must be relocalized.</summary>
public sealed class MapCamera : IMapScanCamera
{
    public static readonly SourceFile DirectionSource = new("module/map/utils.py",
        "74b9fb3440cf3000336a6a056419b35ea03b8735c935849f6b85c1f0830eaf94");
    private readonly CampaignState _map;
    private readonly MapCameraState _camera;
    private readonly IMapViewSource _source;
    private readonly IMapSwipeInput _input;
    private readonly GridRecognition _recognition;
    private readonly MapSwipePredictor _predictor;
    private readonly MapCameraRules _rules;
    private readonly ScreenPoint _multiply;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly Random _random;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MapObservation? _observation;
    private bool _faulted;
    public Cell Position => _camera.Position;
    public MapViewFrame View => _camera.View;

    public MapCamera(CampaignState map, Cell initialPosition, MapViewFrame initialView,
        IMapViewSource source, IMapSwipeInput input, GridRecognition recognition, MapSwipePredictor predictor,
        MapCameraRules rules, MapControlMethod method = MapControlMethod.Adb, TimeSpan? timeout = null,
        TimeProvider? clock = null, Random? random = null, bool correctInitialEdges = true)
    {
        rules.Validate();
        if (!Enum.IsDefined(method)) throw new ArgumentOutOfRangeException(nameof(method));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        _map = map; _source = source; _input = input; _recognition = recognition; _predictor = predictor; _rules = rules;
        _camera = new(map.Map.Shape, initialPosition, initialView, correctInitialEdges);
        _multiply = method switch { MapControlMethod.Minitouch => rules.MultiplyMinitouch,
            MapControlMethod.MaaTouch => rules.MultiplyMaaTouch, _ => rules.Multiply };
        _clock = clock ?? TimeProvider.System; _random = random ?? Random.Shared;
    }

    /// <summary>Initial capture uses the same error confirmation and outside-view correction as later updates.</summary>
    public static async ValueTask<MapCamera> CreateAsync(CampaignState map, Cell initialPosition,
        IMapViewSource source, IMapSwipeInput input, GridRecognition recognition, MapSwipePredictor predictor,
        MapCameraRules rules, TimeSpan timeout, CancellationToken token = default, TimeProvider? clock = null)
    {
        rules.Validate();
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        clock ??= TimeProvider.System;
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var errors = new IntervalTimer(clock, 5, 10); errors.Reset();
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                try
                {
                    var first = await source.CaptureAsync(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    return new(map, initialPosition, first, source, input, recognition, predictor, rules, timeout: timeout, clock: clock);
                }
                catch (MapInterruptionHandledException) { }
                catch (CameraOutsideViewException error) when (error.PartialView is { } partial)
                {
                    // Incomplete view edges cannot localize the camera before the corrective capture succeeds.
                    var camera = new MapCamera(map, initialPosition, partial, source, input, recognition, predictor,
                        rules, timeout: timeout, clock: clock, correctInitialEdges: false);
                    try
                    {
                        await camera.RunAsync(async ct =>
                        {
                            if (!await camera.SwipeVectorCoreAsync(new(-error.Offset.X, -error.Offset.Y), ct, partial)) throw error;
                            return true;
                        }, linked.Token);
                        return camera;
                    }
                    catch (MapGeometryException) { if (errors.Reached()) throw; }
                }
                catch (MapGeometryException) { if (errors.Reached()) throw; }
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Map camera initialization exceeded its time limit", error); }
    }

    public async ValueTask FocusAsync(Cell destination, CancellationToken token)
    {
        if (!_map.Contains(destination)) throw new ArgumentOutOfRangeException(nameof(destination));
        await RunAsync(async ct =>
        {
            while (true)
            {
                var delta = new ViewCell((int)Math.Clamp((long)destination.Column - Position.Column, -4, 4),
                    (int)Math.Clamp((long)destination.Row - Position.Row, -3, 3));
                // Even when already at the destination, native focus_to performs zero-vector centering.
                if (!await SwipeCoreAsync(delta, ct)) break;
            }
            return true;
        }, token);
    }

    public async ValueTask CenterAsync(double tolerance, CancellationToken token)
    {
        if (!double.IsFinite(tolerance) || tolerance is < 0 or > 0.5) throw new ArgumentOutOfRangeException(nameof(tolerance));
        await RunAsync(async ct =>
        {
            double limit = tolerance == 0 ? _rules.CenterTolerance : tolerance;
            var offset = View.Geometry.CenterOffset;
            if (Math.Abs(offset.X - 0.5) > limit || Math.Abs(offset.Y - 0.5) > limit)
                await SwipeCoreAsync(default, ct);
            return true;
        }, token);
    }

    public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return RunAsync(async ct =>
        {
            _observation ??= await _recognition.ObserveAsync(View, Position, token: ct);
            return _observation with { Mode = mode };
        }, token);
    }
    public async ValueTask RefreshAsync(bool waitSwipe = false, CancellationToken token = default)
        => await RunAsync(async ct => { await UpdateCoreAsync(waitSwipe, ct); return true; }, token);

    public async ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token)
        => await EnsureEdgesAsync(skipFirstUpdate, reverse: false, preset: null, new(3, 2), token);

    public ValueTask<IReadOnlyList<ViewCell>> EnsureEdgesAsync(bool skipFirstUpdate, bool reverse,
        ViewCell? preset, ViewCell swipeLimit, CancellationToken token = default)
    {
        if (swipeLimit.X < 1 || swipeLimit.Y < 1) throw new ArgumentOutOfRangeException(nameof(swipeLimit));
        return RunAsync<IReadOnlyList<ViewCell>>(async ct =>
        {
            string corner = _rules.EdgeCorner.ToLowerInvariant();
            int xSign = _random.NextDouble() > 0.5 ? 1 : -1, ySign = _random.NextDouble() > 0.5 ? 1 : -1;
            if (corner.Contains("left", StringComparison.Ordinal)) xSign = -1;
            else if (corner.Contains("right", StringComparison.Ordinal)) xSign = 1;
            if (corner.Contains("upper", StringComparison.Ordinal)) ySign = -1;
            else if (corner.Contains("bottom", StringComparison.Ordinal)) ySign = 1;
            var record = new List<ViewCell>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (record.Count == 0)
                {
                    if (!skipFirstUpdate) await UpdateCoreAsync(false, ct);
                    if (preset is { } first) { await SwipeCoreAsync(first, ct); record.Add(first); }
                }
                var edge = View.Geometry.Edges;
                var delta = new ViewCell(edge.Left || edge.Right ? 0 : swipeLimit.X * xSign,
                    edge.Lower || edge.Upper ? 0 : swipeLimit.Y * ySign);
                // The first requested direction is recorded without moving unless a preset ran.
                if (record.Count > 0) await SwipeCoreAsync(delta, ct);
                record.Add(delta);
                if (delta == default) break;
            }
            if (reverse)
                foreach (var delta in record.AsEnumerable().Reverse())
                    if (delta != default) await SwipeCoreAsync(new(-delta.X, -delta.Y), ct);
            return record.AsReadOnly();
        }, token);
    }

    private async ValueTask<bool> SwipeCoreAsync(ViewCell requested, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var vector = _camera.PrepareSwipe(requested);
        return await SwipeVectorCoreAsync(vector, token);
    }

    private async ValueTask<bool> SwipeVectorCoreAsync(ScreenPoint vector, CancellationToken token, MapViewFrame? partial = null)
    {
        var view = partial ?? View;
        if (Math.Abs(vector.X) <= _rules.SwipeDrop && Math.Abs(vector.Y) <= _rules.SwipeDrop) return false;
        IReadOnlyList<PixelArea>? preferred = null, forbidden = null;
        if (_rules.Optimize)
        {
            // View.load creates fresh unpredicted grids before reporting an outside center.
            var observation = partial is null ? _observation ??= await _recognition.ObserveAsync(view, Position, token: token) :
                new MapObservation(view.Geometry.Grids.Select(g => new MapCellObservation(g.LocalCell, new())).ToArray(), Position, view.Geometry.Center);
            (preferred, forbidden) = SwipeAreas(_map, Position, view.Geometry, observation, vector);
        }
        var basis = view.Geometry.SwipeBase;
        var pixels = new ScreenPoint(-basis.X * _multiply.X * vector.X, -basis.Y * _multiply.Y * vector.Y);
        await _input.SwipeAsync(new(pixels, new(123, 159, 1052, 469), preferred, forbidden), token);
        await UpdateCoreAsync(true, token);
        return true;
    }

    private async ValueTask UpdateCoreAsync(bool waitSwipe, CancellationToken token)
    {
        long start = _clock.GetTimestamp(), sequence = View.Frame.Sequence;
        var errors = new IntervalTimer(_clock, 5, 10); errors.Reset();
        var previous = _camera.Previous?.Geometry.CenterOffset;
        bool swiped = true;
        MapViewFrame next;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { next = await _source.CaptureAsync(token); }
            catch (MapInterruptionHandledException) { continue; }
            catch (CameraOutsideViewException error) when (error.PartialView is { } partial)
            {
                if (partial.Frame.Sequence <= sequence) throw new InvalidDataException("Outside-map capture reused a stale frame");
                try
                {
                    // Native _map_swipe corrects the visual vector without replacing the pending map displacement.
                    if (!await SwipeVectorCoreAsync(new(-error.Offset.X, -error.Offset.Y), token, partial)) throw;
                    next = View;
                }
                catch (MapGeometryException) { if (errors.Reached()) throw; else continue; }
            }
            catch (MapGeometryException) { if (errors.Reached()) throw; else continue; }
            if (next.Frame.Sequence <= sequence) throw new InvalidDataException("Map capture reused a stale frame");
            sequence = next.Frame.Sequence;
            if (!waitSwipe || _clock.GetElapsedTime(start) > TimeSpan.FromSeconds(0.35)) break;
            var offset = next.Geometry.CenterOffset;
            if (previous is { } old && double.Hypot(offset.X - old.X, offset.Y - old.Y) < 0.001) swiped = false;
            bool centered = Math.Abs(offset.X - 0.5) <= _rules.CenterTolerance && Math.Abs(offset.Y - 0.5) <= _rules.CenterTolerance;
            if (centered && swiped) break;
            if (!centered) swiped = true;
            errors.Reset();
        }
        await _camera.UpdateAsync(next, _predictor, _rules.Predict, _rules.PredictCurrentFleet, _rules.PredictSeaGrids, token);
        _observation = await _recognition.ObserveAsync(next, Position, token: token);
    }

    public static (IReadOnlyList<PixelArea> Preferred, IReadOnlyList<PixelArea> Forbidden) SwipeAreas(
        CampaignState map, Cell position, MapViewGeometry geometry, MapObservation observation, ScreenPoint vector)
    {
        if (observation.Camera != position || observation.LocalCenter != geometry.Center ||
            observation.Cells.Count != geometry.Grids.Length || observation.Cells.Any(g => !geometry.Projections.ContainsKey(g.LocalCell)) ||
            observation.Cells.Select(g => g.LocalCell).Distinct().Count() != observation.Cells.Count ||
            !double.IsFinite(vector.X) || !double.IsFinite(vector.Y))
            throw new ArgumentException("Swipe avoidance requires matching camera geometry and observations");
        PixelArea Moved(GridGeometry grid, int padding)
        {
            var corners = new GridCorners(grid.GridToScreen(new(-vector.X, -vector.Y)),
                grid.GridToScreen(new(1 - vector.X, -vector.Y)), grid.GridToScreen(new(-vector.X, 1 - vector.Y)),
                grid.GridToScreen(new(1 - vector.X, 1 - vector.Y)));
            return GridGeometry.PaddedArea(corners, padding);
        }
        var whitelist = new List<PixelArea>();
        foreach (var cell in map.Cells.Where(g => g.IsLand || g.IsCurrentFleet)
                     .OrderBy(g => Math.Abs((long)g.Location.Column - position.Column) + Math.Abs((long)g.Location.Row - position.Row)))
        {
            var local = new ViewCell(checked(cell.Location.Column - position.Column + geometry.Center.X),
                checked(cell.Location.Row - position.Row + geometry.Center.Y));
            if (geometry.Projections.TryGetValue(local, out var grid)) whitelist.Add(Moved(grid, 25));
        }
        var blacklist = observation.Cells.Where(g => g.State.IsEnemy || g.State.IsSiren || g.State.IsBoss ||
            g.State.IsMystery || g.State.IsFleet && !g.State.IsCurrentFleet).Select(g => geometry.Projections[g.LocalCell]).ToArray();
        return (whitelist.AsReadOnly(), blacklist.Select(g => g.Outer).Concat(blacklist.Select(g => Moved(g, -5))).ToArray());
    }

    private async ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(_timeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        bool entered = false;
        try
        {
            await _gate.WaitAsync(linked.Token); entered = true;
            if (_faulted) throw new InvalidOperationException("Camera state is uncertain; create a freshly localized camera");
            linked.Token.ThrowIfCancellationRequested();
            var result = await operation(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (entered) _faulted = true;
            if (error is OperationCanceledException && !token.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException("Map camera operation exceeded its time limit", error);
            throw;
        }
        finally { if (entered) _gate.Release(); }
    }
}
