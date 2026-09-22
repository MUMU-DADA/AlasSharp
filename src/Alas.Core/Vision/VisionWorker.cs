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
    [JsonPropertyName("detail")] public Dictionary<string, double>? Detail { get; set; }
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
    /// <summary>**阈值**，不是分数（上游 Button.match 的 similarity 参数，默认 0.85）。</summary>
    [JsonPropertyName("similarity")] public double Similarity { get; set; }
    /// <summary>实测相似度（仅 probeScore=true 时返回）。</summary>
    [JsonPropertyName("score")] public double? Score { get; set; }
    [JsonPropertyName("score_error")] public string? ScoreError { get; set; }
    /// <summary>匹配到的按钮区域。上游 <c>Button.button</c> 在 match 之后返回它，
    /// ALAS 的 appear+click 点的就是这个区域，不是资产里的标称坐标。</summary>
    [JsonPropertyName("button_offset")] public List<double>? ButtonOffset { get; set; }

    /// <summary>该按钮的实际点击点（匹配区域中心）；没有匹配结果时返回 null。</summary>
    public (int X, int Y)? ClickPoint()
    {
        if (ButtonOffset is not { Count: 4 }) return null;
        return ((int)Math.Round((ButtonOffset[0] + ButtonOffset[2]) / 2),
                (int)Math.Round((ButtonOffset[1] + ButtonOffset[3]) / 2));
    }
}

public sealed class PageCurrentResult
{
    [JsonPropertyName("hit")] public List<string> Hit { get; set; } = new();
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();
}

public sealed class PageLinkInfo
{
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("button")] public string Button { get; set; } = "";
    /// <summary>同一按钮在不同主界面版本下的候选资产（上游声明的，已按可解析性过滤）。</summary>
    [JsonPropertyName("variants")] public List<string> Variants { get; set; } = new();
}

public sealed class PageNodeInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("check")] public string? Check { get; set; }
    [JsonPropertyName("links")] public List<PageLinkInfo> Links { get; set; } = new();
}

public sealed class PageGraphResult
{
    [JsonPropertyName("nodes")] public List<PageNodeInfo> Nodes { get; set; } = new();
    [JsonPropertyName("node_count")] public int NodeCount { get; set; }
    [JsonPropertyName("edge_count")] public int EdgeCount { get; set; }
    [JsonPropertyName("unmapped")] public List<string> Unmapped { get; set; } = new();
    [JsonPropertyName("roundtrip_bad")] public List<string> RoundtripBad { get; set; } = new();
    [JsonPropertyName("roundtrip_checked")] public int RoundtripChecked { get; set; }
}

public sealed class TemplateMatchResult
{
    [JsonPropertyName("similarity")] public double Similarity { get; set; }
    [JsonPropertyName("button_area")] public List<double> ButtonArea { get; set; } = new();
}

public sealed class ButtonCenter
{
    [JsonPropertyName("asset")] public string Asset { get; set; } = "";
    [JsonPropertyName("button")] public List<double> Button { get; set; } = new();
    [JsonPropertyName("center")] public List<int> Center { get; set; } = new();
}

public sealed class PageInfo
{
    [JsonPropertyName("page")] public string Page { get; set; } = "";
    [JsonPropertyName("check_button")] public string? CheckButton { get; set; }
    [JsonPropertyName("is_main")] public bool IsMain { get; set; }
}

public sealed class PageListResult
{
    [JsonPropertyName("pages")] public List<PageInfo> Pages { get; set; } = new();
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class PageAppearResult
{
    [JsonPropertyName("page")] public string Page { get; set; } = "";
    [JsonPropertyName("appear")] public bool Appear { get; set; }
}

public sealed class ScaleResult
{
    [JsonPropertyName("factor")] public double Factor { get; set; }
    [JsonPropertyName("before")] public List<int> Before { get; set; } = new();
    [JsonPropertyName("after")] public List<int> After { get; set; } = new();
}

public sealed class ScreenshotInfo
{
    [JsonPropertyName("shape")] public List<int> Shape { get; set; } = new();
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

/// <summary>
/// 识图引擎客户端（**进程外**宿主）：C# 侧不实现任何图像算法，只按 JSON 协议把请求发给
/// 运行上游 Python 代码的 worker（见 tools/vision_worker.py）。
///
/// 与进程内宿主 <see cref="InProcessVisionEngine"/> 调的是同一份
/// <c>alas_vision.handle_line()</c>，类型化接口由 <see cref="VisionEngineBase"/> 统一实现，
/// 因此换宿主不影响上层。
///
/// 什么时候用哪个：进程内省掉每帧几十次的进程间往返（实测单次 6.83ms 里 4.38ms 是通信），
/// 是目标形态；进程外则在拿不到 CPython C API 的场景下仍能跑。
/// </summary>
public sealed class VisionWorker : VisionEngineBase
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly TimeSpan _timeout;
    private bool _disposed;

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

    protected override JsonNode CallRaw(string op, object? args)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VisionWorker));

        _stdin.Write(VisionProtocol.BuildRequest(NextId(), op, args));
        _stdin.Write('\n');
        _stdin.Flush();

        string? line = _stdout.ReadLine();
        if (line is null)
        {
            string stderr = string.Join("\n", Stderr.TakeLast(10));
            throw new InvalidOperationException(
                $"识图 worker 已退出（op={op}）。stderr:\n{stderr}");
        }
        return VisionProtocol.ParseResponse(line, op);
    }

    /// <summary>让 worker 自行退出（之后仍应调用 <see cref="Dispose"/> 回收进程）。</summary>
    public void Shutdown()
    {
        if (_disposed) return;
        try
        {
            CallRaw("shutdown", null);
        }
        catch (Exception)
        {
            // worker 可能已自行退出
        }
    }

    public override void Dispose()
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
        base.Dispose();
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
