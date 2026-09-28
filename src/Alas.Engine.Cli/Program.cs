using System.Text;
using System.Text.Json;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("Alas.Engine.Cli <observe|navigate|run|campaign> --adb <executable> --serial <device> --server <cn|en|jp|tw> --assets <directory> --vision-runtime <executable> --artifacts <directory>");
    Console.WriteLine("observe: capture and identify one frame, with device actions disabled.");
    Console.WriteLine("navigate: additionally requires --package <Android package> --page <destination>; --timeout <seconds> defaults to 120. Performs game clicks and recovery.");
    Console.WriteLine("run: --queue <JSON task array> [--models <ONNX directory>] [--allow-actions --package <Android package>] [--dry-run] [--continue-on-failure] [--resume <run directory>]. Task kinds: observe, navigate, data_key, map_observe, campaign_stages (read-only OCR; optional entrances array), campaign_select (input: campaign; stops at map preparation), campaign_fleet_prepare (input: campaign; stops at fleet preparation), campaign_run (input: campaign, fleet1, fleet2, submarine; default emotionMode=calculate), campaign_resume (input: campaign; default emotionMode=ignore; requires a freshly entered map and never records cleared).");
    Console.WriteLine("campaign: --chapter <compiled C# rule[,rule...]> --models <OCR model directory>; IDs use campaign_main/campaign_1_1 and never Python module paths. Defaults to dry-run. Add --run --allow-actions --package <Android package> to execute; optional --fleet1/--fleet2/--submarine/--submarine-mode/--timeout/--continue-on-failure/--resume. --fleet1-formation/--fleet2-formation accept line_ahead, double_line (default), diamond.");
    Console.WriteLine("--fleet-order: fleet1_mob_fleet2_boss (default), fleet1_boss_fleet2_mob, fleet1_all_fleet2_standby, fleet1_standby_fleet2_all; current campaign rules may disable fleet 2. --submarine-mode: do_not_use (default), hunt_only, every_combat; boss_only and hunt_and_boss are not yet supported when a submarine is enabled.");
    Console.WriteLine("run/campaign: --profile-root <Engine profile root> --instance <profile name> bind persistent Engine state to the same device. campaign: --emotion-mode <calculate|calculate_ignore|ignore|nothing> (default ignore). Calculated modes require a profile binding; deferred recovery is recorded in the profile and this task is skipped.");
    Console.WriteLine("campaign: --clear-mode <true|false> (default true), --double-book <true|false> (default false). Effective settings are observed on map/fleet preparation before entry.");
    Console.WriteLine("campaign: --map-achievement <non_stop|100_percent_clear|map_3_stars|threat_safe|threat_safe_without_3_stars>, --stage-increase <true|false>. Achievement stops require --profile-root and --instance to update Engine state.");
    return 0;
}
try
{
    if (args[0] is not ("observe" or "navigate" or "run" or "campaign")) throw new ArgumentException("Unknown command");
    bool navigate = args[0] == "navigate";
    bool run = args[0] == "run";
    bool campaign = args[0] == "campaign";
    string[] required = ["--adb", "--serial", "--server", "--assets", "--vision-runtime", "--artifacts",
        .. navigate ? new[] { "--package", "--page" } : run ? new[] { "--queue" } : campaign ? new[] { "--chapter", "--models" } : []];
    var allowed = required.Concat(navigate ? ["--timeout"] : run ? ["--models", "--package", "--resume", "--profile-root", "--instance"] : campaign
        ? ["--models", "--package", "--resume", "--fleet1", "--fleet2", "--submarine", "--submarine-mode", "--timeout", "--fleet1-formation", "--fleet2-formation", "--fleet-order", "--profile-root", "--instance", "--emotion-mode", "--clear-mode", "--double-book", "--map-achievement", "--stage-increase"]
        : Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
    var switches = (run ? new[] { "--allow-actions", "--dry-run", "--continue-on-failure" } : campaign
        ? new[] { "--run", "--allow-actions", "--continue-on-failure" } : []).ToHashSet(StringComparer.Ordinal);
    var flags = new HashSet<string>(StringComparer.Ordinal);
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i++)
    {
        if (switches.Contains(args[i]))
        {
            if (!flags.Add(args[i])) throw new ArgumentException("Duplicate argument");
            continue;
        }
        if (!allowed.Contains(args[i]) || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Unknown or incomplete argument");
        if (!values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate argument");
        i++;
    }
    if (required.Any(key => !values.ContainsKey(key))) throw new ArgumentException("Required arguments are missing");
    var server = values["--server"] switch
    {
        "cn" => GameServer.Cn, "en" => GameServer.En, "jp" => GameServer.Jp, "tw" => GameServer.Tw,
        _ => throw new ArgumentException("Unknown game server")
    };
    if (campaign && flags.Contains("--run") && !flags.Contains("--allow-actions"))
        throw new ArgumentException("Campaign execution requires --allow-actions");
    if (campaign && flags.Contains("--run") && !values.ContainsKey("--package"))
        throw new ArgumentException("Campaign execution requires --package");
    var timeout = values.TryGetValue("--timeout", out var seconds)
        ? TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture))
        : campaign ? TimeSpan.FromSeconds(1500) : TimeSpan.FromMinutes(2);
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        if (campaign)
        {
            int ParseFleet(string key, int fallback) => values.TryGetValue(key, out string? value)
                ? int.Parse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture)
                : fallback;
            var options = new CampaignCommandOptions(
                values["--chapter"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                values["--adb"], values["--serial"], server, values["--assets"], values["--vision-runtime"], values["--artifacts"],
                values.GetValueOrDefault("--package"), values.GetValueOrDefault("--models"),
                DryRun: !flags.Contains("--run"), AllowActions: flags.Contains("--allow-actions"),
                ContinueOnFailure: flags.Contains("--continue-on-failure"), ResumeDirectory: values.GetValueOrDefault("--resume"),
                Fleet1: ParseFleet("--fleet1", 1), Fleet2: ParseFleet("--fleet2", 0),
                Submarine: ParseFleet("--submarine", 0), SubmarineMode: SubmarineRules.Parse(values.GetValueOrDefault("--submarine-mode", "do_not_use")), TimeoutSeconds: timeout.TotalSeconds,
                Fleet1Formation: CampaignStrategy.ParseFormation(values.GetValueOrDefault("--fleet1-formation", "double_line")),
                Fleet2Formation: CampaignStrategy.ParseFormation(values.GetValueOrDefault("--fleet2-formation", "double_line")),
                FleetOrder: FleetRoles.Parse(values.GetValueOrDefault("--fleet-order", "fleet1_mob_fleet2_boss")),
                EmotionMode: EmotionRules.ParseMode(values.GetValueOrDefault("--emotion-mode", "ignore")),
                ProfileRoot: values.GetValueOrDefault("--profile-root"), ProfileInstance: values.GetValueOrDefault("--instance"),
                ClearMode: bool.Parse(values.GetValueOrDefault("--clear-mode", "true")),
                DoubleBook: bool.Parse(values.GetValueOrDefault("--double-book", "false")),
                MapAchievement: CampaignObjectives.Parse(values.GetValueOrDefault("--map-achievement", "non_stop")),
                StageIncrease: bool.Parse(values.GetValueOrDefault("--stage-increase", "false")));
            var campaignResult = await CampaignCommand.RunAsync(options, cancellation.Token);
            Console.WriteLine(CampaignCommand.Serialize(campaignResult));
            return campaignResult.Failed || cancellation.IsCancellationRequested ? 1 : 0;
        }
        if (run)
        {
            var tasks = await TaskQueue.ReadAsync(values["--queue"], cancellation.Token);
            var session = new EngineSessionOptions(values["--adb"], values["--serial"], server, values["--assets"], values["--vision-runtime"],
                values.GetValueOrDefault("--package"), values.GetValueOrDefault("--models"), flags.Contains("--allow-actions"),
                values.GetValueOrDefault("--profile-root"), values.GetValueOrDefault("--instance"));
            var queue = await new TaskQueue().RunAsync(tasks, session,
                new TaskQueueOptions(values["--artifacts"], flags.Contains("--dry-run"), flags.Contains("--continue-on-failure"), values.GetValueOrDefault("--resume")), cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(queue, TaskQueue.Json));
            return queue.Failed || cancellation.IsCancellationRequested ? 1 : 0;
        }
        var result = await NavigationRun.RunAsync(new NavigationRunOptions(values["--adb"], values["--serial"], server,
            values["--assets"], values["--vision-runtime"], values["--artifacts"], values.GetValueOrDefault("--page"),
            values.GetValueOrDefault("--package"), timeout), cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return result.Error is null ? 0 : 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
