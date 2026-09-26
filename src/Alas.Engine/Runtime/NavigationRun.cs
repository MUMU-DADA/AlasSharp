using System.Security.Cryptography;
using System.Text.Json;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record NavigationRunOptions(string Adb, string Serial, GameServer Server, string Assets,
    string Python, string Artifacts, string? Destination = null, string? ApplicationPackage = null, TimeSpan? Timeout = null);
public sealed record NavigationRunResult(string Artifacts, IReadOnlyList<string> Pages, int ActionAttempts, string? Error);

/// <summary>Independent composition root; never enters legacy tasks, configuration, or business Python.</summary>
public static class NavigationRun
{
    public static async Task<NavigationRunResult> RunAsync(NavigationRunOptions options, CancellationToken token = default)
    {
        bool navigate = options.Destination is not null;
        if (navigate && string.IsNullOrWhiteSpace(options.ApplicationPackage))
            throw new ArgumentException("Navigation requires an explicit application package");
        var graph = UpstreamPages.Create();
        if (navigate && graph[options.Destination!].Check is null)
            throw new NotSupportedException("Destination has no page recognition rule");
        var timeout = options.Timeout ?? TimeSpan.FromMinutes(2);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "Navigation deadline is invalid");
        string artifacts = Path.Combine(Path.GetFullPath(options.Artifacts), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var device = new AuditedDevice(new AdbDevice(options.Adb, options.Serial, allowActions: navigate));
        UiDriver? driver = null;
        try
        {
            await using var vision = new PythonTemplateVision(Path.GetFullPath(options.Python),
                Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
            driver = new UiDriver(options.Server, device, vision, new AssetFiles(options.Assets));
            IReadOnlyList<string> pages;
            bool? switched = null;
            if (navigate)
            {
                var application = new AuditedApplication(new AdbApplication(options.Adb, options.Serial,
                    options.ApplicationPackage!, allowActions: true), device);
                var recovery = new UiRecovery(driver, application, graph, new UiRecoveryOptions());
                var observation = await new UiNavigator(driver, graph, recovery).EnsureAsync(options.Destination!, timeout, token: token);
                pages = [observation.Page];
                switched = observation.Switched;
            }
            else pages = await new PageObserver(driver, graph).ObserveAsync(token);
            var frame = driver.Frame ?? throw new InvalidOperationException("Observation has no frame");
            await File.WriteAllBytesAsync(Path.Combine(artifacts, "frame.png"), frame.Png.ToArray(), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(artifacts, navigate ? "navigation.json" : "observation.json"), JsonSerializer.Serialize(new
            {
                engine = "Alas.Engine", operation = navigate ? "navigate" : "observe", actionsAllowed = navigate,
                server = options.Server.ToString().ToLowerInvariant(), frame.Sequence, frame.CapturedAt,
                frameSha256 = Convert.ToHexStringLower(SHA256.HashData(frame.Png.Span)), pages, switched,
                meaning = navigate ? "destination_page_observed" : "single_frame_observation"
            }, json), CancellationToken.None);
            return new NavigationRunResult(artifacts, pages, device.Actions.Count, null);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            string? failureFrame = null;
            if (driver?.Frame is { } frame)
            {
                failureFrame = "failure.png";
                await File.WriteAllBytesAsync(Path.Combine(artifacts, failureFrame), frame.Png.ToArray(), CancellationToken.None);
            }
            await File.WriteAllTextAsync(Path.Combine(artifacts, "failure.json"), JsonSerializer.Serialize(new
            {
                error = error.ToString(), cancelled = error is OperationCanceledException,
                failure_frames = failureFrame is null ? Array.Empty<string>() : new[] { failureFrame }
            }, json), CancellationToken.None);
            return new NavigationRunResult(artifacts, [], device.Actions.Count,
                error is OperationCanceledException ? nameof(OperationCanceledException) : error.GetType().Name);
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(artifacts, "actions.json"), JsonSerializer.Serialize(device.Actions, json), CancellationToken.None);
        }
    }

    private sealed record DeviceAction(string Kind, DateTimeOffset StartedAt, object Parameters)
    {
        public bool Completed { get; set; }
        public string? Error { get; set; }
    }
    private sealed class AuditedDevice(IGameDevice device) : IGameDevice
    {
        public List<DeviceAction> Actions { get; } = [];
        public ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default) => device.CaptureAsync(token);
        public ValueTask TapAsync(PixelPoint point, CancellationToken token = default)
            => Act("tap", point, () => device.TapAsync(point, token));
        public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default)
            => Act("swipe", new { start, end, duration }, () => device.SwipeAsync(start, end, duration, token));
        public ValueTask BackAsync(CancellationToken token = default)
            => Act("back", new { }, () => device.BackAsync(token));
        public async ValueTask Act(string kind, object parameters, Func<ValueTask> action)
        {
            var attempt = new DeviceAction(kind, DateTimeOffset.UtcNow, parameters);
            Actions.Add(attempt);
            try { await action(); attempt.Completed = true; }
            catch (Exception error) { attempt.Error = error.GetType().Name; throw; }
        }
    }
    private sealed class AuditedApplication(IApplicationHealth application, AuditedDevice audit) : IApplicationHealth
    {
        public ValueTask<bool> IsRunningAsync(CancellationToken token) => application.IsRunningAsync(token);
        public ValueTask RefreshOrientationAsync(CancellationToken token) => application.RefreshOrientationAsync(token);
        public ValueTask StopAsync(CancellationToken token) => audit.Act("app_stop", new { }, () => application.StopAsync(token));
    }
}
