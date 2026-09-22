using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Alas.Vision;

/// <summary>识图引擎 worker 的启动配置。</summary>
public sealed class VisionWorkerOptions
{
    /// <summary>ALAS 仓库根目录（worker 的 cwd，素材路径相对它解析）。</summary>
    public required string ForkDirectory { get; init; }

    /// <summary>venv 的 python.exe；默认取 &lt;fork&gt;/.venv/Scripts/python.exe。</summary>
    public string? PythonExecutable { get; init; }

    /// <summary>vision_worker.py 路径；默认取 &lt;csharp&gt;/tools/vision_worker.py。</summary>
    public string? WorkerScript { get; init; }

    /// <summary>单次请求超时。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed class WorkerInfo
{
    [JsonPropertyName("python")] public string Python { get; set; } = "";
    [JsonPropertyName("cv2")] public string Cv2 { get; set; } = "";
    [JsonPropertyName("numpy")] public string Numpy { get; set; } = "";
    [JsonPropertyName("fork")] public string Fork { get; set; } = "";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
}

public sealed class AppearResult
{
    [JsonPropertyName("asset")] public string Asset { get; set; } = "";
    [JsonPropertyName("appear")] public bool Appear { get; set; }
    [JsonPropertyName("tolerance")] public double? Tolerance { get; set; }
    [JsonPropertyName("threshold")] public int? Threshold { get; set; }
    [JsonPropertyName("color")] public List<double>? Color { get; set; }
    [JsonPropertyName("expected")] public List<double>? Expected { get; set; }
    [JsonPropertyName("elapsed_ms")] public double? ElapsedMs { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class AppearBatchResult
{
    [JsonPropertyName("results")] public List<AppearResult> Results { get; set; } = new();
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("elapsed_ms")] public double ElapsedMs { get; set; }
}

public sealed class ButtonMatchResult
{
    [JsonPropertyName("match")] public bool Match { get; set; }
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("similarity")] public double Similarity { get; set; }
    [JsonPropertyName("button_offset")] public List<double>? ButtonOffset { get; set; }
}

public sealed class TemplateMatchResult
{
    [JsonPropertyName("similarity")] public double Similarity { get; set; }
    [JsonPropertyName("button_area")] public List<double> ButtonArea { get; set; } = new();
}

public sealed class ScreenshotInfo
{
    [JsonPropertyName("shape")] public List<int> Shape { get; set; } = new();
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

/// <summary>
/// 识图引擎客户端：C# 侧**不实现任何图像算法**，只按 JSON 协议把请求发给
/// 运行上游 Python 代码的 worker（见 tools/vision_worker.py）。
///
/// 为什么不让 C# 自己算：手工移植 cv2 已被实测证伪 —— OpenCV 会按模板/搜索区的尺寸比
/// 切换相关算法，同一块内容在不同搜索区尺寸下得分不同（实测同位置 0.7487 vs 1.0000）。
/// 逐位一致无法做到，也没有必要：上游代码本来就能跑。
/// </summary>
public sealed class VisionWorker : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly TimeSpan _timeout;
    private int _nextId;
    private bool _disposed;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private VisionWorker(Process process, StreamWriter stdin, StreamReader stdout, TimeSpan timeout)
    {
        _process = process;
        _stdin = stdin;
        _stdout = stdout;
        _timeout = timeout;
    }

    public static VisionWorker Start(VisionWorkerOptions options)
    {
        string fork = Path.GetFullPath(options.ForkDirectory);
        string python = options.PythonExecutable
                        ?? Path.Combine(fork, ".venv", "Scripts", "python.exe");
        string script = options.WorkerScript ?? DefaultWorkerScript();

        if (!File.Exists(python))
            throw new FileNotFoundException($"找不到 Python 解释器: {python}");
        if (!File.Exists(script))
            throw new FileNotFoundException($"找不到 worker 脚本: {script}");

        var psi = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = fork,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--stdio");

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException("无法启动识图 worker");
        var worker = new VisionWorker(process, process.StandardInput, process.StandardOutput,
                                      options.Timeout);
        // stderr 单独抽干，避免 worker 的日志把管道写满导致死锁
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) worker.Stderr.Add(e.Data); };
        process.BeginErrorReadLine();
        return worker;
    }

    public List<string> Stderr { get; } = new();

    private static string DefaultWorkerScript()
    {
        // bin/Release/net8.0 -> 回到 csharp/tools/
        string baseDir = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                                             "tools", "vision_worker.py"));
    }

    private JsonNode CallRaw(string op, object? args = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VisionWorker));
        int id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject { ["id"] = id, ["op"] = op };
        if (args is not null)
            request["args"] = JsonSerializer.SerializeToNode(args, Json);

        _stdin.Write(request.ToJsonString());
        _stdin.Write('\n');
        _stdin.Flush();

        string? line = _stdout.ReadLine();
        if (line is null)
        {
            string stderr = string.Join("\n", Stderr.TakeLast(10));
            throw new InvalidOperationException(
                $"识图 worker 已退出（op={op}）。stderr:\n{stderr}");
        }

        var response = JsonNode.Parse(line)
                       ?? throw new InvalidDataException($"响应不是合法 JSON: {line}");
        if (response["ok"]?.GetValue<bool>() != true)
        {
            string error = response["error"]?.GetValue<string>() ?? "未知错误";
            throw new VisionWorkerException(op, error, line);
        }
        return response["result"] ?? new JsonObject();
    }

    private T Call<T>(string op, object? args = null)
        => CallRaw(op, args).Deserialize<T>(Json)
           ?? throw new InvalidDataException($"{op} 的响应无法反序列化为 {typeof(T).Name}");

    // ---------------------------------------------------------------- 类型化接口
    public WorkerInfo Ping() => Call<WorkerInfo>("ping");

    public string SetServer(string server)
        => CallRaw("set_server", new { server })["server"]!.GetValue<string>();

    public ScreenshotInfo LoadScreenshot(string path)
        => Call<ScreenshotInfo>("screenshot_load", new { path });

    public AppearResult AppearOn(string asset, int threshold = 10)
        => Call<AppearResult>("appear_on", new { asset, threshold });

    public AppearBatchResult AppearOnBatch(IEnumerable<string> assets, int threshold = 10)
        => Call<AppearBatchResult>("appear_on_batch", new { assets = assets.ToArray(), threshold });

    public ButtonMatchResult ButtonMatch(string asset, int offset = 30, double similarity = 0.85)
        => Call<ButtonMatchResult>("button_match", new { asset, offset, similarity });

    public TemplateMatchResult TemplateMatch(string asset, string? name = null)
        => Call<TemplateMatchResult>("template_match", new { asset, name });

    public string Ocr(double[] area, string lang = "azur_lane", string? letter = null)
        => CallRaw("ocr", new { area, lang, letter })["text"]!.GetValue<string>();

    public void Shutdown()
    {
        if (_disposed) return;
        try
        {
            CallRaw("shutdown");
        }
        catch (Exception)
        {
            // worker 可能已自行退出
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _stdin.Close();
                if (!_process.WaitForExit((int)_timeout.TotalMilliseconds))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 忽略清理期异常
        }
        _process.Dispose();
    }
}

public sealed class VisionWorkerException : Exception
{
    public string Operation { get; }
    public string RawResponse { get; }

    public VisionWorkerException(string operation, string message, string rawResponse)
        : base($"识图 worker 在 {operation} 上失败: {message}")
    {
        Operation = operation;
        RawResponse = rawResponse;
    }
}
