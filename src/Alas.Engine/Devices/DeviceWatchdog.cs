using System.Collections.Immutable;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Devices;

public sealed record DeviceWatchdogEvidence(string Kind, long? FrameSequence, double Seconds, int Checks,
    ImmutableArray<string> Detections, ImmutableArray<string> Controls, bool? ApplicationRunning = null);
public sealed class GameStuckException() : Exception("Game did not progress within the native observation window");
public sealed class GameTooManyClicksException() : Exception("Repeated device controls exceeded the native history limits");

/// <summary>Device stuck/control checks. Time and screenshot-count gates both have to expire.</summary>
public sealed class DeviceWatchdog
{
    public static readonly SourceFile Source = new("module/device/device.py",
        "e425c4f0ff5da9bd5e03aff470292f3a97ecc4d709f5eb4e2d4021997d4cb787");
    public static readonly ImmutableArray<string> LongWait = ["BATTLE_STATUS_S", "PAUSE", "LOGIN_CHECK", "TEMPLATE_MANJUU"];
    private readonly IntervalTimer _normal, _long;
    private readonly HashSet<string> _detections = new(StringComparer.Ordinal);
    private readonly Queue<string> _controls = new();
    private readonly List<DeviceWatchdogEvidence> _evidence = [];
    private int _checks;
    public IReadOnlyList<DeviceWatchdogEvidence> Evidence => _evidence.AsReadOnly();
    public long? FrameSequence { get; set; }
    public DeviceWatchdog(TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        _normal = new(clock, 60, 60); _long = new(clock, 180, 180);
        ResetWait();
    }
    public void Observe(string name) { ArgumentException.ThrowIfNullOrWhiteSpace(name); _detections.Add(name); }
    public void ResetWait() { _detections.Clear(); _normal.Reset(); _long.Reset(); _checks = 0; }
    public void ResetControls() => _controls.Clear();
    public void Reset() { ResetWait(); ResetControls(); }
    public void ResetTask() { Reset(); _evidence.Clear(); FrameSequence = null; }
    public int RemoveControl(string name)
    {
        var keep = _controls.Where(value => value != name).ToArray();
        int removed = _controls.Count - keep.Length;
        _controls.Clear(); foreach (string value in keep) _controls.Enqueue(value);
        return removed;
    }
    public void BeforeControl(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ResetWait();
        if (_controls.Count == 15) _controls.Dequeue();
        _controls.Enqueue(name);
        var counts = _controls.GroupBy(value => value, StringComparer.Ordinal).Select(group => group.Count()).OrderDescending().Take(2).ToArray();
        if (counts[0] < 12 && !(counts.Length == 2 && counts[0] >= 6 && counts[1] >= 6)) return;
        _evidence.Add(new("repeated_controls", FrameSequence, 0, 0, [], _controls.ToImmutableArray()));
        ResetControls();
        throw new GameTooManyClicksException();
    }
    public async ValueTask BeforeCaptureAsync(IApplicationHealth application, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _checks++;
        bool reached = _normal.Reached(), longReached = _long.Reached();
        if (!reached || !longReached && LongWait.Any(_detections.Contains)) return;
        int entry = _evidence.Count;
        _evidence.Add(new("stuck", FrameSequence, _normal.Elapsed, _checks,
            _detections.Order(StringComparer.Ordinal).ToImmutableArray(), _controls.ToImmutableArray()));
        ResetWait();
        // Preserve the attempted check if inspecting the application itself fails.
        bool running = await application.IsRunningAsync(token);
        _evidence[entry] = _evidence[entry] with { ApplicationRunning = running };
        if (!running) throw new GameNotRunningException("Game died while waiting for visual progress");
        throw new GameStuckException();
    }
}
