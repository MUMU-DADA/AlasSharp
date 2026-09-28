using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapViewChecks
{
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
