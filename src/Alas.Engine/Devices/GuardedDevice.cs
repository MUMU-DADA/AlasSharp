using Alas.Engine.Imaging;

namespace Alas.Engine.Devices;

/// <summary>Apply progress checks to UI and map I/O before invoking a physical device.</summary>
public sealed class GuardedDevice(IGameDevice device, IApplicationHealth application, DeviceWatchdog watchdog) : IGameDevice
{
    public DeviceWatchdog Watchdog => watchdog;
    public async ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default)
    {
        await watchdog.BeforeCaptureAsync(application, token);
        var frame = await device.CaptureAsync(token);
        watchdog.FrameSequence = frame.Sequence;
        return frame;
    }
    public ValueTask TapAsync(PixelPoint point, CancellationToken token = default)
        => TapAsync(point, $"({point.X}, {point.Y})", token);
    public ValueTask TapAsync(PixelPoint point, string? name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Null is reserved for the native ADB drag's follow-up click (control_check=False).
        if (name is not null) watchdog.BeforeControl(name);
        return device.TapAsync(point, token);
    }
    public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default)
        => SwipeAsync(start, end, duration, "SWIPE", token);
    public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); watchdog.BeforeControl(name);
        return device.SwipeAsync(start, end, duration, token);
    }
    public ValueTask BackAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); watchdog.BeforeControl("BACK"); return device.BackAsync(token); }
}
