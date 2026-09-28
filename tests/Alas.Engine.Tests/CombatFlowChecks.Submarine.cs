using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CombatFlowChecks
{
    public static async Task SubmarineAsync()
    {
        foreach (string ending in new[] { "called", "unconfirmed", "conflict", "preparation_failure", "cancel" })
        {
            var ui = new Ui(UiAssets.Combat.BATTLE_PREPARATION) { CaptureSeconds = .5, FailClick = ending == "preparation_failure" };
            // Preparation lasts longer than the call window. Its time must not consume the battle's window.
            for (int i = 0; i < 12; i++) ui.Enqueue(UiAssets.Combat.BATTLE_PREPARATION);
            ui.Enqueue(UiAssets.CombatUi.PAUSE, UiAssets.Combat.SUBMARINE_AVAILABLE_CHECK_1,
                UiAssets.Combat.SUBMARINE_AVAILABLE_CHECK_2, UiAssets.Combat.SUBMARINE_READY);
            if (ending != "unconfirmed")
                ui.Enqueue(UiAssets.CombatUi.PAUSE, UiAssets.Combat.SUBMARINE_AVAILABLE_CHECK_1,
                    UiAssets.Combat.SUBMARINE_AVAILABLE_CHECK_2, UiAssets.Combat.SUBMARINE_CALLED);
            ui.Enqueue(UiAssets.Combat.BATTLE_STATUS_S);
            ui.Enqueue(ending == "conflict" ? UiAssets.Combat.EXP_INFO_C : UiAssets.Combat.EXP_INFO_S);
            for (int i = 0; i < 4; i++) ui.Enqueue(UiAssets.Ui.CAMPAIGN_CHECK);
            var submarine = new CombatSubmarineCall(ui, () => ui.Captures + 1, SubmarineMode.EveryCombat);
            var flow = new CombatFlow(ui, new NoStory(), new NoPopup(), new Stage(ui), submarine: submarine);
            if (ending == "preparation_failure")
                await Rejects<IOException>(() => flow.RunAutoAsync(Options).AsTask());
            else if (ending == "cancel")
            {
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                await Rejects<OperationCanceledException>(() => flow.RunAutoAsync(Options, cancellation.Token).AsTask());
            }
            else if (ending == "conflict")
                await Rejects<InvalidDataException>(() => flow.RunAutoAsync(Options).AsTask());
            else
            {
                var result = await flow.RunAutoAsync(Options);
                Check(result is { Return: CombatReturn.InStage, Rank.IsWinningRank: true, SubmarineCall.Attempts.Count: 1 },
                    "Combat lost the call evidence or called during preparation");
            }
            var evidence = submarine.Evidence;
            if (ending is "preparation_failure" or "cancel")
                Check(evidence is { State: "failed", StartedFrame: null, Attempts.Count: 0 }, "Preparation failure started a submarine call");
            else
                Check(evidence.Attempts.Count == 1 && evidence.StartedFrame == 14 &&
                    evidence.State == (ending == "conflict" ? "failed" : ending == "called" ? "called_observed" : "battle_ended_unconfirmed") &&
                    (evidence.ObservedFrame is not null) == (ending != "unconfirmed"), "Combat submarine evidence differs from its observations");
        }
        Console.WriteLine("Submarine combat integration: delayed loading, confirmed/unconfirmed call, result conflict, preparation failure and cancellation passed.");
    }
}
