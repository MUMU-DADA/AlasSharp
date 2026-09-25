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
    string? Unsupported)
{
    /// <summary>
    /// 实参里有**只能运行期解析**的引用（局部变量 / 钩子参数）：执行器在调用前会替换成具体值
    /// （`SubstituteLocals` / `ResolveSuperDelegate`），静态翻译到这里只能到此为止。
    /// **与 <see cref="Unsupported"/> 分开**：这不是"翻译不了"，而是"翻译的时机在运行期"。
    /// </summary>
    public string? RuntimeOnly { get; init; }
}

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
        bool runtimeOnly = false;
        if (step.Args is not null)
        {
            try
            {
                foreach (var value in step.Args.Positional)
                {
                    args.Add(Encode(value, ref runtimeOnly));
                }
                // 关键字实参**按关键字传给宿主**（Python 调用语义本来就是关键字），不折算成位置参数：
                // 折算会改变语义（上游签名顺序不是我们能假设的）。
                foreach (var (key, value) in step.Args.Keyword)
                {
                    keywords.Add(new KeyValuePair<string, JsonNode?>(key, Encode(value, ref runtimeOnly)));
                }
            }
            catch (NotSupportedException error)
            {
                // **未知的实参结构不再原样透传**：以前 `default:` 直接克隆，宿主收到一个它不认识的
                // JSON 对象也可能"看起来能跑"——那是假绿。现在显式报出。
                return new CampaignHostCall(step.Op, upstreamName, fleetPrefix, [], [], error.Message);
            }
        }
        return new CampaignHostCall(step.Op, upstreamName, fleetPrefix, args, keywords, null)
        {
            RuntimeOnly = runtimeOnly
                ? "实参含运行期引用（`__local__` 局部变量 / `__param__` 钩子参数）：执行器调用前替换成具体值"
                : null,
        };
    }

    /// <summary>
    /// 计划里的实参 → 宿主引用形式。**返回 `JsonNode`**：标量原样克隆（`GetValue&lt;object&gt;()` 会报
    /// "An element of type 'String' cannot be converted to a 'System.Object'"，实测踩过）。
    /// </summary>
    private static JsonNode? Encode(JsonNode? value, ref bool runtimeOnly)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonObject payload when payload.ContainsKey("__local_grids__"):
                // 局部**格子集合**引用：执行器在调用前换成 `{"__grids__": …}`（`SubstituteLocals`）
                runtimeOnly = true;
                return JsonValue.Create("#runtime-local-grids");
            case JsonObject payload when payload.ContainsKey("__local__"):
                // 局部变量引用：由**执行器**在运行期替换成 `{"__grid__": …}`（见 `SubstituteLocals`），
                // 静态翻译到这里只能标记"运行期解析"，不能编造一个格子。
                runtimeOnly = true;
                return JsonValue.Create("#runtime-local");
            case JsonObject payload when payload.ContainsKey("__param__"):
                // 钩子**参数引用**（`super().handle_boss_appear_refocus(preset)`）：执行器用计划里记的
                // 参数默认值还原（`ResolveSuperDelegate`），同样是运行期解析。
                runtimeOnly = true;
                return JsonValue.Create("#runtime-param");
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
                var items = new List<JsonNode?>();
                foreach (var item in array) items.Add(Encode(item, ref runtimeOnly));
                return new JsonArray(items.ToArray());
            case JsonObject payload:
                // 未知的对象形状：**报错**而不是原样透传（原样透传会让"翻译不了"看起来像"翻译好了"）。
                throw new NotSupportedException(
                    $"实参里出现未识别的结构：{payload.ToJsonString()[..Math.Min(80, payload.ToJsonString().Length)]}");
            default:
                return value.DeepClone();
        }
    }

    /// <summary>`{"location": "C1"}` → `C1`。</summary>
    /// <summary>\{"__grid__": [x, y]}\ → 格名（条件里的格子属性等调用方也用）。</summary>
    public static string LocationOf(JsonNode? node) => Location(node) ?? "";

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
