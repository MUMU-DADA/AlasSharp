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
    string Python,
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
    FleetOrder FleetOrder = FleetOrder.Fleet1MobFleet2Boss);

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

        return options.Chapters.Select((chapter, index) => new TaskRequest(
            Id: $"campaign-{index + 1:D4}",
            Kind: "campaign_run",
            Input: new JsonObject
            {
                ["campaign"] = NormalizeRuleId(chapter),
                ["fleet1"] = options.Fleet1,
                ["fleet2"] = options.Fleet2,
                ["submarine"] = options.Submarine,
                ["emotionMode"] = "ignore",
                ["fleetLock"] = options.FleetLock,
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
            Path.GetFullPath(options.Assets), options.Python,
            options.ApplicationPackage, options.ModelDirectory is null ? null : Path.GetFullPath(options.ModelDirectory),
            options.AllowActions);
        return await new TaskQueue().RunAsync(requests, session,
            new TaskQueueOptions(Path.GetFullPath(options.Artifacts), options.DryRun,
                options.ContinueOnFailure, options.ResumeDirectory), token);
    }

    /// <summary>Accepts the source module spellings used by upstream and the CLI.</summary>
    public static string NormalizeRuleId(string chapter)
    {
        if (string.IsNullOrWhiteSpace(chapter)) throw new ArgumentException("章节不能为空", nameof(chapter));
        string value = chapter.Trim().Replace('\\', '/');
        if (value.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) value = value[..^3];
        if (value.StartsWith("campaign/", StringComparison.Ordinal)) value = value[9..];
        if (value.StartsWith("campaign.", StringComparison.Ordinal)) value = value[9..].Replace('.', '/');
        value = value.Replace('.', '/').Trim('/');
        if (!value.Contains('/', StringComparison.Ordinal))
            value = "campaign_main/" + value;
        return value;
    }

    public static string Serialize(TaskQueueResult result)
        => JsonSerializer.Serialize(result, TaskQueue.Json);
}
