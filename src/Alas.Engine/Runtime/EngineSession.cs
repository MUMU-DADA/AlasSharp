using System.Security.Cryptography;
using System.Text.Json;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Tasks;

namespace Alas.Engine.Runtime;

public sealed record EngineSessionOptions(string Adb, string Serial, GameServer Server, string Assets, string Python,
    string? ApplicationPackage = null, string? ModelDirectory = null, bool AllowActions = false);

/// <summary>One device and one pure-vision process for the entire new execution graph.</summary>
public sealed class EngineSession : IAsyncDisposable, IMapObservationService
{
    private readonly PythonTemplateVision _vision;
    private readonly JournalDevice _device;
    private readonly IApplicationHealth _application;
    private readonly AssetFiles _assets;
    public UiDriver Driver { get; }
    public PageGraph Pages { get; } = UpstreamPages.Create();
    public TaskCapabilities Capabilities { get; }
    public EngineSession(EngineSessionOptions options)
    {
        if (options.AllowActions && string.IsNullOrWhiteSpace(options.ApplicationPackage))
            throw new ArgumentException("Device actions require an explicit game application package");
        Capabilities = new(options.AllowActions, options.ModelDirectory is not null);
        _device = new JournalDevice(new AdbDevice(options.Adb, options.Serial, options.AllowActions));
        _application = options.ApplicationPackage is null ? new UnconfiguredApplication() :
            new JournalApplication(new AdbApplication(options.Adb, options.Serial, options.ApplicationPackage, options.AllowActions), _device);
        _vision = new PythonTemplateVision(Path.GetFullPath(options.Python), Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"), modelDirectory: options.ModelDirectory);
        _assets = new AssetFiles(options.Assets);
        Driver = new UiDriver(options.Server, _device, _vision, _assets);
    }
    public async ValueTask<MapCamera> CreateMapCameraAsync(CampaignState state, Cell initialPosition,
        MapDetectionRules detection, GridRecognitionRules recognition, MapCameraRules cameraRules,
        TimeSpan timeout, CancellationToken token = default, StageEntranceKind entrances = StageEntranceKind.Normal,
        UiRecoveryOptions? recoveryOptions = null)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (detection.OperationSiren) throw new NotSupportedException("Operation Siren geometry is available, but its grid predictor and camera workflow are not ported");
        detection.Validate(); recognition.Validate(); cameraRules.Validate();
        using var deadline = new CancellationTokenSource(timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var detector = new GridDetector(_vision, _assets, detection);
            async ValueTask<ScreenFrame> Capture(CancellationToken ct)
            { await Driver.ScreenshotAsync(ct); return Driver.Frame!; }
            var recovery = new UiRecovery(Driver, _application, Pages, recoveryOptions ?? new UiRecoveryOptions());
            var observations = new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No map screenshot"),
                _vision, _assets, Driver.Server, entrances);
            var guard = new MapUiRecovery(Driver, observations, _application, recovery, recovery);
            var source = new MapViewSource(Capture, detector, guard);
            var predictor = new GridRecognition(_vision, _assets, Driver.Server, recognition);
            var mask = await _assets.ReadAsync(detection.OperationSiren ? MapDetectionAssets.OsMask : MapDetectionAssets.Mask, combined.Token);
            var evidence = new MapSwipeEvidence(predictor, _vision, _vision, new(1, DateTimeOffset.UnixEpoch, mask), MapDetectionAssets.MaskOrigin);
            return await MapCamera.CreateAsync(state, initialPosition, source, new MapSwipeInput(_device), predictor,
                new(evidence), cameraRules, timeout, combined.Token, gridInput: new MapGridInput(_device));
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Map camera initialization exceeded its time limit", error); }
    }
    public MapArrivalCheck CreateMapArrivalCheck(MapCamera camera, CampaignConfiguration configuration,
        IMapEncounterHandler? handler = null)
    {
        var probe = new MapEncounterProbe(Driver, configuration.HasAmbush);
        return new(camera, camera.State, token => Driver.AppearsAsync(UiAssets.Handler.IN_MAP, token: token), Driver.Clock,
            probe, new MapAirRaidHandler(Driver, probe,
                () => Driver.Frame?.Sequence ?? throw new InvalidOperationException("No air raid screenshot"), handler));
    }
    public MapMovement CreateMapMovement(MapCamera camera, CampaignConfiguration configuration)
        => new(camera.State, configuration, camera, CreateMapArrivalCheck(camera, configuration));
    public async ValueTask<MapVisualObservation> ObserveMapAsync(CampaignRule rule, CancellationToken token)
    {
        var configuration = rule.Configure(new());
        var detector = new GridDetector(_vision, _assets, new MapDetectionRules().WithChapter(configuration.Vision));
        await Driver.ScreenshotAsync(token);
        var view = await detector.DetectAsync(Driver.Frame!, token);
        var recognition = new GridRecognition(_vision, _assets, Driver.Server, new());
        // This task reports local geometry only; a screenshot alone does not establish global map position.
        var cells = await recognition.ObserveAsync(view, new(1, 1), token: token);
        return new(view, cells.Cells);
    }
    public TaskContext BeginTask(TimeSpan timeout)
    {
        _device.Actions.Clear();
        Driver.ResetTask();
        var recovery = new UiRecovery(Driver, _application, Pages, new UiRecoveryOptions());
        return new(Driver, new UiNavigator(Driver, Pages, recovery), recovery, timeout, this);
    }
    public async Task<JsonObjectEvidence> SaveEvidenceAsync(string directory, bool failed)
    {
        Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        await File.WriteAllTextAsync(Path.Combine(directory, "actions.json"), JsonSerializer.Serialize(_device.Actions, json));
        string? image = null, hash = null;
        long? sequence = null;
        if (Driver.Frame is { } frame)
        {
            image = failed ? "failure.png" : "frame.png";
            await File.WriteAllBytesAsync(Path.Combine(directory, image), frame.Png.ToArray());
            hash = Convert.ToHexStringLower(SHA256.HashData(frame.Png.Span));
            sequence = frame.Sequence;
        }
        return new(image, hash, sequence, _device.Actions.Count);
    }
    public ValueTask DisposeAsync() => _vision.DisposeAsync();
    public sealed record JsonObjectEvidence(string? Image, string? Sha256, long? FrameSequence, int ActionAttempts);
    private sealed record DeviceAction(string Kind, DateTimeOffset StartedAt, object Parameters)
    {
        public bool Completed { get; set; }
        public string? Error { get; set; }
    }
    private sealed class JournalDevice(IGameDevice device) : IGameDevice
    {
        public List<DeviceAction> Actions { get; } = [];
        public ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default) => device.CaptureAsync(token);
        public ValueTask TapAsync(PixelPoint point, CancellationToken token = default) => Act("tap", point, () => device.TapAsync(point, token));
        public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default)
            => Act("swipe", new { start, end, duration }, () => device.SwipeAsync(start, end, duration, token));
        public ValueTask BackAsync(CancellationToken token = default) => Act("back", new { }, () => device.BackAsync(token));
        public async ValueTask Act(string kind, object parameters, Func<ValueTask> action)
        {
            var record = new DeviceAction(kind, DateTimeOffset.UtcNow, parameters);
            Actions.Add(record);
            try { await action(); record.Completed = true; }
            catch (Exception error) { record.Error = error.GetType().Name; throw; }
        }
    }
    private sealed class JournalApplication(IApplicationHealth application, JournalDevice device) : IApplicationHealth
    {
        public ValueTask<bool> IsRunningAsync(CancellationToken token) => application.IsRunningAsync(token);
        public ValueTask RefreshOrientationAsync(CancellationToken token) => application.RefreshOrientationAsync(token);
        public ValueTask StopAsync(CancellationToken token) => device.Act("app_stop", new { }, () => application.StopAsync(token));
    }
    private sealed class UnconfiguredApplication : IApplicationHealth
    {
        public ValueTask<bool> IsRunningAsync(CancellationToken token) => throw new InvalidOperationException("Application package is not configured");
        public ValueTask RefreshOrientationAsync(CancellationToken token) => throw new InvalidOperationException("Application package is not configured");
        public ValueTask StopAsync(CancellationToken token) => throw new InvalidOperationException("Application package is not configured");
    }
}
