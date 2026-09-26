using System.Globalization;
using Alas.Engine.Imaging;

namespace Alas.Engine.Devices;

public interface IGameDevice
{
    ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default);
    ValueTask TapAsync(PixelPoint point, CancellationToken token = default);
    ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default);
    ValueTask BackAsync(CancellationToken token = default);
}

/// <summary>Native C# ADB I/O, independent of the image service and all game business rules.</summary>
public sealed class AdbDevice : IGameDevice
{
    private readonly string _executable;
    private readonly string _serial;
    private readonly IProcessRunner _process;
    private readonly bool _allowActions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequence;

    public AdbDevice(string executable, string serial, bool allowActions, IProcessRunner? process = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        _executable = executable;
        _serial = serial;
        _allowActions = allowActions;
        _process = process ?? new ProcessRunner();
    }

    public async ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            byte[] png = await RunAsync(["exec-out", "screencap", "-p"], token);
            if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new IOException("ADB did not return a PNG screenshot");
            return new ScreenFrame(++_sequence, DateTimeOffset.UtcNow, png);
        }
        finally { _gate.Release(); }
    }

    public ValueTask TapAsync(PixelPoint point, CancellationToken token = default)
    {
        ValidatePoint(point);
        return ActAsync(["shell", "input", "tap", Number(point.X), Number(point.Y)], token);
    }
    public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default)
    {
        ValidatePoint(start);
        ValidatePoint(end);
        if (duration.TotalMilliseconds < 1 || duration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
        return ActAsync(["shell", "input", "swipe", Number(start.X), Number(start.Y), Number(end.X),
            Number(end.Y), Number((int)duration.TotalMilliseconds)], token);
    }
    public ValueTask BackAsync(CancellationToken token = default) => ActAsync(["shell", "input", "keyevent", "4"], token);

    private async ValueTask ActAsync(string[] arguments, CancellationToken token)
    {
        if (!_allowActions) throw new InvalidOperationException("Device actions are disabled for this session");
        await _gate.WaitAsync(token);
        try { await RunAsync(arguments, token); }
        finally { _gate.Release(); }
    }
    private async Task<byte[]> RunAsync(string[] arguments, CancellationToken token)
    {
        var result = await _process.RunAsync(_executable, ["-s", _serial, .. arguments], TimeSpan.FromSeconds(30), token);
        if (result.ExitCode != 0) throw new IOException($"ADB exited with {result.ExitCode}: {result.Error}");
        return result.Output;
    }
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static void ValidatePoint(PixelPoint point)
    {
        if (point.X < 0 || point.Y < 0) throw new ArgumentOutOfRangeException(nameof(point));
    }
}
