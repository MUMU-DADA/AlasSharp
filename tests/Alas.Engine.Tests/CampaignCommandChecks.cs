using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class CampaignCommandChecks
{
    public static async Task RunAsync(string artifacts)
    {
        Check(CampaignCommand.RequireCompiledRuleId("campaign_main/campaign_1_1") == "campaign_main/campaign_1_1");
        foreach (string retired in new[] { "campaign.campaign_main.campaign_1_1", "campaign/campaign_main/campaign_1_2.py", "campaign_1_3", "campaign_main/campaign_1_1.py" })
            Reject<ArgumentException>(() => CampaignCommand.RequireCompiledRuleId(retired));

        var options = new CampaignCommandOptions(
            ["campaign_main/campaign_13_1", "campaign_main/campaign_13_4", "campaign_main/campaign_99_1"],
            "missing-adb", "offline", GameServer.Cn, "missing-assets", "missing-python", artifacts,
            ModelDirectory: "missing-models", DryRun: true, ContinueOnFailure: true,
            Fleet1Formation: FleetFormation.Diamond, Fleet2Formation: FleetFormation.LineAhead,
            FleetOrder: FleetOrder.Fleet1BossFleet2Mob);
        var requests = CampaignCommand.BuildRequests(options);
        Check(CampaignCommand.BuildRequests(options with { AutoSearch = true, SubmarineAutoCall = true, OilLimit = 1800 })
            .All(request => request.Input!["autoSearch"]!.GetValue<bool>() && request.Input["submarineAutoCall"]!.GetValue<bool>() &&
                request.Input["oilLimit"]!.GetValue<int>() == 1800), "Command lost automatic search inputs");
        Reject<ArgumentOutOfRangeException>(() => CampaignCommand.BuildRequests(options with { OilLimit = -1 }));
        foreach (string distance in new[] { "to_boss_position", "1_grid_to_boss", "2_grid_to_boss", "use_open_ocean_support" })
            Check(CampaignCommand.BuildRequests(options with { SubmarineDistanceToBoss = distance })
                .All(request => request.Input!["submarineDistanceToBoss"]!.GetValue<string>() == distance), "Command lost submarine distance");
        Reject<ArgumentException>(() => CampaignCommand.BuildRequests(options with { SubmarineDistanceToBoss = "invalid" }));
        foreach (var mode in Enum.GetValues<SubmarineMode>())
            Check(CampaignCommand.BuildRequests(options with { SubmarineMode = mode })
                .All(request => request.Input!["submarineMode"]!.GetValue<string>() == mode.Name()),
                "Command lost submarine mode");
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
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Retired campaign spelling was accepted");
    }
}
