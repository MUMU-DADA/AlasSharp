using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alas.Core;

/// <summary>
/// 上游素材绑定（由 csharp/tools/export_upstream_data.py 从 module/**/assets.py 导出）。
/// 对应上游的 <c>Button(area=..., color=..., button=..., file=...)</c>，字段按服务器分列。
/// </summary>
public sealed class AssetBinding
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("module")] public string Module { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// <summary>服务器 -> [x0, y0, x1, y1]</summary>
    [JsonPropertyName("area")] public Dictionary<string, int[]>? Area { get; set; }

    /// <summary>服务器 -> [r, g, b]</summary>
    [JsonPropertyName("color")] public Dictionary<string, int[]>? Color { get; set; }

    /// <summary>服务器 -> [x0, y0, x1, y1]（点击区域）</summary>
    [JsonPropertyName("button")] public Dictionary<string, int[]>? Button { get; set; }

    /// <summary>服务器 -> 相对仓库根的图片路径</summary>
    [JsonPropertyName("file")] public Dictionary<string, string>? File { get; set; }

    [JsonPropertyName("servers")] public List<string> Servers { get; set; } = new();
    [JsonPropertyName("all_servers")] public bool AllServers { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";

    public int[]? AreaFor(string server) => Lookup(Area, server);
    public int[]? ColorFor(string server) => Lookup(Color, server);
    public int[]? ButtonFor(string server) => Lookup(Button, server);
    public string? FileFor(string server) => Lookup(File, server);

    private static T? Lookup<T>(Dictionary<string, T>? map, string server)
    {
        if (map is null) return default;
        if (map.TryGetValue(server, out var v)) return v;
        // Native Resource.parse_property indexes the requested server directly.
        // A missing variant is a broken export, never permission to use CN data.
        throw new KeyNotFoundException($"素材字段缺少服务器变体: {server}");
    }
}

public sealed class AssetCatalog
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("servers")] public List<string> Servers { get; set; } = new();
    [JsonPropertyName("assets")] public Dictionary<string, AssetBinding> Assets { get; set; } = new();
}

/// <summary>战斗步骤。plan_complete=false 时 steps 恒为空，不允许当作完整计划使用。</summary>
public sealed class CampaignStep
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("args")] public JsonElement? Args { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
}

public sealed class CampaignBattle
{
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("calls")] public List<string> Calls { get; set; } = new();
    [JsonPropertyName("steps")] public List<CampaignStep> Steps { get; set; } = new();
    [JsonPropertyName("plan_complete")] public bool PlanComplete { get; set; }
    [JsonPropertyName("unparsed")] public List<string> Unparsed { get; set; } = new();
    [JsonPropertyName("stmt_count")] public int StmtCount { get; set; }
}

public sealed class CampaignPlan
{
    [JsonPropertyName("class")] public string? Class { get; set; }
    [JsonPropertyName("bases")] public List<string> Bases { get; set; } = new();
    [JsonPropertyName("boss_battle")] public int? BossBattle { get; set; }
    [JsonPropertyName("plan_complete")] public bool PlanComplete { get; set; }
    [JsonPropertyName("template_only")] public bool TemplateOnly { get; set; }

    /// <summary>A/B/C 仅表示静态摘要完整度；所有战役继续由原生流程执行。</summary>
    [JsonPropertyName("tier")] public string Tier { get; set; } = "";
    [JsonPropertyName("has_siren")] public bool HasSiren { get; set; }
    [JsonPropertyName("attributes")] public Dictionary<string, JsonElement> Attributes { get; set; } = new();
    [JsonPropertyName("battles")] public List<CampaignBattle> Battles { get; set; } = new();

    /// <summary>非 battle_* 的覆写钩子且含真实逻辑，由原生 Campaign 继承调度保留。</summary>
    [JsonPropertyName("native_overrides")] public List<string> NativeOverrides { get; set; } = new();

    /// <summary>纯 return super().X() 的覆写 —— 只需虚方法分派，无新增逻辑。</summary>
    [JsonPropertyName("super_delegates")] public List<string> SuperDelegates { get; set; } = new();
}

public sealed class CampaignConfigOrigin
{
    [JsonPropertyName("module")] public string Module { get; set; } = "";
    [JsonPropertyName("class")] public string Class { get; set; } = "";
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("expression")] public string Expression { get; set; } = "";
}

