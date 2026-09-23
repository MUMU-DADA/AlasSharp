using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Alas.Vision;

/// <summary>
/// 识图引擎接口。**C# 侧不实现任何图像算法**，只把请求发给跑上游 Python 代码的宿主
/// （见仓库 README「架构铁律 2」）。
///
/// 两种宿主实现同一接口、调用同一份 <c>alas_vision.handle_line()</c>：
///   - <see cref="InProcessVisionEngine"/>：进程内 CPython（目标形态）
///   - <see cref="VisionWorker"/>：进程外 worker（受限环境下的可运行形态）
/// 换宿主不需要改动上层任何一行。
/// </summary>
public interface IVisionEngine : IDisposable
{
    WorkerInfo Ping();
    string SetServer(string server);
    ScreenshotInfo LoadScreenshot(string path);
    /// <summary>用截图字节流设置当前画面（真实设备路径：adb 回来的就是 PNG 字节）。
    /// 像素不跨语言边界 —— C# 只传字节，解码与识图全在宿主里。</summary>
    ScreenshotInfo SetScreenshot(byte[] pngBytes, string? label = null);
    /// <summary>按因子重采样当前截图（识别层缩放适配用）。</summary>
    ScaleResult ScaleScreenshot(double factor);
    /// <summary>列出上游 module/ui/page.py 的页面规则。</summary>
    PageListResult PageList();
    /// <summary>当前画面命中的页面集合（只跑页面判定，供导航使用）。</summary>
    PageCurrentResult PageCurrent();
    /// <summary>上游页面导航图（节点 + 出边），**运行时向上游要**，不导出、不重写。</summary>
    PageGraphResult PageGraph();
    /// <summary>取上游素材的点击坐标（button 区域中心）—— 坐标由上游规则给出。</summary>
    ButtonCenter AssetButtonCenter(string asset);
    /// <summary>**按上游 UI.ui_page_appear 的原规则**判定当前页面（模板匹配，非颜色检查）。</summary>
    PageAppearResult PageAppear(string page);
    AppearResult AppearOn(string asset, int threshold = 10, bool detail = false);
    AppearBatchResult AppearOnBatch(IEnumerable<string> assets, int threshold = 10);
    /// <summary><paramref name="probeScore"/> 为真时额外二分反解实测相似度（慢 20 倍，诊断用）。</summary>
    ButtonMatchResult ButtonMatch(string asset, int offset = 30, double similarity = 0.85,
                                  bool probeScore = false);
    TemplateMatchResult TemplateMatch(string asset, string? name = null);
    string Ocr(double[] area, string lang = "azur_lane", string? letter = null);
    /// <summary>通用 op 调用（结果反序列化为 T）：S2 地图识别等尚未定型的 op 用它。</summary>
    T CallTyped<T>(string op, object? args = null);

    /// <summary>
    /// 账号/环境状态的**只读**快照（R2 账号状态域）：当前页面、是否在图内、服务器、章节与关键配置。
    /// 不点击、不导航；`capture=true` 才让设备抓一帧，`screenshotPath` 则用存盘帧（离线验收）。
    /// </summary>
    AccountStateResult AccountState(bool capture = false, string? screenshotPath = null);

    /// <summary>
    /// 上游任务目录（只读）。`SourceGroups` 是 `task.yaml` 的顶层键（**分组**），
    /// `GeneratedTasks` 是生成产物里的**扁平任务清单** —— 实测本机 9 个分组 / 68 个任务，
    /// 交集只有 3 个，所以两个字段各自标明是什么，不合并成一个含混的"任务列表"。
    /// </summary>
    TaskCatalogResult TaskCatalog();

    /// <summary>
    /// 选择**引擎的设备后端**（截图后端 / 输入后端）。引擎自带多后端
    /// （adb、droidcast、maatouch、minitouch、scrcpy、hermit、nemu_ipc…），
    /// 换后端只改这里，产品代码零改动 —— 这是"设备 I/O 走宿主"的入口。
    /// </summary>
    DeviceConfigResult ConfigureDevice(string serial, string screenshot = "adb", string control = "ADB");

    /// <summary>
    /// 用引擎的设备层截图并**直接置入宿主的当前截图**（像素不跨语言边界）。
    /// 比 `Screenshot()` 的 `adb → C# → 宿主` 少一次跨语言传输与落盘/读盘。
    /// </summary>
    DeviceCaptureResult CaptureViaEngine(bool raw = true);

