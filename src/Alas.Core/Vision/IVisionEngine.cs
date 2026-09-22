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
/// 相比进程外 worker 省掉了每帧几十次的进程间往返：
/// 实测进程外单次 <c>appear_on</c> 往返 6.83ms，其中 4.38ms 是通信开销，
/// worker 内部真正算一次只要 0.014ms。
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
