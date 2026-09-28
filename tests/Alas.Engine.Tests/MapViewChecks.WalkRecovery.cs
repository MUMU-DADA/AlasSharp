using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapViewChecks
{
    internal static async Task WalkTimeoutCameraAsync(string upstream)
    {
        foreach (string failure in new[] { "", "stale", "transport", "cancel" })
        {
            var clock = new FakeClock(); var patches = new EmptyPatches();
            var frame = new ScreenFrame(1, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
            var oldGeometry = Regular(new(262, 227.5));
            var newGeometry = Regular(new(262, 227.5), new(Left: true, Upper: true));
            var state = Map(); state.Fleet1Location = new(5, 4); state[new(5, 4)].IsFleet = true;
            int observedBeforeCapture = 0;
            var source = new Source(i =>
            {
                observedBeforeCapture = patches.Calls;
                if (failure == "transport") throw new IOException("Synthetic timeout recovery transport failure");
                if (failure == "cancel") throw new OperationCanceledException();
                return new(frame with { Sequence = failure == "stale" ? 1 : i + 1 }, newGeometry);
            }, clock);
            var input = new Input();
            var camera = new MapCamera(state, new(5, 4), new(frame, oldGeometry), source, input,
                new(patches, new AssetFiles(Path.Combine(upstream, "assets")), GameServer.Cn, new()),
                new(new FixedEvidence(null)), new() { Predict = false, Optimize = false }, clock: clock,
                correctInitialEdges: false);
            camera.Suspend(); bool rejected = false;
            try { await camera.RecoverWalkTimeoutAsync(); }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { rejected = true; }
            Check(rejected == (failure != "") && observedBeforeCapture > 0 && source.Captures == 1 && input.Gestures.Count == 0,
                "Walk timeout must predict old view then refresh geometry before deciding to swipe: " + failure);
            Check(state.Fleet1Location == new Cell(5, 4) && state[new(5, 4)].IsFleet,
                "Camera timeout recovery mutated authoritative fleet state");
            if (failure == "")
            {
                Check(ReferenceEquals(camera.View.Geometry, newGeometry) && camera.FrameSequence == 2 && patches.Calls > observedBeforeCapture,
                    "Timeout recovery retained stale geometry or omitted fresh prediction");
                _ = await camera.ObserveAsync(MapScanMode.Normal, default);
            }
            else
            {
                bool unusable = false;
                try { await camera.RefreshImageAsync(); } catch (InvalidOperationException) { unusable = true; }
                Check(unusable, "Failed timeout camera remained usable");
            }
        }
    }

    internal static async Task WalkRecoveryCameraAsync(string upstream)
    {
        foreach (bool stale in new[] { false, true })
        {
            var clock = new FakeClock(); var patches = new EmptyPatches();
            var frame = new ScreenFrame(1, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
            var geometry = Regular(new(262, 227.5), new(Left: true, Upper: true));
            var source = new Source(i => new(frame with { Sequence = stale ? 1 : i + 1 }, geometry), clock);
            var camera = new MapCamera(Map(), new(5, 4), new(frame, geometry), source, new Input(),
                new(patches, new AssetFiles(Path.Combine(upstream, "assets")), GameServer.Cn, new()),
                new(new FixedEvidence(null)), new() { Predict = false }, clock: clock, correctInitialEdges: false);
            camera.Suspend();
            bool failed = false;
            try { await camera.RecoverWalkAsync(); } catch (InvalidDataException) { failed = true; }
            Check(failed == stale, "Walk camera accepted a stale recovery capture");
            if (!stale)
            {
                Check(patches.Calls > 0 && camera.FrameSequence > 1, "Walk recovery did not predict the fresh view");
                _ = await camera.ObserveAsync(MapScanMode.Normal, default);
            }
        }
    }
}
