using System.Collections.Immutable;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record FleetEmotionRecord(int Value, DateTimeOffset RecordedAt, FleetEmotionSettings Settings);
public sealed record EmotionSnapshot(string Revision, ImmutableArray<FleetEmotionRecord> Fleets);
public interface IEmotionStore
{
    ValueTask<EmotionSnapshot> ReadAsync(CancellationToken token);
    ValueTask<EmotionSnapshot> SaveAsync(EmotionSnapshot expected, ImmutableArray<int> values,
        DateTimeOffset recordedAt, DateTimeOffset? nextRun, CancellationToken token);
}
public interface ICampaignEmotionService
{
    ValueTask<EmotionEntryEvidence?> PrepareAsync(CampaignConfiguration configuration, int battles,
        bool alreadyInMap, CancellationToken token);
}
public interface ICombatEmotion
{
    ValueTask WaitAsync(CancellationToken token);
    ValueTask ReduceAsync(CancellationToken token);
}
public sealed record EmotionEntryEvidence(DateTimeOffset CheckedAt, DateTimeOffset? DeferredUntil,
    ImmutableArray<int> Values, int ExpectedFirst, int ExpectedSecond);
public sealed record EmotionEvent(string Operation, DateTimeOffset At, int? Fleet, ImmutableArray<int> Values,
    DateTimeOffset? RecoveryAt, long? BattleSequence, bool Saved);

/// <summary>Session-owned native emotion estimates, persisted before actions and immediately upon battle loading.</summary>
public sealed class CampaignEmotion(IEmotionStore store, TimeProvider clock,
    Func<TimeSpan, CancellationToken, ValueTask>? delay = null)
{
    private readonly List<EmotionEvent> _events = [];
    public IReadOnlyList<EmotionEvent> Evidence => _events.ToArray();
    public int TotalReduced { get; private set; }
    public async ValueTask ValidateAsync(CancellationToken token) => Validate(await store.ReadAsync(token));
    private static void Validate(EmotionSnapshot snapshot)
    {
        if (snapshot.Fleets.IsDefault || snapshot.Fleets.Length != 2)
            throw new InvalidDataException("Emotion state requires both fleets");
        foreach (var fleet in snapshot.Fleets) fleet.Settings.Validate();
    }
    private static ImmutableArray<int> Update(EmotionSnapshot snapshot, DateTimeOffset now)
        => snapshot.Fleets.Select(f => EmotionRules.Recover(f.Value, f.RecordedAt, now, f.Settings)).ToImmutableArray();
    private async ValueTask RecordAsync(string operation, EmotionSnapshot snapshot, ImmutableArray<int> values,
        DateTimeOffset now, int? fleet, DateTimeOffset? recovered, DateTimeOffset? nextRun,
        long? battleSequence, CancellationToken token)
    {
        int index = _events.Count;
        _events.Add(new(operation, now, fleet, values, recovered, battleSequence, false));
        await store.SaveAsync(snapshot, values, now, nextRun, token);
        _events[index] = _events[index] with { Saved = true };
    }
    public async ValueTask<EmotionEntryEvidence> CheckEntryAsync(int battles, FleetOrder order,
        CancellationToken token, bool mapDoubleBook = false, bool requestedDoubleBook = false)
    {
        var snapshot = await store.ReadAsync(token); Validate(snapshot);
        var now = clock.GetUtcNow();
        var values = Update(snapshot, now);
        var reductions = EmotionRules.ExpectedReduction(battles, order, mapDoubleBook, requestedDoubleBook);
        var first = EmotionRules.RecoveredAt(values[0], reductions.First, now, snapshot.Fleets[0].Settings);
        var second = EmotionRules.RecoveredAt(values[1], reductions.Second, now, snapshot.Fleets[1].Settings);
        var recovered = first > second ? first : second;
        DateTimeOffset? deferred = recovered > now ? recovered : null;
        await RecordAsync("entry", snapshot, values, now, null, recovered, deferred, null, token);
        return new(now, deferred, values, reductions.First, reductions.Second);
    }
    public async ValueTask WaitAsync(int fleet, CancellationToken token, bool mapDoubleBook = false)
    {
        if (fleet is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(fleet));
        var snapshot = await store.ReadAsync(token); Validate(snapshot);
        var now = clock.GetUtcNow();
        var values = Update(snapshot, now);
        var recovered = EmotionRules.RecoveredAt(values[fleet - 1], mapDoubleBook ? 4 : 2, now, snapshot.Fleets[fleet - 1].Settings);
        await RecordAsync("wait", snapshot, values, now, fleet, recovered, null, null, token);
        if (recovered <= now) return;
        // Native wait uses strict greater-than and sixty-second polling. Cancellation remains responsive.
        while (clock.GetUtcNow() <= recovered)
        {
            token.ThrowIfCancellationRequested();
            if (delay is not null) await delay(TimeSpan.FromSeconds(60), token);
            else await Task.Delay(TimeSpan.FromSeconds(60), clock, token);
        }
    }
    public async ValueTask ReduceAsync(int fleet, long battleSequence, CancellationToken token, bool mapDoubleBook = false)
    {
        if (fleet is not (1 or 2) || battleSequence <= 0) throw new ArgumentOutOfRangeException(nameof(fleet));
        var snapshot = await store.ReadAsync(token); Validate(snapshot);
        var now = clock.GetUtcNow();
        var values = Update(snapshot, now).ToBuilder();
        int amount = mapDoubleBook ? 4 : 2;
        values[fleet - 1] -= amount;
        // Once combat is observed, cancellation must not erase its already-incurred cost.
        await RecordAsync("reduce", snapshot, values.ToImmutable(), now, fleet, null, null, battleSequence, CancellationToken.None);
        TotalReduced += amount;
    }
    public ICombatEmotion ForBattle(int fleet, Func<long> sequence, bool mapDoubleBook = false)
        => new Battle(this, fleet, sequence, mapDoubleBook);
    private sealed class Battle(CampaignEmotion owner, int fleet, Func<long> sequence, bool mapDoubleBook) : ICombatEmotion
    {
        private bool _reduced;
        public ValueTask WaitAsync(CancellationToken token) => owner.WaitAsync(fleet, token, mapDoubleBook);
        public async ValueTask ReduceAsync(CancellationToken token)
        {
            if (_reduced) throw new InvalidOperationException("Emotion cost already recorded for this battle");
            _reduced = true;
            await owner.ReduceAsync(fleet, sequence(), token, mapDoubleBook);
        }
    }
}