    /// <summary>
    /// 执行上游 Campaign.run() 的完整出击流程；IR 用于展示关卡规则。
    /// C# 传递运行配置并报告上游执行结果。
    /// `dryRun` 默认 true：只回计划内容，不碰游戏；真跑必须 `allowActions = true`
    /// （宿主侧还有一道硬性安全联锁）。详见 docs/s3-entry-sequence.md。
    /// `clearAll` 选的是上游两套战斗流程里的哪一套：
    ///   false（默认）= `battle_{battle_count}`：BOSS 一刷出来就打 BOSS；
    ///   true         = `MAP_CLEAR_ALL_THIS_TIME` 分支：先清光小怪，清完才打 BOSS。
    /// `artifactsDir` 非空时，失败帧与结果文档落在该目录（R0 证据链的落盘位置）。
    /// </summary>
    CampaignPlanResult RunCampaignPlan(string chapter, bool dryRun = true,
                                       bool allowActions = false, double maxSeconds = 1500,
                                       int maxRounds = 20, bool repeatUntilCleared = true,
                                       int fleet1 = 1, int fleet2 = 0, int submarineFleet = 0,
                                       bool clearAll = false, string? serial = null,
                                       string? artifactsDir = null, string? withdrawFile = null);
}

/// <summary>
/// `s3_run_plan` 的返回：计划元数据 + <see cref="SortieResult"/> 的结果合同字段。
///
/// 合同字段**继承**而不是重抄一遍：结果口径只有一处定义（`Alas.Campaign.SortieResult`），
/// 这里只往上加"这一关的规则元数据"。判定一律走 `SortieContract.Violations`。
/// </summary>
public sealed class CampaignPlanResult : Alas.Campaign.SortieResult
{
    [JsonPropertyName("tier")] public string? Tier { get; set; }
    [JsonPropertyName("plan_steps")] public List<string>? PlanSteps { get; set; }
    [JsonPropertyName("semantic_trace")] public List<string>? SemanticTrace { get; set; }
    [JsonPropertyName("config_present")] public bool? ConfigPresent { get; set; }
    [JsonPropertyName("config_complete")] public bool? ConfigComplete { get; set; }
    [JsonPropertyName("config_count")] public int? ConfigCount { get; set; }
    [JsonPropertyName("config_origins")] public Dictionary<string, Alas.Core.CampaignConfigOrigin>? ConfigOrigins { get; set; }
    [JsonPropertyName("config_sources")] public List<string>? ConfigSources { get; set; }
    [JsonPropertyName("runtime_config_source")] public string? RuntimeConfigSource { get; set; }
    [JsonPropertyName("execution")] public string? Execution { get; set; }
    [JsonPropertyName("upstream_returned")] public bool? UpstreamReturned { get; set; }
}

