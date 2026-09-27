using Alas.Engine.Rules;

namespace Alas.Engine.Navigation;

public sealed record UiSwitchState(string Id, AssetRule Check, AssetRule? Click = null);

/// <summary>Port of Switch.get/set: observe the requested state, including animation and unknown-state retries.</summary>
public sealed class UiSwitch
{
    public static readonly SourceFile Source = new("module/ui/switch.py",
        "f22a8dffcf577d3f1b6b4280a6e78cdef93e831fbddca9ac7186335890135636");
    private readonly IUiDriver _ui;
    private readonly UiSwitchState[] _states;
    private readonly ButtonOffset _offset;
    private readonly bool _selector;
    private readonly Func<CancellationToken, ValueTask<string?>>? _read;
    private readonly Func<long>? _frameSequence;

    public UiSwitch(IUiDriver ui, IEnumerable<UiSwitchState> states, ButtonOffset offset, bool selector = false,
        Func<CancellationToken, ValueTask<string?>>? read = null, Func<long>? frameSequence = null)
    {
        _ui = ui;
        _states = states.ToArray();
        if (_states.Length == 0 || _states.Any(state => string.IsNullOrWhiteSpace(state.Id) || state.Id == "unknown") ||
            _states.Select(state => state.Id).Distinct(StringComparer.Ordinal).Count() != _states.Length)
            throw new ArgumentException("Switch states must have unique, known names", nameof(states));
        _offset = offset;
        _selector = selector;
        _read = read;
        _frameSequence = frameSequence;
    }

    public async ValueTask<string?> ReadAsync(CancellationToken token = default)
    {
        if (_read is not null)
        {
            var value = await _read(token);
            if (value is not null) _ = State(value);
            return value;
        }
        foreach (var state in _states)
            if (await _ui.AppearsAsync(state.Check, _offset, token: token)) return state.Id;
        return null;
    }

    public async ValueTask<bool> SetAsync(string expected, TimeSpan timeout,
        bool skipFirstScreenshot = true, CancellationToken token = default)
    {
        _ = State(expected);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var limit = new IntervalTimer(_ui.Clock, timeout.TotalSeconds);
        var unknown = new IntervalTimer(_ui.Clock, 5, count: 10);
        var click = new IntervalTimer(_ui.Clock, 1, count: 2);
        limit.Reset(); unknown.Reset(); click.Clear();
        bool changed = false, hadUnknown = false, first = skipFirstScreenshot && _ui.HasFrame;
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (limit.Reached()) throw new TimeoutException("Switch did not reach " + expected);
                if (!first)
                {
                    long? previous = _ui.HasFrame ? _frameSequence?.Invoke() : null;
                    await _ui.ScreenshotAsync(linked.Token);
                    if (previous is { } sequence && _frameSequence!() <= sequence)
                        throw new InvalidDataException("Switch reused a stale screenshot");
                }
                first = false;
                string? current = await ReadAsync(linked.Token);
                if (current == expected) return changed;
                if (current is null)
                {
                    if (unknown.Reached()) { hadUnknown = true; unknown.Reset(); }
                    if (!hadUnknown) continue;
                }
                else unknown.Reset();
                if (!click.Reached()) continue;
                var state = State(_selector || current is null ? expected : current);
                await _ui.ClickAsync(state.Click ?? state.Check, linked.Token);
                changed = true;
                click.Reset(); unknown.Reset();
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Switch did not reach " + expected, error); }
    }

    private UiSwitchState State(string id) => _states.FirstOrDefault(state => state.Id == id)
        ?? throw new ArgumentException("Unknown switch state: " + id, nameof(id));
}
