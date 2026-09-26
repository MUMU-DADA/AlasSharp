namespace Alas.Engine.Navigation;

/// <summary>Port of the upstream time-and-access-count Timer, using a monotonic clock.</summary>
public sealed class IntervalTimer(TimeProvider clock, double seconds, int count = 0)
{
    private long _start;
    private int _access;
    public bool Started { get; private set; }
    public double Seconds { get; } = double.IsFinite(seconds) && seconds >= 0
        ? seconds : throw new ArgumentOutOfRangeException(nameof(seconds));
    public double Elapsed => Started ? Math.Max(0, clock.GetElapsedTime(_start).TotalSeconds) : 0;
    public bool Reached()
    {
        _access++;
        return !Started || (_access > count && clock.GetElapsedTime(_start).TotalSeconds > Seconds);
    }
    public void Reset() { _start = clock.GetTimestamp(); _access = 0; Started = true; }
    public void Clear() { Started = false; _access = count; }
}
