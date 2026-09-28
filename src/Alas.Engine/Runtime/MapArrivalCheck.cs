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
    ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default);
    ValueTask RelocalizeAsync(CancellationToken token = default);
    ValueTask AnchorAtAsync(Cell location, CancellationToken token = default);
    void Suspend();
    void Invalidate();
}

public enum MapArrivalOutcome { MarkerConfirmed, MapInterrupted, Unconfirmed, StageReturned, WalkOutOfStep }
public sealed record MapArrivalResult(MapArrivalOutcome Outcome, long FrameSequence, int FreshFrames,
    MapEncounterKind Encounter = MapEncounterKind.None)
{
    public ImmutableArray<MapEncounterKind> HandledEncounters { get; init; } = [];
    public ImmutableArray<CombatFlowResult> Combats { get; init; } = [];
    public ImmutableArray<long> AmmoNotificationFrames { get; init; } = [];
    public ImmutableArray<MapAmbushResult> Ambushes { get; init; } = [];
    public ImmutableArray<MapCarrierResult> Carriers { get; init; } = [];
    public bool LastMysteryWasCarrier { get; init; }
    public bool CarriersConfirmed => (!LastMysteryWasCarrier || !Carriers.IsEmpty) &&
        Carriers.Length == HandledEncounters.Count(kind => kind == MapEncounterKind.CarrierSpawn) &&
        Carriers.All(carrier => carrier.IsConsistent);
    public int RetryTaps { get; init; }
    public bool AmbushesConfirmed => Ambushes.Length == HandledEncounters.Count(kind => kind == MapEncounterKind.Ambush) &&
        Ambushes.All(ambush => ambush.CanContinue);
    public bool SupplyClickCompleted { get; init; }
    public ImmutableArray<WalkTimeoutEvidence> WalkTimeouts { get; init; } = [];
}
public sealed record WalkTimeoutEvidence(int Fleet, Cell Target, long ObservedFrame,
    long? RecoveredFrame = null, long? RetapFrame = null, bool RetapCompleted = false);
public sealed record MapArrivalOptions(TimeSpan ConfirmDelay, TimeSpan WalkTimeout, bool AllowCurrentMarker = false)
{
    public TimeSpan? AfterCombatConfirmDelay { get; init; }
    public bool ExpectCombat { get; init; }
    public bool ExpectMystery { get; init; }
    public bool ExpectedBoss { get; init; }
    public static MapArrivalOptions Default { get; } = new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(20));
}

public enum MapEncounterContinuation { Unhandled, InMap, InStage }
public sealed record MapEncounterHandling(MapEncounterContinuation Continuation, CombatFlowResult? Combat = null,
    MapAmbushResult? Ambush = null, MapCarrierResult? Carrier = null);

public interface IMapEncounterHandler
{
    ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token);
}