/// <summary>章节 Config 的静态导出证据；Config 本身仍保留在 CampaignIr.Config。</summary>
public sealed class CampaignConfigExport
{
    [JsonPropertyName("present")] public bool Present { get; set; }
    [JsonPropertyName("complete")] public bool Complete { get; set; }
    [JsonPropertyName("mro")] public List<string> Mro { get; set; } = new();
    [JsonPropertyName("origins")] public Dictionary<string, CampaignConfigOrigin> Origins { get; set; } = new();
    [JsonPropertyName("typed_values")] public Dictionary<string, JsonElement> TypedValues { get; set; } = new();
    [JsonPropertyName("source_files")] public List<string> SourceFiles { get; set; } = new();
    [JsonPropertyName("unresolved")] public List<JsonElement> Unresolved { get; set; } = new();
}

/// <summary>MAP 源声明的离线证据；不重建原生地图对象或执行静态规则。</summary>
public sealed class CampaignMapExport
{
    [JsonPropertyName("present")] public bool Present { get; set; }
    [JsonPropertyName("complete")] public bool Complete { get; set; }
    [JsonPropertyName("derived_from")] public string? DerivedFrom { get; set; }
    [JsonPropertyName("origins")] public Dictionary<string, CampaignConfigOrigin> Origins { get; set; } = new();
    [JsonPropertyName("typed_values")] public Dictionary<string, JsonElement> TypedValues { get; set; } = new();
    [JsonPropertyName("source_files")] public List<string> SourceFiles { get; set; } = new();
    [JsonPropertyName("unresolved")] public List<JsonElement> Unresolved { get; set; } = new();
    [JsonPropertyName("calls")] public List<JsonElement>? Calls { get; set; }
}

/// <summary>单个关卡的中间表示（IR），对应上游 campaign/**/campaign_*.py。</summary>
public sealed class CampaignIr
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>CampaignMap = 上游权威名字；path = 从文件名兜底派生。</summary>
    [JsonPropertyName("name_source")] public string? NameSource { get; set; }

    [JsonPropertyName("map")] public Dictionary<string, JsonElement> Map { get; set; } = new();
    [JsonPropertyName("map_meta")] public CampaignMapExport MapMeta { get; set; } = new();
    [JsonPropertyName("config")] public Dictionary<string, JsonElement> Config { get; set; } = new();
    [JsonPropertyName("config_meta")] public CampaignConfigExport ConfigMeta { get; set; } = new();
    [JsonPropertyName("campaign")] public CampaignPlan Campaign { get; set; } = new();
    [JsonPropertyName("unresolved")] public List<string> Unresolved { get; set; } = new();

    public string? Shape => Map.TryGetValue("shape", out var v) && v.ValueKind == JsonValueKind.String
        ? v.GetString() : null;

    public string? MapData => Map.TryGetValue("map_data", out var v) && v.ValueKind == JsonValueKind.String
        ? v.GetString() : null;

    /// <summary>解析 "E3" / "h5" 形式的 shape；上游存在小写写法，这里统一按大写算列。</summary>
    public static (int Columns, int Rows)? ParseShape(string? shape)
    {
        if (string.IsNullOrWhiteSpace(shape) || shape.Length < 2) return null;
        char letter = char.ToUpperInvariant(shape[0]);
        if (letter < 'A' || letter > 'Z') return null;
        if (!int.TryParse(shape.AsSpan(1), out int rows)) return null;
        return (letter - 'A' + 1, rows);
    }
}

public sealed class CampaignIndexEntry
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("json")] public string Json { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("name_source")] public string? NameSource { get; set; }
    [JsonPropertyName("tier")] public string Tier { get; set; } = "";
    [JsonPropertyName("plan_complete")] public bool PlanComplete { get; set; }
    [JsonPropertyName("template_only")] public bool TemplateOnly { get; set; }
    [JsonPropertyName("boss_battle")] public int? BossBattle { get; set; }
    [JsonPropertyName("has_siren")] public bool HasSiren { get; set; }
    [JsonPropertyName("battle_methods")] public List<string> BattleMethods { get; set; } = new();
    [JsonPropertyName("native_overrides")] public List<string> NativeOverrides { get; set; } = new();
    [JsonPropertyName("super_delegates")] public List<string> SuperDelegates { get; set; } = new();
    [JsonPropertyName("config_keys")] public List<string> ConfigKeys { get; set; } = new();
    [JsonPropertyName("config_present")] public bool ConfigPresent { get; set; }
    [JsonPropertyName("config_complete")] public bool ConfigComplete { get; set; }
    [JsonPropertyName("map_keys")] public List<string> MapKeys { get; set; } = new();
    [JsonPropertyName("map_present")] public bool MapPresent { get; set; }
    [JsonPropertyName("map_complete")] public bool MapComplete { get; set; }
    [JsonPropertyName("needs_review")] public bool NeedsReview { get; set; }
}

public sealed class CampaignIndex
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("chapters")] public List<CampaignIndexEntry> Chapters { get; set; } = new();
}
