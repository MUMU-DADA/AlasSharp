using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Alas.Campaign;

/// <summary>
/// 导出规则里的「关卡计划」：由 `tools/export_upstream_data.py` **静态解析**上游关卡得到
/// （`data/campaign/&lt;章节&gt;/&lt;关卡&gt;.json`）。这是"C# 引擎按静态规则执行关卡流程"的输入侧模型。
///
/// 只读数据：不导入游戏代码、不连设备、不执行任何上游逻辑。
/// 关键语义（与导出器一致）：
/// <list type="bullet">
///   <item><c>battles[].calls</c>：该钩子按顺序调用的原语名（如 <c>clear_siren</c> / <c>battle_default</c>）；</item>
///   <item><c>battles[].plan_complete = false</c>：该钩子含静态无法表达的部分，原因见 <c>unparsed</c>；</item>
///   <item><c>config</c>：该关卡生效的配置键值（识别参数、地图开关等）。</item>
/// </list>
/// 统计口径与 `tools/diagnostics/r5_upstream_audit.py` 对齐，便于跨语言对拍。
/// </summary>
public sealed class CampaignPlan
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("name_source")] public string? NameSource { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("campaign")] public required CampaignPlanHeader Header { get; init; }
    [JsonPropertyName("map")] public CampaignPlanMap? Map { get; init; }
    [JsonPropertyName("config")] public IReadOnlyDictionary<string, JsonNode?> Config { get; init; }
        = new Dictionary<string, JsonNode?>();

    /// <summary>章节目录名（如 <c>campaign_main</c>），不是 JSON 里的字段，由读取器补上。</summary>
    [JsonIgnore] public string Chapter { get; init; } = "";

    /// <summary>关卡文件名（如 <c>campaign_1_1</c>），不是 JSON 里的字段，由读取器补上。</summary>
    [JsonIgnore] public string Level { get; init; } = "";
}

/// <summary>关卡导出里的 <c>campaign</c> 段：类声明、基类、覆写钩子与完整性标记。</summary>
public sealed class CampaignPlanHeader
{
    [JsonPropertyName("class")] public string? Class { get; init; }
    [JsonPropertyName("bases")] public IReadOnlyList<string> Bases { get; init; } = [];
    [JsonPropertyName("tier")] public string? Tier { get; init; }
    [JsonPropertyName("has_siren")] public bool HasSiren { get; init; }
    [JsonPropertyName("template_only")] public bool TemplateOnly { get; init; }
    [JsonPropertyName("plan_complete")] public bool PlanComplete { get; init; }
    [JsonPropertyName("battles")] public IReadOnlyList<CampaignPlanBattle> Battles { get; init; } = [];
}

/// <summary>一个覆写钩子的计划：钩子名 + 原语调用序列 + 可执行步骤 + 静态可表达性。</summary>
public sealed class CampaignPlanBattle
{
    [JsonPropertyName("method")] public required string Method { get; init; }
    [JsonPropertyName("calls")] public IReadOnlyList<string> Calls { get; init; } = [];
    [JsonPropertyName("plan_complete")] public bool PlanComplete { get; init; }
    [JsonPropertyName("stmt_count")] public int StatementCount { get; init; }

    /// <summary>
    /// 可执行步骤（`plan_complete=true` 时非空）：C# 引擎要执行的计划本体。
    /// `steps` 为空表示该钩子含静态无法表达的部分，原因见 <see cref="Unparsed"/>。
    /// </summary>
    [JsonPropertyName("steps")] public IReadOnlyList<CampaignPlanStep> Steps { get; init; } = [];

    [JsonPropertyName("unparsed")] public IReadOnlyList<string> Unparsed { get; init; } = [];
}

/// <summary>
/// 计划里的一步。实测 `kind` 只有 4 种：<c>call</c>（直接调用原语）、<c>conditional</c>（条件成立才调用）、
/// <c>terminal</c>（收尾调用）、<c>super_delegate</c>（委托父类实现）；<c>op</c> 是原语名
/// （实测全部导出里只有 32 个不同取值）。
/// </summary>
public sealed class CampaignPlanStep
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("args")] public CampaignPlanStepArgs? Args { get; init; }
}

/// <summary>步骤实参：`positional` 里出现 <c>"&lt;expr&gt;"</c> 表示导出器未求值的表达式占位。</summary>
public sealed class CampaignPlanStepArgs
{
    [JsonPropertyName("positional")] public IReadOnlyList<JsonNode?> Positional { get; init; } = [];
    [JsonPropertyName("keyword")] public IReadOnlyDictionary<string, JsonNode?> Keyword { get; init; }
        = new Dictionary<string, JsonNode?>();
}