/// <summary>Checks a clicked cell against fresh map frames; no sortie state changes until a caller handles all interactions.</summary>
public sealed class MapArrivalCheck(IMapArrivalCamera camera, CampaignState state,
    Func<CancellationToken, ValueTask<bool>> isInMap, TimeProvider? clock = null,
    IMapEncounterProbe? probe = null, IMapEncounterHandler? handler = null,
    Func<CancellationToken, ValueTask>? recoverAfterCombat = null, MapWalkPopups? walkPopups = null,
    Func<CancellationToken, ValueTask>? recoverAfterWalkTimeout = null,
    Func<long, CancellationToken, ValueTask<WalkInterruptionResult>>? walkInterruptions = null)
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<WalkTimeoutEvidence> _walkTimeouts = [];
    public IReadOnlyList<WalkTimeoutEvidence> WalkTimeouts => _walkTimeouts.AsReadOnly();
    private int _started;

    public async ValueTask<MapArrivalResult> TapAndCheckAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
    {
        if (!state.Contains(destination)) throw new ArgumentOutOfRangeException(nameof(destination));
        options ??= MapArrivalOptions.Default;
        if (options.ConfirmDelay < TimeSpan.Zero || options.WalkTimeout <= TimeSpan.Zero ||
            options.ConfirmDelay >= options.WalkTimeout || options.WalkTimeout.TotalMilliseconds > int.MaxValue ||
            options.AfterCombatConfirmDelay is { } afterCombat && (afterCombat < TimeSpan.Zero || afterCombat >= options.WalkTimeout))
            throw new ArgumentOutOfRangeException(nameof(options));
        if ((options.ExpectCombat || options.ExpectMystery) && options.ConfirmDelay + TimeSpan.FromSeconds(1) >= options.WalkTimeout)
            throw new ArgumentOutOfRangeException(nameof(options), "Unexpected-arrival confirmation must fit the walk deadline");
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("An arrival check belongs to one movement attempt");
        bool portal = state[destination].IsPortal;
        Cell? portalExit = portal
            ? state[destination].PortalLink ?? throw new InvalidDataException("Portal has no linked exit") : null;
        bool submarineAbove = destination.Row > 1 && state.SubmarineLocation == new Cell(destination.Column, destination.Row - 1);
        var walk = new IntervalTimer(_clock, options.WalkTimeout.TotalSeconds);
        var confirm = new IntervalTimer(_clock, options.ConfirmDelay.TotalSeconds, count: 2);
        var unexpected = new IntervalTimer(_clock, options.ConfirmDelay.TotalSeconds + 1, count: 6);
        var ambushedRetry = new IntervalTimer(_clock, options.ConfirmDelay.TotalSeconds, count: 2);
        long sequence = camera.FrameSequence;
        int frames = 0;
        bool confirmed = false;
        bool stepInterrupted = false;
        bool supplyClickCompleted = false;
        bool waitingForPopup = false;
        var handled = ImmutableArray.CreateBuilder<MapEncounterKind>();
        var combats = ImmutableArray.CreateBuilder<CombatFlowResult>();
        var ammoFrames = ImmutableArray.CreateBuilder<long>();
        var ambushes = ImmutableArray.CreateBuilder<MapAmbushResult>();
        var carriers = ImmutableArray.CreateBuilder<MapCarrierResult>();
        bool lastMysteryWasCarrier = false;
        int retryTaps = 0;
        MapArrivalResult Result(MapArrivalOutcome outcome, MapEncounterKind encounter = MapEncounterKind.None)
            => new(outcome, sequence, frames, encounter)
            { HandledEncounters = handled.ToImmutable(), Combats = combats.ToImmutable(),
                AmmoNotificationFrames = ammoFrames.ToImmutable(), SupplyClickCompleted = supplyClickCompleted,
                Ambushes = ambushes.ToImmutable(), RetryTaps = retryTaps,
                Carriers = carriers.ToImmutable(), LastMysteryWasCarrier = lastMysteryWasCarrier,
                WalkTimeouts = _walkTimeouts.ToImmutableArray() };
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
                bool retryTap = false;
                bool walkTimedOut = false;
                walk.Reset();
                confirm.Clear();
                unexpected.Clear();
                using (var deadline = new CancellationTokenSource(options.WalkTimeout, _clock))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
                {
                    try
                    {
                        while (true)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            // Bound one capture/inspection iteration independently of the walk timer:
                            // a stable current marker can finish its confirmation after walk_timeout.
                            deadline.CancelAfter(options.WalkTimeout);
                            if (portal) await camera.RelocalizeAsync(linked.Token);
                            else await camera.RefreshImageAsync(linked.Token);
                            if (camera.FrameSequence <= sequence)
                                throw new InvalidDataException("Arrival check reused a stale map frame");
                            sequence = camera.FrameSequence;
                            frames++;
                            if (walkInterruptions is not null)
                            {
                                // Retirement can take longer than a movement observation. The enclosing
                                // task owns its deadline; it must not inherit the short capture timeout.
                                linked.Token.ThrowIfCancellationRequested();
                                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                                var interruption = await walkInterruptions(sequence, token);
                                token.ThrowIfCancellationRequested();
                                deadline.CancelAfter(options.WalkTimeout);
                                if (camera.FrameSequence < sequence)
                                    throw new InvalidDataException("Walk interruption restored an older camera frame");
                                sequence = camera.FrameSequence;
                                if (interruption.ResetWalk) walk.Reset();
                                if (interruption.OffMap) waitingForPopup = true;
                            }
                            encounter = probe is null ? MapEncounterKind.None : await probe.InspectAsync(sequence, linked.Token);
                            // Native ammo messages do not interrupt movement or
                            // require re-localization. Retain each throttled
                            // observation for the caller's mystery accounting.
                            if (encounter == MapEncounterKind.AmmoNotification)
                            {
                                ammoFrames.Add(sequence);
                                lastMysteryWasCarrier = false;
                                encounter = await probe!.InspectAfterMysteryAsync(sequence, linked.Token);
                            }
                            if (encounter is MapEncounterKind.CatAttack or MapEncounterKind.GuildPopup && walkPopups is not null)
                            {
                                await walkPopups.HandleAsync(encounter, sequence, linked.Token);
                                handled.Add(encounter);
                                // Native cat attacks reset arrival confirmation; guild popups preserve it.
                                if (encounter == MapEncounterKind.CatAttack) { confirm.Reset(); unexpected.Reset(); }
                                walk.Reset();
                                deadline.CancelAfter(options.WalkTimeout);
                                waitingForPopup = true;
                                continue;
                            }
                            if (encounter == MapEncounterKind.None)
                            {
                                if (await isInMap(linked.Token)) waitingForPopup = false;
                                else if (waitingForPopup)
                                {
                                    if (confirm.Started) { confirm.Reset(); unexpected.Reset(); }
                                    if (walk.Reached()) { walkTimedOut = true; break; }
                                    continue;
                                }
                                else encounter = MapEncounterKind.UnknownPage;
                            }
                            if (encounter != MapEncounterKind.None) break;
                            var marker = portal ? await camera.ReadCenterMarkerAsync(linked.Token) :
                                await camera.ReadFleetMarkerAsync(destination, linked.Token);
                            bool present = (submarineAbove ? marker.Current : marker.Fleet) ||
                                options.AllowCurrentMarker && (marker.Fleet || marker.Current) ||
                                walk.Reached() && marker.Current;
                            linked.Token.ThrowIfCancellationRequested();
                            if (present)
                            {
                                if (!confirm.Started) { confirm.Reset(); unexpected.Reset(); }
                                bool expected = (!options.ExpectCombat || combats.Count > 0) &&
                                    (!options.ExpectMystery || handled.Contains(MapEncounterKind.ItemPopup) || ammoFrames.Count > 0 || carriers.Count > 0);
                                if (confirm.Reached() && (expected || unexpected.Reached()))
                                {
                                    if (portal) await camera.AnchorAtAsync(portalExit!.Value, linked.Token);
                                    var arrivedCell = portalExit ?? destination;
                                    if (state[arrivedCell].MayAmmo)
                                    {
                                        await camera.TapCellAsync(arrivedCell, linked.Token);
                                        supplyClickCompleted = true;
                                    }
                                    confirmed = true;
                                    return Result(MapArrivalOutcome.MarkerConfirmed);
                                }
                                // Native waits for a stable marker or the expected-result grace timer
                                // even after walk_timeout. A current marker does not prove combat success.
                                continue;
                            }
                            else
                            {
                                if (confirm.Started) { confirm.Clear(); unexpected.Clear(); }
                                if (ambushedRetry.Started && ambushedRetry.Reached()) { retryTap = true; break; }
                            }
                            if (walk.Reached()) { walkTimedOut = true; break; }
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                    { return Result(MapArrivalOutcome.Unconfirmed); }
                }
                int? timeoutIndex = null;
                if (walkTimedOut)
                {
                    timeoutIndex = _walkTimeouts.Count;
                    _walkTimeouts.Add(new(state.FleetIndex, destination, sequence));
                    if (recoverAfterWalkTimeout is null) return Result(MapArrivalOutcome.Unconfirmed);
                    camera.Suspend();
                    await recoverAfterWalkTimeout(token);
                    if (camera.FrameSequence <= sequence)
                        throw new InvalidDataException("Walk timeout recovery reused a stale map frame");
                    sequence = camera.FrameSequence;
                    _walkTimeouts[timeoutIndex.Value] = _walkTimeouts[timeoutIndex.Value] with { RecoveredFrame = sequence };
                    waitingForPopup = false;
                    retryTap = true;
                }
                if (retryTap)
                {
                    await camera.PrepareTapAsync(destination, token);
                    sequence = camera.FrameSequence;
                    if (probe is not null) await probe.InitializeAsync(sequence, token);
                    if (timeoutIndex is { } index)
                        _walkTimeouts[index] = _walkTimeouts[index] with { RetapFrame = sequence };
                    await camera.TapCellAsync(destination, token);
                    if (timeoutIndex is { } completed)
                        _walkTimeouts[completed] = _walkTimeouts[completed] with { RetapCompleted = true };
                    sequence = camera.FrameSequence;
                    retryTaps++;
                    ambushedRetry.Clear();
                    continue;
                }
                if (encounter == MapEncounterKind.WalkOutOfStep)
                {
                    // The original tap has not established arrival. Only the movement recovery may resume this camera.
                    camera.Suspend();
                    stepInterrupted = true;
                    return Result(MapArrivalOutcome.WalkOutOfStep, encounter);
                }
                if (handler is null) return Result(MapArrivalOutcome.MapInterrupted, encounter);
                camera.Suspend();
                state.EncounterExpectedBoss = encounter == MapEncounterKind.Combat && options.ExpectedBoss;
                MapEncounterHandling resolution;
                try { resolution = await handler.HandleAsync(encounter, token); }
                finally { state.EncounterExpectedBoss = false; }
                if (!Enum.IsDefined(resolution.Continuation))
                    throw new InvalidDataException("Encounter handler returned an unknown continuation");
                if (resolution.Continuation == MapEncounterContinuation.Unhandled)
                    return Result(MapArrivalOutcome.MapInterrupted, encounter);
                if (resolution.Carrier is not null && encounter != MapEncounterKind.CarrierSpawn)
                    throw new InvalidDataException("Unrelated interaction returned carrier evidence");
                if (encounter == MapEncounterKind.Combat)
                {
                    if (resolution.Ambush is not null || resolution.Combat is not { } combat ||
                        combat.Return != (resolution.Continuation == MapEncounterContinuation.InStage
                            ? CombatReturn.InStage : CombatReturn.InMap))
                        throw new InvalidDataException("Combat encounter has no matching C# battle result");
                    combats.Add(combat);
                    if (options.AfterCombatConfirmDelay is { } delay)
                        confirm = new IntervalTimer(_clock, delay.TotalSeconds, count: 2);
                }
                else if (encounter == MapEncounterKind.Ambush)
                {
                    if (resolution.Combat is not null || resolution.Ambush is not { IsConsistent: true } ambush ||
                        (ambush.Combat?.Return == CombatReturn.InStage) != (resolution.Continuation == MapEncounterContinuation.InStage))
                        throw new InvalidDataException("Ambush handler returned inconsistent encounter evidence");
                    ambushes.Add(ambush);
                    state.RecordAmbushEncounter(new(state.FleetIndex, destination, ambush));
                }
                else if (encounter == MapEncounterKind.CarrierSpawn)
                {
                    if (resolution.Combat is not null || resolution.Ambush is not null ||
                        resolution.Continuation != MapEncounterContinuation.InMap || resolution.Carrier is not { IsConsistent: true } carrier)
                        throw new InvalidDataException("Carrier handler returned inconsistent observation evidence");
                    carriers.Add(carrier);
                    lastMysteryWasCarrier = true;
                    state.RecordCarrierEncounter(new(state.FleetIndex, destination, carrier));
                }
                else if (resolution.Ambush is not null || resolution.Combat is not null || resolution.Continuation == MapEncounterContinuation.InStage)
                    throw new InvalidDataException("Noncombat interaction returned battle or stage evidence");
                if (encounter == MapEncounterKind.ItemPopup && state.Rule?.CountMysteryItems != false) lastMysteryWasCarrier = false;
                handled.Add(encounter);
                if (resolution.Continuation == MapEncounterContinuation.InStage)
                    return Result(MapArrivalOutcome.StageReturned, encounter);
                if (resolution.Ambush is { CanContinue: false }) return Result(MapArrivalOutcome.MapInterrupted, encounter);
                if (resolution.Combat is { Return: CombatReturn.InMap, Rank.IsWinningRank: true } && recoverAfterCombat is not null)
                    await recoverAfterCombat(token);
                else await camera.RelocalizeAsync(token);
                if (camera.FrameSequence <= sequence)
                    throw new InvalidDataException("Interaction recovery reused a stale map frame");
                sequence = camera.FrameSequence;
                if (probe is not null) await probe.InitializeAsync(sequence, token);
                if (resolution.Ambush is { FleetStatusRefreshed: true })
                {
                    var marker = portal ? await camera.ReadCenterMarkerAsync(token) : await camera.ReadFleetMarkerAsync(destination, token);
                    if (!(marker.Fleet && marker.Current)) ambushedRetry.Reset();
                }
            }
        }
        finally { if (!confirmed && !stepInterrupted) camera.Invalidate(); }
    }
}
