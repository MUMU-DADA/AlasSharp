using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class CampaignCommandChecks
{
    public static async Task RunAsync(string artifacts)
    {
        Check(CampaignCommand.NormalizeRuleId("campaign.campaign_main.campaign_1_1") == "campaign_main/campaign_1_1");
        Check(CampaignCommand.NormalizeRuleId("campaign/campaign_main/campaign_1_2.py") == "campaign_main/campaign_1_2");
        Check(CampaignCommand.NormalizeRuleId("campaign_1_3") == "campaign_main/campaign_1_3");

        var options = new CampaignCommandOptions(
            ["campaign.campaign_main.campaign_13_1", "campaign/campaign_main/campaign_13_4.py", "campaign_main/campaign_99_1"],
            "missing-adb", "offline", GameServer.Cn, "missing-assets", "missing-python", artifacts,
            ModelDirectory: "missing-models", DryRun: true, ContinueOnFailure: true,
            Fleet1Formation: FleetFormation.Diamond, Fleet2Formation: FleetFormation.LineAhead,
            FleetOrder: FleetOrder.Fleet1BossFleet2Mob);
        var requests = CampaignCommand.BuildRequests(options);
        Check(requests.All(request => request.Input!["clearMode"]!.GetValue<bool>() &&
            !request.Input["doubleBook"]!.GetValue<bool>()), "Command lost native preparation defaults");
        Check(CampaignCommand.BuildRequests(options with { ClearMode = false, DoubleBook = true })
            .All(request => !request.Input!["clearMode"]!.GetValue<bool>() && request.Input["doubleBook"]!.GetValue<bool>()),
            "Command lost requested preparation settings");
        Check(CampaignCommand.BuildRequests(options with { MapAchievement = MapAchievement.ThreatSafe, StageIncrease = true })
            .All(request => request.Input!["mapAchievement"]!.GetValue<string>() == "threat_safe" &&
                request.Input["stageIncrease"]!.GetValue<bool>()), "Command lost achievement stop settings");
        var calculated = CampaignCommand.BuildRequests(options with { EmotionMode = CampaignEmotionMode.CalculateIgnore });
        Check(calculated.All(request => request.Input!["emotionMode"]!.GetValue<string>() == "calculate_ignore" &&
            !request.Input!.ContainsKey("configTask")), "Command retained retired task configuration state");
        Check(requests.Count == 3 && requests[0].Input!["campaign"]!.GetValue<string>() == "campaign_main/campaign_13_1");
        Check(requests.All(request => request.Input!["fleet1Formation"]!.GetValue<string>() == "diamond" &&
            request.Input["fleet2Formation"]!.GetValue<string>() == "line_ahead" &&
            request.Input["fleetOrder"]!.GetValue<string>() == "fleet1_boss_fleet2_mob"), "Command lost formation/order options");
        var result = await CampaignCommand.RunAsync(options);
        Check(result.Failed && result.Tasks.Count == 3 &&
              result.Tasks[0].Outcome == TaskOutcome.DryRun &&
              result.Tasks[1].Outcome == TaskOutcome.DryRun &&
              result.Tasks[2].Outcome == TaskOutcome.Refused,
              "Campaign command did not keep compiled-rule and unsupported-rule outcomes distinct");
    }

    private static void Check(bool value, string message = "Campaign command check failed")
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
