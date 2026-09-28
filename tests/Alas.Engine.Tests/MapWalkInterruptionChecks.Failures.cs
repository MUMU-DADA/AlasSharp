using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapWalkInterruptionChecks
{
    private static async Task FailuresAsync()
    {
        foreach (string failure in new[] { "retirement", "capture", "stale", "click", "cancel" })
        {
            using var cancel = new CancellationTokenSource();
            var sample = new Sample("walk", true, [["$map"], ["$retire"], ["MAP_OFFENSIVE"]]);
            var ui = new Replay(sample) { Failure = failure, Cancel = failure == "cancel" ? cancel : null };
            var interruptions = new MapWalkInterruptions(ui, () => ui.Sequence, ui, ui.Combat);
            bool rejected = false;
            try { await ui.Arrival(interruptions).TapAndCheckAsync(new(2, 1), token: cancel.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { rejected = true; }
            Check(rejected && ui.Invalidated, "Failed interruption left usable map: " + failure);
            Check(interruptions.Evidence.All(e => e.FinishedFrame is null && !e.OffensiveCompleted), "Failed interruption claimed completion");
        }
        var longRetirement = new Replay(new("walk", true, [["$map"], ["$retire"], ["BATTLE_PREPARATION"]])) { DelayRetirement = true };
        var flow = new MapWalkInterruptions(longRetirement, () => longRetirement.Sequence, longRetirement, longRetirement.Combat);
        var result = await longRetirement.Arrival(flow).TapAndCheckAsync(new(2, 1), new(TimeSpan.Zero, TimeSpan.FromMilliseconds(40)));
        Check(result is { Outcome: MapArrivalOutcome.MapInterrupted, Encounter: MapEncounterKind.Combat },
            "Short walk observation deadline cancelled retirement");
        var ui2 = new Replay(new("walk", true, [["$retire"], ["BATTLE_PREPARATION"]]));
        var invalid = new MapWalkInterruptions(ui2, () => ui2.Sequence, ui2, ui2.Combat);
        bool stale = false;
        try { await invalid.HandleAsync(2, default); } catch (InvalidDataException) { stale = true; }
        Check(stale && ui2.Trace.Count == 0, "Wrong interruption frame executed actions");
        foreach (var bad in new WalkInterruptionEvidence?[] { null, new(0), new(2, 1), new(1, 2, OffensiveCompleted: true),
            new(1, LowEmotionHandled: true), new(1, 2, RetirementHandled: true) })
        {
            bool rejected = false;
            try { RunReport.ValidateWalkInterruption(bad); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Contradictory interruption evidence accepted");
        }
    }
}
