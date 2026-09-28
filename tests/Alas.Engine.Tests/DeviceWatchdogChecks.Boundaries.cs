using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class DeviceWatchdogChecks
{
    private sealed class Device(byte[] png) : IGameDevice
    {
        public int Captures, Taps, Swipes;
        public bool FailTap;
        public ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new ScreenFrame(++Captures, DateTimeOffset.UnixEpoch, png)); }
        public ValueTask TapAsync(PixelPoint point, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Taps++; if (FailTap) throw new IOException("Synthetic input failure"); return ValueTask.CompletedTask; }
        public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Swipes++; return ValueTask.CompletedTask; }
        public ValueTask BackAsync(CancellationToken token = default) => ValueTask.CompletedTask;
    }
    private static async Task RejectAsync<T>(Func<ValueTask> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task BoundariesAsync(string python, string upstream, string artifacts)
    {
        byte[] png = await File.ReadAllBytesAsync(Path.Combine(artifacts, "watchdog.png"));
        await using var vision = new PureVisionWorker(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        foreach (string mode in new[] { "running", "dead", "query-failure" })
        {
            var clock = new Clock(); var application = new Application(mode != "dead") { Fail = mode == "query-failure" };
            var raw = new Device(png); var guard = new DeviceWatchdog(clock); var device = new GuardedDevice(raw, application, guard);
            var ui = new UiDriver(GameServer.Cn, device, vision, new AssetFiles(Path.Combine(upstream, "assets")), clock);
            await ui.ScreenshotAsync(default); ui.ResetProgress();
            Check(await ui.AppearsAsync(UiAssets.Handler.IN_MAP), "Real CV fixture is not in map");
            clock.Advance(61);
            for (int i = 0; i < 60; i++) await ui.ScreenshotAsync(default);
            int captures = raw.Captures; long frame = ui.Frame!.Sequence;
            if (mode == "running") await RejectAsync<GameStuckException>(() => ui.ScreenshotAsync(default));
            if (mode == "dead") await RejectAsync<GameNotRunningException>(() => ui.ScreenshotAsync(default));
            if (mode == "query-failure") await RejectAsync<IOException>(() => ui.ScreenshotAsync(default));
            Check(captures == raw.Captures && ui.Frame.Sequence == frame && application.Inspections == 1 &&
                guard.Evidence is [{ Kind: "stuck", Seconds: 61, Checks: 61 }] &&
                guard.Evidence[0].Detections.Contains("IN_MAP") && guard.Evidence[0].FrameSequence == frame &&
                guard.Evidence[0].ApplicationRunning == (mode == "query-failure" ? null : mode == "running"),
                "Stuck check captured another image or lost partial application/observation evidence");
            ui.ResetTask(); application.Fail = false;
            await ui.ScreenshotAsync(default);
            Check(guard.Evidence.Count == 0 && application.Inspections == 1, "New task inherited elapsed time or failure records");
        }
        {
            var clock = new Clock(); var guard = new DeviceWatchdog(clock); var application = new Application(true); var raw = new Device(png);
            var device = new GuardedDevice(raw, application, guard);
            var ui = new UiDriver(GameServer.Cn, device, vision, new AssetFiles(Path.Combine(upstream, "assets")), clock);
            await ui.ScreenshotAsync(default); ui.ResetProgress();
            ui.ResetInterval(UiAssets.CombatUi.PAUSE, 500);
            Check(!await ui.AppearsAsync(UiAssets.CombatUi.PAUSE, interval: 500), "Throttled appearance unexpectedly matched");
            clock.Advance(180);
            for (int i = 0; i < 181; i++) await ui.ScreenshotAsync(default);
            Check(guard.Evidence.Count == 0, "Exact long-wait deadline or throttled appearance lost native semantics");
            clock.Advance(.25);
            await RejectAsync<GameStuckException>(() => ui.ScreenshotAsync(default));
            Check(guard.Evidence[0].Detections.Contains("PAUSE"), "Appearance interval bypassed detection recording");
            ui.ResetTask(); await ui.ScreenshotAsync(default);
            for (int i = 0; i < 11; i++) await ui.ClickAsync(UiAssets.Map.SWITCH_OVER, default);
            await RejectAsync<GameTooManyClicksException>(() => ui.ClickAsync(UiAssets.Map.SWITCH_OVER, default));
            Check(raw.Taps == 11 && guard.Evidence is [{ Controls.Length: 12 }], "Repeated button was physically clicked or randomized into separate identities");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            int checks = application.Inspections;
            await RejectAsync<OperationCanceledException>(() => ui.ClickAsync(UiAssets.Map.SWITCH_OVER, cancelled.Token));
            await RejectAsync<OperationCanceledException>(() => ui.ScreenshotAsync(cancelled.Token));
            Check(raw.Taps == 11 && application.Inspections == checks, "Cancelled operation reached physical control or health query");
            guard.Reset(); raw.FailTap = true;
            await RejectAsync<IOException>(() => ui.ClickAsync(UiAssets.Map.SWITCH_OVER, default)); raw.FailTap = false;
            for (int i = 0; i < 10; i++) await ui.ClickAsync(UiAssets.Map.SWITCH_OVER, default);
            await RejectAsync<GameTooManyClicksException>(() => ui.ClickAsync(UiAssets.Map.SWITCH_OVER, default));
            Check(raw.Taps == 22, "Failed physical control was erased from repetition history");
            guard.Reset();
            var grid = new MapGridInput(device);
            for (int i = 0; i < 11; i++) await grid.TapAsync(new(30 + i, 20, 10, 10), new(2, 1), default);
            await RejectAsync<GameTooManyClicksException>(() => grid.TapAsync(new(90, 20, 10, 10), new(2, 1), default));
            Check(guard.Evidence[^1].Controls.All(name => name == "B1"), "Map target identity depended on screen position");
            guard.Reset();
            var drag = new AdbFleetDrag(device, ui); int taps = raw.Taps;
            for (int i = 0; i < 11; i++) await drag.DragAsync(new(100, 100), new(200, 100), default);
            await RejectAsync<GameTooManyClicksException>(() => drag.DragAsync(new(100, 100), new(200, 100), default));
            Check(raw.Swipes == 11 && raw.Taps == taps + 11 && guard.Evidence[^1].Controls.All(name => name == "DRAG"),
                "ADB drag follow-up click was counted twice or issued after blocked gesture");
            guard.Reset();
            var swipe = new MapSwipeInput(device, new Random(46)); int swipes = raw.Swipes;
            var shortGesture = new MapSwipeGesture(new(3, 4), new(123, 159, 1052, 469), null, null)
                { ControlName = "MAP_SWIPE_1_0" };
            for (int i = 0; i < 11; i++) await swipe.SwipeAsync(shortGesture, default);
            Check(raw.Swipes == swipes, "Short swipe reached the physical device");
            await RejectAsync<GameTooManyClicksException>(() => swipe.SwipeAsync(shortGesture with { Pixels = new(100, 0) }, default));
            Check(raw.Swipes == swipes && guard.Evidence[^1].Controls.All(name => name == "MAP_SWIPE_1_0"),
                "Dropped and physical swipes did not share the named control history");
            foreach (var record in guard.Evidence) RunReport.ValidateDeviceWatchdog(record);
            var valid = guard.Evidence[^1];
            foreach (var corrupt in new[] { valid with { Controls = ["DRAG"] }, valid with { ApplicationRunning = true },
                valid with { FrameSequence = 0 }, valid with { Kind = "unknown" }, valid with { Detections = ["PAUSE"] },
                valid with { Kind = "stuck", Seconds = 60, Checks = 61 } })
            {
                bool rejected = false;
                try { RunReport.ValidateDeviceWatchdog(corrupt); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Malformed watchdog evidence was accepted");
            }
        }
    }
}
