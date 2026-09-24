using System.Globalization;
using System.Threading.Channels;

namespace Alas.Server;

/// <summary>
/// One sampler for all observers. Each observer holds at most one pending full
/// snapshot, so slow/disconnected UI clients cannot accumulate a journal or block
/// runtime work. This is a display feed, not a replayable task audit log.
/// </summary>
internal sealed class ControlStateFeed : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<string> _readState;
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private readonly HashSet<Subscription> _subscribers = [];
    private readonly CancellationTokenSource _stop;
    private readonly Task _sampler;
    private Snapshot? _latest;
    private long _revision;

    public ControlStateFeed(Func<string> readState, CancellationToken stopping)
    {
        _readState = readState;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _sampler = Task.Run(SampleAsync);
    }

    public Subscription Subscribe()
    {
        lock (_gate)
        {
            _stop.Token.ThrowIfCancellationRequested();
            // The first observer needs a fresh state after any idle interval.
            if (_subscribers.Count == 0) Refresh();
            var subscription = new Subscription(this);
            _subscribers.Add(subscription);
            subscription.Pending.Writer.TryWrite(_latest!);
            return subscription;
        }
    }

    private void Refresh()
    {
        string json = _readState();
        if (_latest?.Json == json) return;
        _latest = new Snapshot(_epoch + ":" + (++_revision).ToString(CultureInfo.InvariantCulture), json);
        foreach (var subscriber in _subscribers) subscriber.Pending.Writer.TryWrite(_latest);
    }

    private async Task SampleAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                lock (_gate)
                {
                    if (_subscribers.Count == 0) continue;
                    try { Refresh(); }
                    catch (Exception error)
                    {
                        // Report a lost feed rather than silently presenting stale state.
                        foreach (var subscriber in _subscribers) subscriber.Pending.Writer.TryComplete(error);
                        _subscribers.Clear();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                foreach (var subscriber in _subscribers) subscriber.Pending.Writer.TryComplete();
                _subscribers.Clear();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _sampler;
        _stop.Dispose();
    }

    internal sealed record Snapshot(string Cursor, string Json);

    internal sealed class Subscription(ControlStateFeed owner) : IDisposable
    {
        internal Channel<Snapshot> Pending { get; } = Channel.CreateBounded<Snapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._subscribers.Remove(this);
                Pending.Writer.TryComplete();
            }
        }
    }
}