/// <summary>`account_state` 的返回：账号/环境状态的只读快照。</summary>
public sealed class AccountStateResult
{
    [JsonPropertyName("server")] public string? Server { get; set; }
    /// <summary>当前画面命中的页面集合（上游 `Page.check_button` 判定）。</summary>
    [JsonPropertyName("pages")] public List<string>? Pages { get; set; }
    [JsonPropertyName("page_errors")] public List<string>? PageErrors { get; set; }
    /// <summary>是否在关卡地图里（上游 `is_in_map` 用同一个 `IN_MAP` 判定）。</summary>
    [JsonPropertyName("in_map")] public bool? InMap { get; set; }
    /// <summary>`IN_MAP` 的实测颜色与相似度 —— 判据是颜色比对，把数值留下来才能复核临界情况。</summary>
    [JsonPropertyName("in_map_evidence")] public Dictionary<string, JsonElement>? InMapEvidence { get; set; }
    [JsonPropertyName("in_map_error")] public string? InMapError { get; set; }
    [JsonPropertyName("frame")] public AccountFrameInfo? Frame { get; set; }
    /// <summary>已初始化的章节实例（没有则为 null）。</summary>
    [JsonPropertyName("campaign")] public Dictionary<string, JsonElement>? Campaign { get; set; }
    /// <summary>账号配置要点（只读快照，字段随上游配置项）。</summary>
    [JsonPropertyName("config")] public Dictionary<string, JsonElement>? Config { get; set; }
    [JsonPropertyName("config_name")] public string? ConfigName { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class AccountFrameInfo
{
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("shape")] public List<int>? Shape { get; set; }
}

/// <summary>`task_catalog` 的返回：上游任务目录的两个来源。</summary>
public sealed class TaskCatalogResult
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("loader")] public string? Loader { get; set; }
    /// <summary>`task.yaml` 的顶层键 —— 是**分组**，不是任务。</summary>
    [JsonPropertyName("source_groups")] public List<string>? SourceGroups { get; set; }
    [JsonPropertyName("source_group_count")] public int? SourceGroupCount { get; set; }
    /// <summary>生成产物里的**扁平任务清单** —— "有哪些任务"看这个。</summary>
    [JsonPropertyName("generated_tasks")] public List<string>? GeneratedTasks { get; set; }
    [JsonPropertyName("generated_task_count")] public int? GeneratedTaskCount { get; set; }
    [JsonPropertyName("generated_source")] public string? GeneratedSource { get; set; }
    [JsonPropertyName("generated_error")] public string? GeneratedError { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>`device_configure` 的返回：当前选择的设备后端。</summary>
public sealed class DeviceConfigResult
{
    [JsonPropertyName("configured")] public Dictionary<string, string>? Configured { get; set; }
}

/// <summary>`device_capture_set` 的返回：抓图耗时、后端与方法。</summary>
public sealed class DeviceCaptureResult
{
    [JsonPropertyName("capture_ms")] public double CaptureMs { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }
    [JsonPropertyName("raw")] public bool Raw { get; set; }
    [JsonPropertyName("shape")] public List<int>? Shape { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>协议编解码：请求 {"id","op","args"}，响应 {"id","ok","result"|"error"}。</summary>
public static class VisionProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string BuildRequest(int id, string op, object? args)
    {
        var request = new JsonObject { ["id"] = id, ["op"] = op };
        if (args is not null)
            request["args"] = JsonSerializer.SerializeToNode(args, Json);
        return request.ToJsonString();
    }

    public static JsonNode ParseResponse(string line, string op)
    {
        JsonNode? response;
        try
        {
            response = JsonNode.Parse(line);
        }
        catch (JsonException e)
        {
            throw new VisionWorkerException(op, $"响应不是合法 JSON: {e.Message}", Truncate(line));
        }
        if (response is null)
            throw new VisionWorkerException(op, "响应为空", Truncate(line));
        if (response["ok"]?.GetValue<bool>() != true)
        {
            string error = response["error"]?.GetValue<string>() ?? "未知错误";
            throw new VisionWorkerException(op, error, Truncate(line));
        }
        return response["result"] ?? new JsonObject();
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";
}

/// <summary>两种宿主共用的类型化接口实现。</summary>
public abstract class VisionEngineBase : IVisionEngine
{
    private int _nextId;

    /// <summary>把一次请求送进宿主并取回响应；由具体宿主实现。</summary>
    protected abstract JsonNode CallRaw(string op, object? args);

    private JsonNode Call(string op, object? args = null) => CallRaw(op, args);

    private T Call<T>(string op, object? args = null)
        => Call(op, args).Deserialize<T>(VisionProtocol.Json)
           ?? throw new InvalidDataException($"{op} 的响应无法反序列化为 {typeof(T).Name}");

    /// <summary>
    /// 发一次请求并把结果反序列化成 <typeparamref name="T"/>。
    /// 给"S2 地图识别"这类**尚未定型**的 op 用：先拿到结构化结果，再决定要不要在
    /// <see cref="IVisionEngine"/> 上开专用方法（否则接口会退化成一长串协议清单）。
    /// </summary>
    public T CallTyped<T>(string op, object? args = null) => Call<T>(op, args);

    public DeviceConfigResult ConfigureDevice(string serial, string screenshot = "adb",
                                              string control = "ADB")
        => CallTyped<DeviceConfigResult>("device_configure",
            new { serial, screenshot, control });

    public AccountStateResult AccountState(bool capture = false, string? screenshotPath = null)
        => CallTyped<AccountStateResult>("account_state",
            new { capture, screenshot = screenshotPath });

    public TaskCatalogResult TaskCatalog() => CallTyped<TaskCatalogResult>("task_catalog");

    public DeviceCaptureResult CaptureViaEngine(bool raw = true)
        => CallTyped<DeviceCaptureResult>("device_capture_set", new { raw });

    public CampaignPlanResult RunCampaignPlan(string chapter, bool dryRun = true,
                                              bool allowActions = false, double maxSeconds = 1500,
                                              int maxRounds = 20, bool repeatUntilCleared = true,
                                              int fleet1 = 1, int fleet2 = 0, int submarineFleet = 0,
                                              bool clearAll = false, string? serial = null,
                                              string? artifactsDir = null, string? withdrawFile = null)
        => CallTyped<CampaignPlanResult>("s3_run_plan", new
        {
            chapter,
            dry_run = dryRun,
            allow_actions = allowActions,
            max_seconds = maxSeconds,
            max_rounds = maxRounds,
            repeat_until_cleared = repeatUntilCleared,
            fleet1,
            fleet2,
            submarine_fleet = submarineFleet,
            clear_all = clearAll,
            serial,
            artifact_dir = artifactsDir,
            // **运行中请求撤退**：这个文件一旦出现，宿主会在**下一次战斗之前**调用上游自己的
            // `withdraw()`（挂钩见 `tools/s3_campaign_execution.py`），于是本局以
            // `CampaignEnd('Withdraw')` 结束 —— 调用栈里有 `withdraw` 帧，合同据此判
            // `outcome=withdrawn`。语义与既有的 `--stop-file` 一致：**文件出现即请求**。
            withdraw_file = withdrawFile,
        });

    protected int NextId() => Interlocked.Increment(ref _nextId);

    public WorkerInfo Ping() => Call<WorkerInfo>("ping");
    public string SetServer(string server) => Call("set_server", new { server })["server"]!.GetValue<string>();
    public ScreenshotInfo LoadScreenshot(string path) => Call<ScreenshotInfo>("screenshot_load", new { path });
    public PageListResult PageList() => Call<PageListResult>("page_list");
    public PageCurrentResult PageCurrent() => Call<PageCurrentResult>("page_current");
    public PageGraphResult PageGraph() => Call<PageGraphResult>("ui_page_graph");
    public ButtonCenter AssetButtonCenter(string asset)
        => Call<ButtonCenter>("asset_button_center", new { asset });
    public PageAppearResult PageAppear(string page) => Call<PageAppearResult>("page_appear", new { page });
    public ScaleResult ScaleScreenshot(double factor)
        => Call<ScaleResult>("screenshot_scale", new { factor });

    public ScreenshotInfo SetScreenshot(byte[] pngBytes, string? label = null)
        => Call<ScreenshotInfo>("screenshot_set",
            new { png_base64 = Convert.ToBase64String(pngBytes), label });
    public AppearResult AppearOn(string asset, int threshold = 10, bool detail = false) => Call<AppearResult>("appear_on", new { asset, threshold, detail });
    public AppearBatchResult AppearOnBatch(IEnumerable<string> assets, int threshold = 10)
        => Call<AppearBatchResult>("appear_on_batch", new { assets = assets.ToArray(), threshold });
    public ButtonMatchResult ButtonMatch(string asset, int offset = 30, double similarity = 0.85,
                                         bool probeScore = false)
        => Call<ButtonMatchResult>("button_match", new { asset, offset, similarity, probe_score = probeScore });
    public TemplateMatchResult TemplateMatch(string asset, string? name = null)
        => Call<TemplateMatchResult>("template_match", new { asset, name });
    public string Ocr(double[] area, string lang = "azur_lane", string? letter = null)
        => Call("ocr", new { area, lang, letter })["text"]!.GetValue<string>();

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 进程内 CPython 宿主实现（**目标形态**）。
///
/// 相比进程外 worker 省掉了进程间往返。基线中 ping 往返为 0.021ms（进程内）与
/// 0.084ms（worker）；两者均调用同一份上游 Python 入口，实际识图和设备 I/O 的耗时另计。
/// </summary>
public sealed class InProcessVisionEngine : VisionEngineBase
{
    private readonly PythonHost _host;

    private InProcessVisionEngine(PythonHost host) => _host = host;

    /// <summary>按 ALAS 仓库自动推导解释器与路径。</summary>
    public static InProcessVisionEngine StartFromAlasFork(string forkDirectory, string toolsDirectory)
        => Start(PythonHostOptions.FromAlasFork(forkDirectory, toolsDirectory));

    public static InProcessVisionEngine Start(PythonHostOptions options)
        => new(PythonHost.Start(options));

    public string PythonVersion => _host.PythonVersion;

    protected override JsonNode CallRaw(string op, object? args)
        => VisionProtocol.ParseResponse(_host.Call(VisionProtocol.BuildRequest(NextId(), op, args)), op);

    public override void Dispose()
    {
        _host.Dispose();
        base.Dispose();
    }
}
