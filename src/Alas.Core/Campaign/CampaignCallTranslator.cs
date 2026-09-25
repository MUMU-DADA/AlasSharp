using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Alas.Campaign;

/// <summary>一条翻译好的宿主调用：调哪个上游方法、带哪些参数（宿主引用形式）。</summary>
public sealed record CampaignHostCall(
    string Op,
    string Method,
    string? FleetPrefix,
    IReadOnlyList<JsonNode?> Args,
    IReadOnlyList<KeyValuePair<string, JsonNode?>> KeywordArgs,
    string? Unsupported);

/// <summary>
/// 把计划步骤翻成**宿主调用**（路线 (a)：C# 决定"做哪个原语、对哪个格子"，上游方法负责执行）。
///
/// 参数走宿主已有的引用通道（`tools/alas_vision.py: _campaign_arg`）：
/// <list type="bullet">
///   <item>`__grid__ {location: "C1"}` → `"#C1"`（宿主用 `node2location` 换成元组再取格子）；</item>
///   <item>`__grids__ [...]` → `"#grids:[C1,C2]"`；</item>
///   <item>`__roads__ [[[x,y],…],…]` → `"#roads:[[C1,C2],[C3]]"`（宿主用上游 `RoadGrids` 重建）；</item>
///   <item>标量原样透传。</item>
/// </list>
///
/// 方法名一般就等于原语名（实测 29 个原语里 26 个如此）；只有少数内部名与上游名不同，列在
/// <see cref="MethodNames"/> 里。舰队前缀（`fleet_2.clear_boss`）不在参数里表达——切队是 C# 的
/// `ensure_fleet` 原语（上游 `Fleet.switch_to` 本身是 `pass`），这里只把前缀带出来供调用方安排顺序。
///
/// 纯函数：只读计划步骤，不碰设备、不碰宿主。
/// </summary>
public static class CampaignCallTranslator
{
    /// <summary>C# 内部名 → 上游方法名（其余同名）。</summary>
    private static readonly Dictionary<string, string> MethodNames = new(StringComparer.Ordinal)
    {
        ["update_map"] = "update",          // Fleet.update()
    };

    /// <summary>上游没有对应方法、需要 C# 侧自己实现设备动作的原语（当前为空）。</summary>
    private static readonly Dictionary<string, string> Unsupported = new(StringComparer.Ordinal);

    public static CampaignHostCall Translate(CampaignPlanStep step)
    {
        // `SplitFleetPrefix` 返回 (前缀, 内层原语)。
        // 舰队前缀**保留成点号路径**：上游 `Fleet.fleet_2` 是属性，会先 `fleet_ensure(index=2)` 再
        // **返回 self**（`module/map/fleet.py:35`），所以 `instance.fleet_2.clear_boss` 才是忠实调用
        // ——"切队 + 内层原语"一次到位，比在 C# 侧分开调 `ensure_fleet` 更贴上游语义。
        // `super()` 不是舰队前缀：它是"调基类实现"，翻译成直接调那个方法。
        var (prefix, inner) = CampaignPrimitiveRegistry.SplitFleetPrefix(step.Op);
        string? fleetPrefix = null;
        if (prefix == "super()")
        {
            prefix = "";
        }
        else if (prefix.Length > 0)
        {
            fleetPrefix = prefix;
        }
        string methodName = prefix.Length > 0 ? $"{prefix}.{inner}" : inner;
        string upstreamName = prefix.Length > 0 || !MethodNames.TryGetValue(inner, out string? renamed)
            ? methodName
            : renamed;
        if (Unsupported.TryGetValue(inner, out string? reason))
        {
            return new CampaignHostCall(step.Op, upstreamName, fleetPrefix, [], [], reason);
        }
        var args = new List<JsonNode?>();
        var keywords = new List<KeyValuePair<string, JsonNode?>>();
        if (step.Args is not null)
        {
            foreach (var value in step.Args.Positional) args.Add(Encode(value));
            // 关键字实参**按关键字传给宿主**（Python 调用语义本来就是关键字），不折算成位置参数：
            // 折算会改变语义（上游签名顺序不是我们能假设的）。
            foreach (var (key, value) in step.Args.Keyword)
            {
                keywords.Add(new KeyValuePair<string, JsonNode?>(key, Encode(value)));
            }
        }
        return new CampaignHostCall(step.Op, upstreamName, fleetPrefix, args, keywords, null);
    }

    /// <summary>
    /// 计划里的实参 → 宿主引用形式。**返回 `JsonNode`**：标量原样克隆（`GetValue&lt;object&gt;()` 会报
    /// "An element of type 'String' cannot be converted to a 'System.Object'"，实测踩过）。
    /// </summary>
    private static JsonNode? Encode(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonObject payload when payload.ContainsKey("__grid__"):
                return JsonValue.Create("#" + Location(payload["__grid__"]));
            case JsonObject payload when payload.ContainsKey("__grids__"):
                var nodes = (payload["__grids__"] as JsonArray ?? [])
                    .Select(item => Location(item)).Where(node => node is not null).ToArray();
                return JsonValue.Create("#grids:[" + string.Join(",", nodes) + "]");
            case JsonObject payload when payload.ContainsKey("__roads__"):
                // 结构无歧义地写成 **JSON**：`[[["C1","D1"]], [["E1"]]]` = 两条道路，每条道路是若干
                // block，每个 block 是若干格。带引号是因为宿主要 `json.loads` 这一段（`[[C1,D1]]` 不是
                // 合法 JSON，实测报 JSONDecodeError）；层级必须写清楚，"少一层"会被宿主解析成逐字符
                // 遍历（`invalid literal for int() with base 10: ''`），实测踩过。
                var roads = new JsonArray((payload["__roads__"] as JsonArray ?? []).Select(road =>
                    (JsonNode)new JsonArray((road as JsonArray ?? []).Select(block =>
                        (JsonNode)new JsonArray((block as JsonArray ?? []).Select(cell =>
                            (JsonNode?)JsonValue.Create(Cell(cell))).ToArray())).ToArray())).ToArray());
                return JsonValue.Create("#roads:" + roads.ToJsonString());
            case JsonArray array:
                return new JsonArray(array.Select(Encode).ToArray());
            default:
                return value.DeepClone();
        }
    }

    /// <summary>`{"location": "C1"}` → `C1`。</summary>
    private static string? Location(JsonNode? node) =>
        node is JsonObject payload && payload.TryGetPropertyValue("location", out var location)
            ? location?.GetValue<string>()
            : null;

    /// <summary>`[x, y]` → `C1`（宿主用上游 `node2location` 反向换算）。</summary>
    private static string Cell(JsonNode? node)
    {
        if (node is JsonArray pair && pair.Count == 2)
        {
            return CampaignLocations.ToNode(pair[0]!.GetValue<int>(), pair[1]!.GetValue<int>());
        }
        return node?.ToJsonString() ?? "";
    }
}
