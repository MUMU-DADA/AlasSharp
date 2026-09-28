using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Tasks;

namespace Alas.Engine.Runtime;

/// <summary>Production composition root for campaign tasks.</summary>
public sealed record CampaignCommandOptions(
    IReadOnlyList<string> Chapters,
    string Adb,
    string Serial,
    GameServer Server,
    string Assets,
    string VisionRuntime,
    string Artifacts,
    string? ApplicationPackage = null,
    string? ModelDirectory = null,
    bool DryRun = true,
    bool AllowActions = false,
    bool ContinueOnFailure = false,
    string? ResumeDirectory = null,
    int Fleet1 = 1,
    int Fleet2 = 0,
    int Submarine = 0,
    bool FleetLock = true,
    double TimeoutSeconds = 1500,
    FleetFormation Fleet1Formation = FleetFormation.DoubleLine,
    FleetFormation Fleet2Formation = FleetFormation.DoubleLine,
    FleetOrder FleetOrder = FleetOrder.Fleet1MobFleet2Boss,
    CampaignEmotionMode EmotionMode = CampaignEmotionMode.Ignore,
    string? ProfileRoot = null,
    string? ProfileInstance = null,
    bool ClearMode = true,
    bool DoubleBook = false,
    MapAchievement MapAchievement = MapAchievement.NonStop,
    bool StageIncrease = false,
    SubmarineMode SubmarineMode = SubmarineMode.DoNotUse,
    string SubmarineDistanceToBoss = "2_grid_to_boss");

/// <summary>Translates CLI chapter arguments into typed C# task requests.</summary>
public static class CampaignCommand
{
    public static IReadOnlyList<TaskRequest> BuildRequests(CampaignCommandOptions options)
    {
        if (options.Chapters.Count == 0) throw new ArgumentException("至少要有一关", nameof(options));
        if (options.Fleet1 <= 0 || options.Fleet2 < 0 || options.Submarine < 0)
            throw new ArgumentException("舰队编号无效", nameof(options));
        if (!double.IsFinite(options.TimeoutSeconds) || options.TimeoutSeconds <= 0)
            throw new ArgumentException("时间上限必须为正数", nameof(options));
        _ = options.EmotionMode.Name();
        _ = options.MapAchievement.Name();
        _ = options.SubmarineMode.Name();
        _ = new CampaignConfiguration { SubmarineDistanceToBoss = options.SubmarineDistanceToBoss }.BossDistance();
        return options.Chapters.Select((chapter, index) => new TaskRequest(
            Id: $"campaign-{index + 1:D4}",
            Kind: "campaign_run",
            Input: new JsonObject
            {
                ["campaign"] = RequireCompiledRuleId(chapter),
                ["fleet1"] = options.Fleet1,
                ["fleet2"] = options.Fleet2,
                ["submarine"] = options.Submarine,
                ["submarineMode"] = options.SubmarineMode.Name(),
                ["submarineDistanceToBoss"] = options.SubmarineDistanceToBoss,
                ["emotionMode"] = options.EmotionMode.Name(),
                ["fleetLock"] = options.FleetLock,
                ["clearMode"] = options.ClearMode,
                ["doubleBook"] = options.DoubleBook,
                ["mapAchievement"] = options.MapAchievement.Name(),
                ["stageIncrease"] = options.StageIncrease,
                ["fleet1Formation"] = CampaignStrategy.FormationName(options.Fleet1Formation),
                ["fleet2Formation"] = CampaignStrategy.FormationName(options.Fleet2Formation),
                ["fleetOrder"] = FleetRoles.Name(options.FleetOrder)
            },
            Required: true,
            TimeoutSeconds: options.TimeoutSeconds)).ToArray();
    }

    public static async Task<TaskQueueResult> RunAsync(CampaignCommandOptions options,
        CancellationToken token = default)
    {
        var requests = BuildRequests(options);
        var session = new EngineSessionOptions(options.Adb, options.Serial, options.Server,
            Path.GetFullPath(options.Assets), options.VisionRuntime,
            options.ApplicationPackage, options.ModelDirectory is null ? null : Path.GetFullPath(options.ModelDirectory),
            options.AllowActions, options.ProfileRoot, options.ProfileInstance);
        return await new TaskQueue().RunAsync(requests, session,
            new TaskQueueOptions(Path.GetFullPath(options.Artifacts), options.DryRun,
                options.ContinueOnFailure, options.ResumeDirectory), token);
    }

    /// <summary>Accepts only the canonical identifier emitted by the compiled C# rule catalog.</summary>
    public static string RequireCompiledRuleId(string chapter)
    {
        if (string.IsNullOrWhiteSpace(chapter)) throw new ArgumentException("必须提供已编译的 C# 规则 ID", nameof(chapter));
        string value = chapter.Trim();
        if (!value.Equals(chapter, StringComparison.Ordinal) ||
            !System.Text.RegularExpressions.Regex.IsMatch(value,
                @"\Acampaign_main/campaign_[0-9]+_[0-9]+(?:_[0-9]+)?\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("只接受形如 campaign_main/campaign_1_1 的已编译 C# 规则 ID；不接受 Python 模块路径或文件名", nameof(chapter));
        return value;
    }

    public static string Serialize(TaskQueueResult result)
        => JsonSerializer.Serialize(result, TaskQueue.Json);
}
