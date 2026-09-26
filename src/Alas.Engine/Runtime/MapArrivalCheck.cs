using System.Collections.Immutable;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public interface IMapArrivalCamera
{
    long FrameSequence { get; }
    ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default);
    ValueTask TapCellAsync(Cell destination, CancellationToken token = default);
    ValueTask RefreshImageAsync(CancellationToken token = default);
    ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default);
    ValueTask RelocalizeAsync(CancellationToken token = default);
    void Suspend();
    void Invalidate();
}

public enum MapArrivalOutcome { MarkerConfirmed, MapInterrupted, Unconfirmed }
public sealed record MapArrivalResult(MapArrivalOutcome Outcome, long FrameSequence, int FreshFrames,
    MapEncounterKind Encounter = MapEncounterKind.None)
{
    public ImmutableArray<MapEncounterKind> HandledEncounters { get; init; } = [];
}
public sealed record MapArrivalOptions(TimeSpan ConfirmDelay, TimeSpan WalkTimeout, bool AllowCurrentMarker = false)
{
    public static MapArrivalOptions Default { get; } = new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(20));
}

public interface IMapEncounterHandler
{
    /// <summary>True only after C# has finished this interaction and the map can be relocalized.</summary>
    ValueTask<bool> HandleAsync(MapEncounterKind encounter, CancellationToken token);
}

/// <summary>Checks a clicked cell against fresh map frames; no sortie state changes until a caller handles all interactions.</summary>
public sealed class MapArrivalCheck(IMapArrivalCamera camera, CampaignState state,
    Func<CancellationToken, ValueTask<bool>> isInMap, TimeProvider? clock = null,
    IMapEncounterProbe? probe = null, IMapEncounterHandler? handler = null)
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _started;

    public async ValueTask<MapArrivalResult> TapAndCheckAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
    {
        if (!state.Contains(destination)) throw new ArgumentOutOfRangeException(nameof(destination));
        options ??= MapArrivalOptions.Default;
        if (options.ConfirmDelay < TimeSpan.Zero || options.WalkTimeout <= TimeSpan.Zero ||
            options.ConfirmDelay >= options.WalkTimeout || options.WalkTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("An arrival check belongs to one grid tap");
        bool submarineAbove = destination.Row > 1 && state.SubmarineLocation == new Cell(destination.Column, destination.Row - 1);
        var walk = new IntervalTimer(_clock, options.WalkTimeout.TotalSeconds);
        var confirm = new IntervalTimer(_clock, options.ConfirmDelay.TotalSeconds, count: 2);
        long sequence = camera.FrameSequence;
        int frames = 0;
        bool confirmed = false;
        var handled = ImmutableArray.CreateBuilder<MapEncounterKind>();
        MapArrivalResult Result(MapArrivalOutcome outcome, MapEncounterKind encounter = MapEncounterKind.None)
            => new(outcome, sequence, frames, encounter) { HandledEncounters = handled.ToImmutable() };
        try
        {
            await camera.PrepareTapAsync(destination, token);
            sequence = camera.FrameSequence;
            if (probe is not null) await probe.InitializeAsync(sequence, token);
            await camera.TapCellAsync(destination, token);
            sequence = camera.FrameSequence;
            while (true)
            {
                MapEncounterKind encounter = MapEncounterKind.None;
                walk.Reset();
                confirm.Clear();
                using (var deadline = new CancellationTokenSource(options.WalkTimeout, _clock))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
                {
                    try
                    {
                        while (true)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            await camera.RefreshImageAsync(linked.Token);
                            if (camera.FrameSequence <= sequence)
                                throw new InvalidDataException("Arrival check reused a stale map frame");
                            sequence = camera.FrameSequence;
                            frames++;
                            encounter = probe is null ? MapEncounterKind.None : await probe.InspectAsync(sequence, linked.Token);
                            if (encounter == MapEncounterKind.None && !await isInMap(linked.Token))
                                encounter = MapEncounterKind.UnknownPage;
                            if (encounter != MapEncounterKind.None) break;
                            var marker = await camera.ReadFleetMarkerAsync(destination, linked.Token);
                            bool present = (submarineAbove ? marker.Current : marker.Fleet) ||
                                options.AllowCurrentMarker && (marker.Fleet || marker.Current);
                            if (present)
                            {
                                if (!confirm.Started) confirm.Reset();
                                if (confirm.Reached())
                                {
                                    confirmed = true;
                                    return Result(MapArrivalOutcome.MarkerConfirmed);
                                }
                            }
                            else if (confirm.Started) confirm.Clear();
                            if (walk.Reached()) return Result(MapArrivalOutcome.Unconfirmed);
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                    { return Result(MapArrivalOutcome.Unconfirmed); }
                }
                if (handler is null) return Result(MapArrivalOutcome.MapInterrupted, encounter);
                camera.Suspend();
                if (!await handler.HandleAsync(encounter, token))
                    return Result(MapArrivalOutcome.MapInterrupted, encounter);
                handled.Add(encounter);
                await camera.RelocalizeAsync(token);
                if (camera.FrameSequence <= sequence)
                    throw new InvalidDataException("Interaction recovery reused a stale map frame");
                sequence = camera.FrameSequence;
                if (probe is not null) await probe.InitializeAsync(sequence, token);
            }
        }
        finally { if (!confirmed) camera.Invalidate(); }
    }
}
