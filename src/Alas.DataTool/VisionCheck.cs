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
        [JsonPropertyName("mode")] public string Mode { get; set; } = "";
        [JsonPropertyName("stored_color")] public List<double>? StoredColor { get; set; }
        [JsonPropertyName("appear_default")] public bool? AppearDefault { get; set; }
    }

    private sealed class ImagingFixture
    {
        [JsonPropertyName("cases")] public List<FixtureCase> Cases { get; set; } = new();
    }

    public static int Run(string fixturePath, string forkDir, string toolsDir, int limit, string mode)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "用例上限必须为正整数");
        if (!File.Exists(fixturePath))
            return Fail($"基准不存在: {fixturePath}\n先跑 tools/make_imaging_fixture.py");

        // 注意：必须在启动宿主**之前**把基准读进来。
        // 进程内宿主 import alas_vision 时会 os.chdir(ALAS 仓库)，此后再用相对路径就会失效。
        var fixture = JsonSerializer.Deserialize<ImagingFixture>(
            File.ReadAllText(Path.GetFullPath(fixturePath)), UpstreamData.Options)
            ?? throw new InvalidDataException("基准反序列化失败");

        var cases = fixture.Cases
            .Where(c => c.AppearDefault is not null && c.StoredColor is not null)
            .Take(limit)
            .ToList();
        if (cases.Count == 0) return Fail("基准里没有可比对的用例");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using IVisionEngine engine = mode switch
        {
            "inproc" => InProcessVisionEngine.StartFromAlasFork(forkDir, toolsDir),
            "worker" => VisionWorker.Start(new VisionWorkerOptions { ForkDirectory = forkDir }),
            _ => throw new ArgumentException($"未知宿主: {mode}（可用 inproc / worker）"),
        };
        var info = engine.Ping();
        double startupMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"宿主        : {mode}（{(mode == "inproc" ? "进程内 CPython" : "进程外 worker")}）");
        Console.WriteLine($"Python      : {info.Python} / numpy {info.Numpy} / cv2 {info.Cv2}");
        Console.WriteLine($"上游仓库    : {info.Fork}");
        Console.WriteLine($"启动耗时    : {startupMs:F0} ms（含 Python 解释器 + numpy/cv2 导入）");
        Console.WriteLine($"用例        : {cases.Count}");
        Console.WriteLine();

        int verdictMismatch = 0, colorMismatch = 0, errors = 0;
        string currentServer = "";
        double loadMs = 0, queryMs = 0, innerMs = 0;
        var detailSums = new Dictionary<string, double>();
        Dictionary<string, double>? firstDetail = null;
        var samples = new List<string>();

        foreach (var c in cases)
        {
            if (c.Server != currentServer)
            {
                engine.SetServer(c.Server);
                currentServer = c.Server;
            }
            string path = c.File.Replace("./", "").Replace('/', Path.DirectorySeparatorChar);
            try
            {
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                engine.LoadScreenshot(path);
                loadMs += t0.Elapsed.TotalMilliseconds;

                t0.Restart();
                var r = engine.AppearOn(c.Id, detail: true);
                queryMs += t0.Elapsed.TotalMilliseconds;
                // Python 侧自报的耗时：用它把「宿主里真正算的时间」与「传输/编解码」切开
                if (r.ElapsedMs is not null) innerMs += r.ElapsedMs.Value;
                if (r.Detail is not null)
                {
                    firstDetail ??= r.Detail;
                    foreach (var kv in r.Detail)
                        detailSums[kv.Key] = detailSums.GetValueOrDefault(kv.Key) + kv.Value;
                }

                if (r.Appear != c.AppearDefault!.Value)
                {
                    verdictMismatch++;
                    if (samples.Count < 8)
                        samples.Add($"{c.Id} [{c.Server}]: 宿主={r.Appear} 真值={c.AppearDefault}");
                }
                if (r.Expected is not null && !ColorsEqual(r.Expected, c.StoredColor!))
                {
                    colorMismatch++;
                    if (samples.Count < 8)
                        samples.Add($"{c.Id} [{c.Server}]: 期望色 宿主=[{string.Join(",", r.Expected)}] "
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
        Console.WriteLine($"[性能] 截图加载 {avgLoad:F2} ms/次（含 PNG 解码）");
        Console.WriteLine($"       appear_on 单次总耗时 {avgQuery:F3} ms"
                          + $" = 宿主内计算 {innerMs / cases.Count:F3} ms"
                          + $" + 传输/编解码 {avgQuery - innerMs / cases.Count:F3} ms");

        // 批量接口：验证每帧几十次判定时的往返成本
        var subset = cases.Take(Math.Min(64, cases.Count)).Select(c => c.Id).ToList();
        var t1 = System.Diagnostics.Stopwatch.StartNew();
        var batch = engine.AppearOnBatch(subset);
        double batchMs = t1.Elapsed.TotalMilliseconds;
        Console.WriteLine($"[批量] {subset.Count} 条一次往返 {batchMs:F2} ms"
                          + $"（宿主内部 {batch.ElapsedMs:F3} ms，调度开销 "
                          + $"{batchMs - batch.ElapsedMs:F2} ms）");

        // 设备路径预演：真实设备给的是**截图字节流**（adb screencap），不是文件路径。
        // 用同一批素材的 PNG 字节走 SetScreenshot，判定结果必须与走文件的完全一致 ——
        // 这样在拿到模拟器之前就能把设备路径的解码环节验掉。
        int byteMismatch = 0, byteSkipped = 0;
        var byteSamples = new List<string>();
        string byteServer = "";
        foreach (var c in cases)
        {
            // ⚠️ 必须切服务器！漏了这一步会让所有用例都用上一个服务器判定，
            //    与按各服算出的真值对不上（实测：40 例中 11 例失败，查了半天发现是测试代码的锅）。
            if (c.Server != byteServer) { engine.SetServer(c.Server); byteServer = c.Server; }
            // 灰度素材（PIL mode L）两条解码路径本就不同，不是 bug：
            //   文件路径走上游 load_image（PIL）→ 二维数组，get_color 返回 (mean,0,0)
            //   字节路径走 cv2.imdecode(IMREAD_COLOR) → 强制三通道 → (mean,mean,mean)
            // 真机截图永远是三通道 PNG，所以**字节路径才贴近生产**；
            // 基准里灰度素材的真值反而是"拿素材图当截图"的产物。显式跳过并计数。
            if (c.Mode == "L") { byteSkipped++; continue; }
            string path = Path.Combine(forkDir, c.File.Replace("./", "")
                .Replace('/', Path.DirectorySeparatorChar));
            try
            {
                engine.SetScreenshot(File.ReadAllBytes(path), c.File);
                var r = engine.AppearOn(c.Id);
                if (r.Appear != c.AppearDefault!.Value)
                {
                    byteMismatch++;
                    if (byteSamples.Count < 5)
                        byteSamples.Add($"{c.Id}: 字节路径={r.Appear} 真值={c.AppearDefault}");
                }
            }
            catch (Exception ex)
            {
                byteMismatch++;
                if (byteSamples.Count < 5) byteSamples.Add($"{c.Id}: {ex.Message}");
            }
        }
        Console.WriteLine($"[字节路径] 截图以字节流送入（模拟 adb screencap）→ 与真值不一致 {byteMismatch}"
                          + $"（跳过灰度素材 {byteSkipped} 例）");
        if (byteMismatch > 0)
            Console.WriteLine("       ⚠ 未查清：文件路径(PIL) 与 字节路径(cv2.imdecode) 的判定差异。"
                              + " 接入设备层之前必须查清——真实设备走的就是字节路径。");
        foreach (var s in byteSamples) Console.WriteLine($"   {s}");
        // 判别实验：ping 在 Python 侧几乎不干活，它的往返耗时就是「协议栈固有开销」。
        // 用它把「宿主内计算」与「跨边界成本」彻底分开。
        const int pingCount = 200;
        var tp = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < pingCount; i++) engine.Ping();
        double pingMs = tp.Elapsed.TotalMilliseconds / pingCount;
        Console.WriteLine($"[ping ] {pingCount} 次平均 {pingMs:F3} ms/次"
                          + "（Python 侧几乎零计算 → 这就是协议栈固有开销）");

        // 变量隔离：同一张（已热的）截图上重复单次调用。
        // 主循环每次 appear_on 之前都重新 LoadScreenshot，若这里显著更快，
        // 说明主循环那个 7~14ms 是「冷图首次访问」而不是每次调用的固有成本。
        var warmSet = cases.Take(Math.Min(100, cases.Count)).ToList();
        engine.LoadScreenshot(warmSet[0].File.Replace("./", "")
            .Replace('/', Path.DirectorySeparatorChar));
        var tw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var c in warmSet) engine.AppearOn(c.Id);
        double warmMs = tw.Elapsed.TotalMilliseconds / warmSet.Count;
        Console.WriteLine($"[热图 ] 同图重复单次调用 {warmSet.Count} 次平均 {warmMs:F3} ms/次");

        if (detailSums.Count > 0)
        {
            // 首调包含了 ALAS 模块的一次性导入（实测 ~0.5s），不能摊进平均值误导结论，
            // 因此这里只报「首调」与「其余调用的均值」，并单独列出总导入代价。
            double first = firstDetail!.GetValueOrDefault("color_of");
            double restAvg = (detailSums.GetValueOrDefault("color_of") - first)
                             / Math.Max(1, cases.Count - 1);
            Console.WriteLine($"[首调] {first:F1} ms（一次性：导入上游模块链）");
            Console.WriteLine($"[稳态] 其余 {cases.Count - 1} 次平均 {restAvg:F3} ms/次"
                              + "（这才是每帧真正的成本）");
        }

        // ⚠️ byteMismatch 暂**不计入**验收判据：字节路径与文件路径的判定差异尚未查清，
        //    在查清之前不能让它污染"验收通过"的结论，也不能假装它通过了。
        int total = verdictMismatch + colorMismatch + errors + byteMismatch;
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