/// <summary>一次解释出来的执行轨迹：某钩子按计划要做的调用，以及它与原始 `calls` 的对拍结果。</summary>
public sealed record CampaignPlanTrace(
    string Method,
    IReadOnlyList<CampaignPlanStep> Steps,
    bool MatchesCalls,
    IReadOnlyList<string> Calls)
{
    /// <summary>无条件调用（`terminal` / `call`）——C# 引擎可直接执行的步骤。</summary>
    public IEnumerable<CampaignPlanStep> Unconditional =>
        Steps.Where(step => step.Kind is "terminal" or "call");

    /// <summary>条件调用（`conditional`）——需要条件求值后才能决定是否执行。</summary>
    public IEnumerable<CampaignPlanStep> Conditional =>
        Steps.Where(step => step.Kind == "conditional");

    /// <summary>委托父类（`super_delegate`）——由父类实现承担，不属本层执行面。</summary>
    public IEnumerable<CampaignPlanStep> Delegates =>
        Steps.Where(step => step.Kind == "super_delegate");

    /// <summary>轨迹里是否存在导出器未求值的表达式占位（`"&lt;expr&gt;"`）。</summary>
    public bool HasUnresolvedArguments => Steps.Any(step => step.Args?.Positional.Any(IsPlaceholder) == true);

    internal static bool IsPlaceholder(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<string>(out string? text) && text == "<expr>";
}

/// <summary>
/// 计划解释器：把导出的 <c>steps</c> 解释成执行轨迹。
///
/// 它是"引擎按静态规则执行"的最小内核——**只解释、不执行**：不调用上游、不连设备、不碰游戏。
/// 目前承担两件事：
/// <list type="number">
///   <item>把步骤按类型分流（无条件 / 条件 / 委托），给出 C# 引擎将来要执行的调用序列；</item>
///   <item>对拍不变量：除 <c>super_delegate</c> 外，步骤的 <c>op</c> 序列应与导出器给出的
///         <c>calls</c> 完全一致（实测全库 2786 个钩子一致、9 个仅差 super 委托）。</item>
/// </list>
/// </summary>
public static class CampaignPlanInterpreter
{
    /// <summary>解释一个钩子的计划。</summary>
    public static CampaignPlanTrace Interpret(CampaignPlanBattle battle)
    {
        var ops = battle.Steps
            .Where(step => step.Kind != "super_delegate")
            .Select(step => step.Op)
            .ToArray();
        bool matches = ops.SequenceEqual(battle.Calls, StringComparer.Ordinal);
        return new CampaignPlanTrace(battle.Method, battle.Steps, matches, battle.Calls);
    }

    /// <summary>解释一个关卡的全部钩子。</summary>
    public static IReadOnlyList<CampaignPlanTrace> Interpret(CampaignPlan plan) =>
        plan.Header.Battles.Select(Interpret).ToArray();
}

/// <summary>关卡导出里的 <c>map</c> 段（原样保留，形状由后续地图引擎解释）。</summary>
public sealed class CampaignPlanMap
{
    [JsonPropertyName("shape")] public string? Shape { get; init; }
    [JsonPropertyName("map_data")] public JsonNode? MapData { get; init; }
    [JsonPropertyName("spawn_data")] public JsonNode? SpawnData { get; init; }
    [JsonPropertyName("spawn_data_loop")] public JsonNode? SpawnDataLoop { get; init; }
    [JsonPropertyName("camera_data")] public JsonNode? CameraData { get; init; }
    [JsonPropertyName("camera_data_spawn_point")] public JsonNode? CameraSpawnPoint { get; init; }
    [JsonPropertyName("weight_data")] public JsonNode? WeightData { get; init; }

    /// <summary>上游 <c>MAP.bouncing_enemy_data</c>：巡逻敌人路线（每条形如 `["C2","C3","C4"]`）。</summary>
    [JsonPropertyName("bouncing_enemy_data")] public IReadOnlyList<IReadOnlyList<string>> BouncingEnemyData { get; init; } = [];
}

/// <summary>关卡计划的读取与统计（只读 <c>data/campaign/**</c>）。</summary>
public static class CampaignPlanReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>列出导出目录下的章节名（目录名排序）。</summary>
    public static IReadOnlyList<string> Chapters(string dataDirectory)
    {
        string root = Path.Combine(dataDirectory, "campaign");
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>读取一个章节的全部关卡计划（读不出的文件跳过并记录原因，不抛异常打断整章）。</summary>
    public static IReadOnlyList<CampaignPlan> LoadChapter(string dataDirectory, string chapter,
                                                          ICollection<string>? failures = null)
    {
        string directory = Path.Combine(dataDirectory, "campaign", chapter);
        if (!Directory.Exists(directory)) return [];
        var plans = new List<CampaignPlan>();
        foreach (string file in Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string level = Path.GetFileNameWithoutExtension(file);
            try
            {
                plans.Add(Read(dataDirectory, chapter, level));
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                failures?.Add($"{chapter}/{level}: {error.Message}");
            }
        }
        return plans;
    }

    /// <summary>读取单个关卡计划；文件缺失或字段不符时抛 <see cref="JsonException"/>。</summary>
    public static CampaignPlan Read(string dataDirectory, string chapter, string level)
    {
        string path = Path.Combine(dataDirectory, "campaign", chapter, level + ".json");
        using var stream = File.OpenRead(path);
        var plan = JsonSerializer.Deserialize<CampaignPlan>(stream, Options)
                   ?? throw new JsonException($"关卡计划为空：{chapter}/{level}");
        return new CampaignPlan
        {
            Name = plan.Name,
            NameSource = plan.NameSource,
            Source = plan.Source,
            Header = plan.Header,
            Map = plan.Map,
            Config = plan.Config,
            Chapter = chapter,
            Level = level,
        };
    }

    /// <summary>
    /// 按**上游模块名**读关卡计划：`campaign.campaign_main.campaign_1_1` → 目录 `campaign_main` + 关卡 `campaign_1_1`。
    /// 运行时要按"这次跑的是哪个模块"去找计划（影子比对、后续引擎执行都走这个映射），
    /// 读不到就返回 false——不猜、不兜底到别的关卡。
    /// </summary>
    public static bool TryReadModule(string dataDirectory, string chapterModule, out CampaignPlan? plan)
    {
        plan = null;
        string[] parts = chapterModule.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int offset = parts.Length > 0 && parts[0] == "campaign" ? 1 : 0;
        if (parts.Length - offset < 2) return false;
        if (string.IsNullOrEmpty(dataDirectory) || !Directory.Exists(Path.Combine(dataDirectory, "campaign")))
        {
            return false;
        }
        try
        {
            plan = Read(dataDirectory, parts[offset], parts[offset + 1]);
            return true;
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 汇总一章的关卡计划：关卡数、钩子数、静态可表达率、未表达原因分布、调用序列 Top。
    /// 统计口径与 `tools/diagnostics/r5_upstream_audit.py` 的 A/D 节一致（同一份导出数据）。
    /// </summary>
    public static CampaignPlanSummary Summarize(string chapter, IEnumerable<CampaignPlan> plans)
    {
        int levels = 0, battles = 0, complete = 0, steps = 0;
        int traceMatch = 0, traceDiff = 0, traceEmpty = 0, unresolvedArgs = 0;
        var unparsed = new Dictionary<string, int>(StringComparer.Ordinal);
        var sequences = new Dictionary<string, int>(StringComparer.Ordinal);
        var methods = new Dictionary<string, int>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var ops = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            levels++;
            foreach (var battle in plan.Header.Battles)
            {
                battles++;
                if (battle.PlanComplete) complete++;
                foreach (string reason in battle.Unparsed)
                    unparsed[reason] = unparsed.GetValueOrDefault(reason) + 1;
                methods[battle.Method] = methods.GetValueOrDefault(battle.Method) + 1;
                // 与 Python 侧一致：同一钩子的调用做去重后按名字排序，作为"序列"归一化形式。
                string key = string.Join("+", battle.Calls.Distinct().OrderBy(name => name, StringComparer.Ordinal));
                sequences[key] = sequences.GetValueOrDefault(key) + 1;
                foreach (var step in battle.Steps)
                {
                    steps++;
                    kinds[step.Kind] = kinds.GetValueOrDefault(step.Kind) + 1;
                    ops[step.Op] = ops.GetValueOrDefault(step.Op) + 1;
                }
                // 对拍不变量：解释出的轨迹（除 super 委托）应与导出的 calls 一致。
                if (battle.Steps.Count == 0)
                {
                    traceEmpty++;
                }
                else
                {
                    var trace = CampaignPlanInterpreter.Interpret(battle);
                    if (trace.MatchesCalls) traceMatch++; else traceDiff++;
                    if (trace.HasUnresolvedArguments) unresolvedArgs++;
                }
            }
        }
        return new CampaignPlanSummary(chapter, levels, battles, complete, steps, traceMatch, traceDiff,
                                       traceEmpty, unresolvedArgs, unparsed, methods, sequences, kinds, ops);
    }
}

/// <summary>一章的关卡计划统计快照。</summary>
public sealed record CampaignPlanSummary(
    string Chapter,
    int Levels,
    int Battles,
    int Complete,
    int Steps,
    int TraceMatch,
    int TraceDiff,
    int TraceEmpty,
    int UnresolvedArguments,
    IReadOnlyDictionary<string, int> Unparsed,
    IReadOnlyDictionary<string, int> Methods,
    IReadOnlyDictionary<string, int> Sequences,
    IReadOnlyDictionary<string, int> Kinds,
    IReadOnlyDictionary<string, int> Ops)
{
    /// <summary>静态可表达率（0–1）；没有钩子时按 1 计。</summary>
    public double CompleteRate => Battles == 0 ? 1 : (double)Complete / Battles;
}
