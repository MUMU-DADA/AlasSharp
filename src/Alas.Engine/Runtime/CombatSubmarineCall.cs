using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record SubmarineCallAttempt(long FrameSequence, bool ReadyIcon, bool Completed);
public sealed record SubmarineCallEvidence(SubmarineMode Mode, string State, long? StartedFrame,
    long? LastFrame, long? ObservedFrame, IReadOnlyList<SubmarineCallAttempt> Attempts);

/// <summary>Native SubmarineCall: five-second opportunity, shared one-second click interval,
/// two availability checks, and independent confirmation of the called icon.</summary>
public sealed class CombatSubmarineCall(IUiDriver ui, Func<long> frameSequence, SubmarineMode mode,
    IntervalTimer? clickTimer = null)
{
    public static readonly SourceFile Source = new("module/combat/submarine.py", "949bc37aa8ca37bc4604879d7e026f93c003c075182ee7ff118235e2070d82c0");
    private readonly IntervalTimer _window = new(ui.Clock, 5);
    private readonly IntervalTimer _click = clickTimer ?? new(ui.Clock, 1);
    private readonly List<SubmarineCallAttempt> _attempts = [];
    private string _state = "not_started";
    private long? _startedFrame, _lastFrame, _observedFrame;
    public SubmarineCallEvidence Evidence => new(mode, _state, _startedFrame, _lastFrame, _observedFrame, _attempts.ToArray());

    public void Begin()
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_state != "not_started") throw new InvalidOperationException("Submarine call belongs to one battle");
        long sequence = frameSequence();
        if (!ui.HasFrame || sequence <= 0) throw new InvalidDataException("Submarine call requires the battle loading frame");
        _startedFrame = sequence;
        _window.Reset();
        _state = "waiting";
    }

    public async ValueTask<bool> HandleAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_state is "not_started" or "failed") throw new InvalidOperationException("Submarine call is not active");
        if (_state != "waiting") return false;
        try
        {
            long sequence = frameSequence();
            if (!ui.HasFrame || sequence < _startedFrame || _lastFrame is { } previous && sequence <= previous)
                throw new InvalidDataException("Submarine call received a stale frame");
            _lastFrame = sequence;
            if (mode is SubmarineMode.DoNotUse or SubmarineMode.HuntOnly or SubmarineMode.HuntAndBoss)
            { _state = "disabled"; return false; }
            if (_window.Reached()) { _state = "window_expired"; return false; }
            if (!await ui.AppearsAsync(UiAssets.Combat.SUBMARINE_AVAILABLE_CHECK_1, token: token) ||
                !await ui.AppearsAsync(UiAssets.Combat.SUBMARINE_AVAILABLE_CHECK_2, token: token)) return false;
            if (await ui.AppearsAsync(UiAssets.Combat.SUBMARINE_CALLED, token: token))
            { _observedFrame = sequence; _state = "called_observed"; return false; }
            if (!_click.Reached()) return false;
            bool ready = await ui.AppearsAsync(UiAssets.Combat.SUBMARINE_READY, token: token);
            _attempts.Add(new(sequence, ready, false));
            // Native clicks this fixed asset even if the ready icon is incorrect, after BOTH checks passed.
            await ui.ClickAsync(UiAssets.Combat.SUBMARINE_READY, token);
            _attempts[^1] = _attempts[^1] with { Completed = true };
            _click.Reset();
            return true;
        }
        catch { _state = "failed"; throw; }
    }

    public void End()
    {
        if (_state == "waiting") _state = "battle_ended_unconfirmed";
    }
    public void Fail() => _state = "failed";
}
