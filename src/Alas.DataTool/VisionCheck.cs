using System.Text.Json;
using System.Text.Json.Serialization;
using Alas.Core;
using Alas.Vision;

namespace Alas.DataTool;

/// <summary>
/// 桥接验收：C# 通过 worker 驱动**上游 Python 识图代码**，结果与独立算出的真值比对。
///
/// 真值来自 tools/make_imaging_fixture.py（由 ALAS 自己的 color_similar/get_color 产出），
/// 与 worker 的调用路径相互独立，因此能真正校验桥接是否忠实：
///   - 素材 id → 上游对象 的映射对不对
///   - 分服切换（server.server 全局）对不对
///   - 截图加载、参数传递、JSON 序列化有没有走样
/// </summary>
internal static class VisionCheck
{
    private sealed class FixtureCase
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("server")] public string Server { get; set; } = "";
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("stored_color")] public List<double>? StoredColor { get; set; }
        [JsonPropertyName("appear_default")] public bool? AppearDefault { get; set; }
    }

    private sealed class ImagingFixture
    {
        [JsonPropertyName("cases")] public List<FixtureCase> Cases { get; set; } = new();
    }

    public static int Run(string fixturePath, string forkDir, int limit)
    {
        if (!File.Exists(fixturePath))
            return Fail($"基准不存在: {fixturePath}\n先跑 tools/make_imaging_fixture.py");

        var fixture = JsonSerializer.Deserialize<ImagingFixture>(
            File.ReadAllText(fixturePath), UpstreamData.Options)
            ?? throw new InvalidDataException("基准反序列化失败");

        var cases = fixture.Cases
            .Where(c => c.AppearDefault is not null && c.StoredColor is not null)
            .Take(limit)
            .ToList();
        if (cases.Count == 0) return Fail("基准里没有可比对的用例");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var worker = VisionWorker.Start(new VisionWorkerOptions { ForkDirectory = forkDir });
        var info = worker.Ping();
        double startupMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"worker      : Python {info.Python} / numpy {info.Numpy} / cv2 {info.Cv2}");
        Console.WriteLine($"上游仓库    : {info.Fork}");
        Console.WriteLine($"启动耗时    : {startupMs:F0} ms（含 Python 解释器 + numpy/cv2 导入）");
        Console.WriteLine($"用例        : {cases.Count}");
        Console.WriteLine();

        int verdictMismatch = 0, colorMismatch = 0, errors = 0;
        string currentServer = "";
        double loadMs = 0, queryMs = 0;
        var samples = new List<string>();

        foreach (var c in cases)
        {
            if (c.Server != currentServer)
            {
                worker.SetServer(c.Server);
                currentServer = c.Server;
            }
            string path = c.File.Replace("./", "").Replace('/', Path.DirectorySeparatorChar);
            try
            {
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                worker.LoadScreenshot(path);
                loadMs += t0.Elapsed.TotalMilliseconds;

                t0.Restart();
                var r = worker.AppearOn(c.Id);
                queryMs += t0.Elapsed.TotalMilliseconds;

                if (r.Appear != c.AppearDefault!.Value)
                {
                    verdictMismatch++;
                    if (samples.Count < 8)
                        samples.Add($"{c.Id} [{c.Server}]: worker={r.Appear} 真值={c.AppearDefault}");
                }
                if (r.Expected is not null && !ColorsEqual(r.Expected, c.StoredColor!))
                {
                    colorMismatch++;
                    if (samples.Count < 8)
                        samples.Add($"{c.Id} [{c.Server}]: 期望色 worker=[{string.Join(",", r.Expected)}] "
                                    + $"真值=[{string.Join(",", c.StoredColor!)}]");
                }
            }
            catch (Exception ex)
            {
                errors++;
                if (samples.Count < 8) samples.Add($"{c.Id} [{c.Server}]: {ex.Message}");
            }
        }

        double avgLoad = loadMs / cases.Count, avgQuery = queryMs / cases.Count;
        Console.WriteLine($"[appear 判定] 与真值不一致 {verdictMismatch}");
        Console.WriteLine($"[期望色    ] 与真值不一致 {colorMismatch}");
        Console.WriteLine($"[调用异常  ] {errors}");
        foreach (var s in samples) Console.WriteLine($"   {s}");
        Console.WriteLine();
        Console.WriteLine($"[性能] 截图加载 {avgLoad:F2} ms/次（含 PNG 解码），"
                          + $"appear_on 往返 {avgQuery:F3} ms/次");

        // 批量接口：验证每帧几十次判定时的往返成本
        var subset = cases.Take(Math.Min(64, cases.Count)).Select(c => c.Id).ToList();
        var t1 = System.Diagnostics.Stopwatch.StartNew();
        var batch = worker.AppearOnBatch(subset);
        double batchMs = t1.Elapsed.TotalMilliseconds;
        Console.WriteLine($"[批量] {subset.Count} 条一次往返 {batchMs:F2} ms"
                          + $"（worker 内部 {batch.ElapsedMs:F3} ms，往返开销 "
                          + $"{batchMs - batch.ElapsedMs:F2} ms）");

        int total = verdictMismatch + colorMismatch + errors;
        Console.WriteLine();
        Console.WriteLine(total == 0 ? "结果: OK" : $"结果: FAIL（{total} 处）");
        return total == 0 ? 0 : 1;
    }

    private static bool ColorsEqual(List<double> a, List<double> b)
    {
        if (a.Count < 3 || b.Count < 3) return false;
        for (int i = 0; i < 3; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-9) return false;
        return true;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
