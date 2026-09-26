using System.Buffers.Binary;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Navigation;

public interface IUiDriver
{
    GameServer Server { get; }
    bool HasFrame { get; }
    TimeProvider Clock { get; }
    ValueTask ScreenshotAsync(CancellationToken token);
    ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
        double similarity = 0.85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
        CancellationToken token = default);
    ValueTask ClickAsync(AssetRule asset, CancellationToken token);
    ValueTask ClickAreaAsync(Rectangle area, CancellationToken token);
    ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token);
    ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token);
    void ClearOffset(AssetRule asset);
    IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false);
    void ResetInterval(AssetRule asset, double seconds = 3);
    void ClearInterval(AssetRule asset);
    ValueTask DelayAsync(TimeSpan time, CancellationToken token);
}

/// <summary>C# owns screenshot identity, button offsets, timers and clicks for one automation session.</summary>
public sealed class UiDriver : IUiDriver
{
    private readonly IGameDevice _device;
    private readonly AssetMatcher _matcher;
    private readonly IVision _vision;
    private readonly Random _random;
    private readonly Dictionary<string, IntervalTimer> _timers = new(StringComparer.Ordinal);
    public GameServer Server { get; }
    public TimeProvider Clock { get; }
    public ScreenFrame? Frame { get; private set; }
    public bool HasFrame => Frame is not null;

    public UiDriver(GameServer server, IGameDevice device, IVision vision, AssetFiles assets,
        TimeProvider? clock = null, Random? random = null)
    {
        Server = server;
        _device = device;
        _matcher = new AssetMatcher(server, vision, assets);
        _vision = vision;
        Clock = clock ?? TimeProvider.System;
        _random = random ?? Random.Shared;
    }
    public async ValueTask ScreenshotAsync(CancellationToken token)
    {
        Frame = await _device.CaptureAsync(token);
        // The compiled upstream UI coordinates use a 1280x720 canvas. Device resizing/rotation is not ported yet.
        var png = Frame.Png.Span;
        if (png.Length < 24 || !png.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)) != 1280 || BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)) != 720)
            throw new NotSupportedException("Native UI currently requires a 1280x720 PNG frame; device normalization is not implemented");
    }
    public async ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
        double similarity = 0.85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
        CancellationToken token = default)
    {
        if (interval > 0 && !Timer(asset, interval, renew: true).Reached()) return false;
        bool appeared = await _matcher.AppearsAsync(Frame ?? throw new InvalidOperationException("No screenshot has been captured"),
            asset, offset, similarity, threshold, preprocessing, token);
        if (appeared && interval > 0) Timer(asset).Reset();
        return appeared;
    }
    public ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        => ClickAreaAsync(_matcher.ClickArea(asset), token);
    public ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        => _device.TapAsync(new PixelPoint(RandomCoordinate(area.Left, area.Right), RandomCoordinate(area.Top, area.Bottom)), token);
    public ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token)
        => _vision.MeanColorAsync(Frame ?? throw new InvalidOperationException("No screenshot has been captured"), area.Area, token);
    public ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token)
        => _vision.ColorBandsAsync(Frame ?? throw new InvalidOperationException("No screenshot has been captured"), request, token);
    private int RandomCoordinate(int minimum, int maximum)
    {
        if (minimum >= maximum) return maximum;
        long sum = 0;
        for (int i = 0; i < 3; i++) sum += _random.NextInt64(minimum, (long)maximum + 1);
        return (int)Math.Round(sum / 3.0, MidpointRounding.ToEven);
    }
    public void ClearOffset(AssetRule asset) => _matcher.ClearOffset(asset);
    public IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false)
    {
        if (!_timers.TryGetValue(asset.Name, out var timer) || (renew && timer.Seconds != seconds))
            _timers[asset.Name] = timer = new IntervalTimer(Clock, seconds);
        return timer;
    }
    public void ResetInterval(AssetRule asset, double seconds = 3) => Timer(asset, seconds).Reset();
    public void ClearInterval(AssetRule asset) => Timer(asset, 3).Clear();
    public ValueTask DelayAsync(TimeSpan time, CancellationToken token) => new(Task.Delay(time, Clock, token));
}
