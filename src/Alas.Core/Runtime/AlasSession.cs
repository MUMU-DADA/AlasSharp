using System.Text.Json.Nodes;
using Alas.Vision;

namespace Alas.Runtime;

/// <summary>
/// 常驻会话（R1 核心）：**一个进程里只构造一次**识图宿主与设备后端，之后所有任务复用。
///
/// 为什么必须常驻：设备后端的收益只在长驻进程里兑现（scrcpy 抓图 128ms / MaaTouch 点击 53ms，
/// 见 docs/device-engine.md），而宿主首调要付 530ms 的一次性导入代价 —— 每关重开一次进程等于
/// 把这些全丢掉。R1 阶段门槛因此是"同一进程连续运行多个任务时宿主和设备只初始化一次"。
///
/// 会话只负责**资源与证据**（宿主、设备、日志、工件目录、释放），不负责业务判定；
/// 判定在 <see cref="CampaignBatchRunner"/> 与结果合同里。
/// </summary>
public sealed class AlasSession : IDisposable
{
    /// <summary>造宿主的工厂。默认走进程内 CPython；离线自检可以注入替身（不启动 Python）。</summary>
    public delegate IVisionEngine EngineFactory(SessionOptions options);

    private readonly SessionLog _log;
    private bool _disposed;

    public SessionOptions Options { get; }
    public IVisionEngine Vision { get; }
    public SessionLog Log => _log;

    /// <summary>宿主构造次数。常驻会话恒为 1；>1 就说明有人又在任务里重开了宿主。</summary>
    public int HostStartCount { get; private set; }
    /// <summary>设备后端配置次数。只在真跑时发生，且一次会话只配一次。</summary>
    public int DeviceConfigureCount { get; private set; }
    /// <summary>本次会话的工件目录（没有配置工件目录时为 null）。</summary>
    public string? RunDirectory { get; }

    private AlasSession(SessionOptions options, IVisionEngine vision, SessionLog log)
    {
        Options = options;
        Vision = vision;
        _log = log;
        HostStartCount = 1;
        RunDirectory = options.ArtifactsDirectory is null
            ? null
            : UniqueRunDirectory(options.ArtifactsDirectory);
        if (RunDirectory is not null) Directory.CreateDirectory(RunDirectory);
    }

    /// <summary>
    /// 本次运行的工件目录：`<artifacts>/<时间戳>`，**同一秒内的第二次运行要能区分开**。
    ///
    /// 为什么必须唯一：时间戳只到秒，而自动化里连跑两次是常态（实测 `alashub runs` 只列出 1 条，
    /// 因为第二次运行把第一次的工件目录覆盖了 —— 证据被悄悄替换掉，事后看不出来）。
    /// 撞名时追加序号，保持"按目录名排序 = 按时间排序"这个性质不变。
    /// </summary>
    private static string UniqueRunDirectory(string artifactsRoot)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd'T'HHmmss");
        string candidate = Path.Combine(artifactsRoot, stamp);
        for (int suffix = 2; Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(artifactsRoot, $"{stamp}-{suffix}");
        return candidate;
    }

    /// <summary>
    /// 启动会话。**顺序是刻意的**：先校验参数（避免带着半截配置启动宿主），
    /// 再起宿主，最后（只有真跑才）配置设备。
    /// </summary>
    public static AlasSession Start(SessionOptions options, EngineFactory? factory = null,
                                    SessionLog? log = null)
    {
        options.Validate();
        log ??= new SessionLog();
        factory ??= DefaultFactory;
        var started = System.Diagnostics.Stopwatch.StartNew();
        IVisionEngine engine;
        try
        {
            engine = factory(options);
        }
        catch (Exception error)
        {
            throw RuntimeErrors.Wrap(error, "启动识图宿主失败");
        }
        started.Stop();

        var session = new AlasSession(options, engine, log);
        session._log.Info("session", "识图宿主已启动", new Dictionary<string, object?>
        {
            ["repo"] = options.RepoDirectory,
            ["dry_run"] = options.DryRun,
            ["host_start_ms"] = Math.Round(started.Elapsed.TotalMilliseconds, 1),
        });

        if (options.ShouldConfigureDevice)
        {
            try
            {
                var configured = engine.ConfigureDevice(options.Serial!, options.ScreenshotBackend,
                                                        options.ControlBackend);
                session.DeviceConfigureCount = 1;
                session._log.Info("session", "设备后端已配置", new Dictionary<string, object?>
                {
                    ["serial"] = options.Serial,
                    ["screenshot"] = options.ScreenshotBackend,
                    ["control"] = options.ControlBackend,
                    ["configured"] = configured.Configured is null
                        ? null : string.Join(",", configured.Configured.Select(kv => $"{kv.Key}={kv.Value}")),
                });
            }
            catch (Exception error)
            {
                session.Dispose();
                throw RuntimeErrors.Wrap(error, "配置设备后端失败");
            }
        }
        return session;
    }

    private static IVisionEngine DefaultFactory(SessionOptions options)
        => InProcessVisionEngine.StartFromAlasFork(options.RepoDirectory, options.ToolsDirectory);

    /// <summary>把一件工件写进本次会话目录；没有配置工件目录时返回 null（不假装存了）。</summary>
    public string? WriteArtifact(string name, JsonNode payload)
    {
        if (RunDirectory is null) return null;
        string path = Path.Combine(RunDirectory, name);
        File.WriteAllText(path, payload.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        _log.Info("artifacts", "已写入工件", new Dictionary<string, object?> { ["path"] = path });
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Vision.Dispose();
            _log.Info("session", "识图宿主已释放");
        }
        catch (Exception error)
        {
            // 释放失败不能盖掉真正的失败原因（R1：异常路径也要保留证据）。
            _log.Warn("session", "释放识图宿主时报错", new Dictionary<string, object?>
            {
                ["error"] = error.Message,
            });
        }
        if (RunDirectory is not null)
        {
            try
            {
                _log.WriteJsonLines(Path.Combine(RunDirectory, "session-log.jsonl"));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"写会话日志失败: {error.Message}");
            }
        }
    }
}
