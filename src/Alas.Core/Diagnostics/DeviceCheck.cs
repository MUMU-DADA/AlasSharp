using System.Text.Json;
using System.Text.Json.Serialization;
using Alas.Core;
using Alas.Device;
using Alas.Vision;

namespace Alas.Core.Diagnostics;

/// <summary>
/// 设备层验收（无需真机）：用**桩 adb 可执行文件**跑通整条真实调用链。
///
/// 验的是「进程调用 → 参数拼接 → 二进制 stdout 捕获 → 交给识图宿主解码 → 判定」
/// 这条链，而不是某个接口的 mock。桩是普通可执行脚本，所以容易出错的地方都被走到。
/// </summary>
internal static class DeviceCheck
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

    private sealed class Fixture
    {
        [JsonPropertyName("cases")] public List<FixtureCase> Cases { get; set; } = new();
    }

    /// <summary>
    /// 真机/模拟器验收。**只截图与查询，不发点击** —— 避免干扰用户正在运行的模拟器。
    /// </summary>
    private static readonly string[] DefaultPageIndicators =
    {
        "ui/CAMPAIGN_CHECK", "ui/DORM_CHECK", "ui/ACADEMY_CHECK", "ui/GUILD_CHECK",
        "ui/EVENT_CHECK", "ui/EXERCISE_CHECK", "ui/MISSION_CHECK", "ui/STORAGE_CHECK",
        "ui/BUILD_CHECK", "ui/ISLAND_CHECK", "ui/BATTLE_PASS_CHECK", "ui/COMMISSION_CHECK",
        "ui/DAILY_CHECK", "ui/FLEET_CHECK", "ui/CHANNEL_CHECK", "ui/IDLE",
        "ui/BACK_ARROW", "ui/GOTO_MAIN",
    };

    public static int RunReal(string adbPath, string serial, string forkDir, string toolsDir,
                              string server = "cn", string[]? assetsCsv = null)
    {
        string[] assetList = assetsCsv ?? Array.Empty<string>();
        var problems = new List<string>();
        using IVisionEngine vision = InProcessVisionEngine.StartFromAlasFork(forkDir, toolsDir);
        var adb = new ProcessAdbTransport(adbPath);
        var device = new DeviceController(adb, vision, serial);

        var devices = device.Devices();
        Console.WriteLine($"[devices ] {string.Join(", ", devices)}");
        if (!devices.Contains(serial)) problems.Add($"devices 未列出 {serial}");

        Console.WriteLine($"[get-state] {device.GetState()}");
        var size = device.ScreenSize();
        Console.WriteLine($"[wm size ] {size?.Width}x{size?.Height}");

        // 真实 screencap 延迟（这是生产里的关键路径，之前一直没机会测）
        var times = new List<double>();
        ScreenshotInfo info = null!;
        for (int i = 0; i < 8; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            info = device.Screenshot();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Console.WriteLine($"[screencap] shape={string.Join("x", info.Shape)}"
                          + $"  延迟 p50={times[times.Count / 2]:F1}ms min={times[0]:F1}ms"
                          + $" max={times[^1]:F1}ms（含 adb 传输 + 宿主解码）");
        if (info.Shape.Count != 3 || info.Shape[2] != 3)
            problems.Add($"截图形状异常: {string.Join("x", info.Shape)}");

        // 页面判定：对当前真实截图跑一批 ALAS 自己的页面指示按钮，看哪些出现。
        // 这是 ALAS 判断"现在在哪个页面"的真实做法，结果应自洽（不该有一堆同时为真）。
        vision.SetServer(server);
        string[] assets = assetList.Length > 0 ? assetList : DefaultPageIndicators;
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var batch = vision.AppearOnBatch(assets);
        double batchMs = sw2.Elapsed.TotalMilliseconds;
        var hits = batch.Results.Where(r => r.Appear).ToList();
        Console.WriteLine($"[页面判定] 候选项 {assets.Length}，命中 {hits.Count}"
                          + $"（宿主内 {batch.ElapsedMs:F2}ms，往返 {batchMs:F2}ms）");
        foreach (var h in hits)
            Console.WriteLine($"[命中    ] {h.Asset,-24} 容差={h.Tolerance?.ToString("F1") ?? "-"}");
        if (hits.Count == 0)
        {
            Console.WriteLine("[命中    ] 无 —— 当前画面不匹配任何页面指示按钮");
            var closest = batch.Results.Where(r => r.Tolerance is not null)
                .OrderBy(r => r.Tolerance).Take(5);
            Console.WriteLine("           最接近的 5 个（容差越小越像）：");
            foreach (var c2 in closest)
                Console.WriteLine($"             {c2.Asset,-24} 容差={c2.Tolerance:F1}");
        }

        // ---- 按上游原规则做页面识别
        // 规则来源：module/ui/page.py 的 Page.check_button + module/ui/ui.py 的 ui_page_appear。
        // 机制是 **Button.match（模板匹配）**，不是 appear_on（颜色检查）—— 后者只是快速预筛。
        var pageList = vision.PageList();
        Console.WriteLine();
        Console.WriteLine($"[页面规则] 上游共 {pageList.Count} 个页面，逐个按原规则判定：");
        var recognized = new List<string>();
        foreach (var pg in pageList.Pages)
        {
            try
            {
                var r = vision.PageAppear(pg.Page);
                if (r.Appear)
                {
                    recognized.Add(pg.Page);
                    Console.WriteLine($"           ✓ {pg.Page,-28} check={pg.CheckButton}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"           ! {pg.Page}: {ex.Message}"); }
        }
        if (recognized.Count == 0)
            Console.WriteLine("           （无页面被识别）");
        else
            Console.WriteLine($"[页面规则] 识别到 {recognized.Count} 个：{string.Join(", ", recognized)}");
        // ---- 识别层缩放适配扫描
        // 模拟器 DPI 与素材采集时不一致会让所有 UI 元素错位。用 ALAS 自己的缩放机制
        // （Template.match 就是对图像做 cv2.resize）反过来扫：哪个因子让最多素材命中，
        // 哪个就是当前 UI 的真实缩放比。**不改模拟器，只在识别层适配。**
        byte[] raw = device.ScreenshotBytes();
        Console.WriteLine();
        Console.WriteLine("[缩放扫描] 用同一帧反复重采样，统计各因子下的命中数：");
        double[] factors = { 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90,
                             0.95, 1.00, 1.10, 1.20, 1.33, 1.50, 1.70 };
        int bestHits = -1; double bestFactor = 0; double bestTol = double.MaxValue;
        foreach (double fac in factors)
        {
            vision.SetScreenshot(raw);
            vision.ScaleScreenshot(fac);
            var b = vision.AppearOnBatch(assets);
            int h = b.Results.Count(r => r.Appear);
            double t = b.Results.Where(r => r.Tolerance is not null)
                                .Min(r => r.Tolerance!.Value);
            Console.WriteLine($"           factor={fac:F2}  命中 {h,3}   最好容差 {t,7:F1}");
            if (h > bestHits || (h == bestHits && t < bestTol))
            { bestHits = h; bestFactor = fac; bestTol = t; }
        }
        Console.WriteLine($"[缩放扫描] 最佳 factor={bestFactor:F2}（命中 {bestHits}，最好容差 {bestTol:F1}）");
        Console.WriteLine();
        if (problems.Count == 0) { Console.WriteLine("结果: OK"); return 0; }
        Console.WriteLine($"结果: FAIL（{problems.Count} 处）");
        foreach (string p in problems) Console.WriteLine($"  - {p}");
        return 1;
    }
    public static int Run(string fixturePath, string forkDir, string toolsDir, string dataDir)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(
            File.ReadAllText(Path.GetFullPath(fixturePath)), UpstreamData.Options)
            ?? throw new InvalidDataException("基准反序列化失败");
        // 挑一个非灰度、有真值的用例当截图
        var probe = fixture.Cases.First(c => c.AppearDefault is not null && c.Mode != "L");
        string png = Path.Combine(forkDir, probe.File.Replace("./", "")
            .Replace('/', Path.DirectorySeparatorChar));
        string logPath = Path.Combine(Path.GetTempPath(), $"stub_adb_{Environment.ProcessId}.log");
        if (File.Exists(logPath)) File.Delete(logPath);

        string python = Path.Combine(forkDir, ".venv", "Scripts", "python.exe");
        var transport = new ProcessAdbTransport(python,
            new[] { Path.Combine(toolsDir, "stub_adb.py") })
        {
            Environment = new Dictionary<string, string>
            {
                ["STUB_ADB_SCREENSHOT"] = png,
                ["STUB_ADB_LOG"] = logPath,
            },
        };

        var problems = new List<string>();
        using IVisionEngine vision = InProcessVisionEngine.StartFromAlasFork(forkDir, toolsDir);
        var device = new DeviceController(transport, vision, serial: "127.0.0.1:5555");

        // ---- 设备发现
        var devices = device.Devices();
        Console.WriteLine($"[devices ] {string.Join(", ", devices)}");
        if (!devices.Contains("127.0.0.1:5555")) problems.Add("devices 未列出桩设备");

        string state = device.GetState();
        Console.WriteLine($"[get-state] {state}");
        if (state != "device") problems.Add($"get-state 返回 {state}");

        // ---- 分辨率
        var size = device.ScreenSize();
        Console.WriteLine($"[wm size ] {size?.Width}x{size?.Height}");
        if (size != (1280, 720)) problems.Add($"wm size 解析异常: {size}");

        // ---- 截图：adb 字节流 → 宿主解码
        var info = device.Screenshot();
        Console.WriteLine($"[screencap] shape={string.Join("x", info.Shape)}（字节经宿主解码，未跨语言边界）");
        if (info.Shape.Count != 3 || info.Shape[0] != 720 || info.Shape[1] != 1280 || info.Shape[2] != 3)
            problems.Add($"截图形状异常: {string.Join("x", info.Shape)}");

        // ---- 用这张截图做一次真实判定，与真值比对
        vision.SetServer(probe.Server);
        var verdict = vision.AppearOn(probe.Id);
        Console.WriteLine($"[判定    ] {probe.Id} [{probe.Server}] = {verdict.Appear}"
                          + $"，真值 {probe.AppearDefault}");
        if (verdict.Appear != probe.AppearDefault!.Value)
            problems.Add($"{probe.Id} 判定与真值不符");

        // ---- 操作：验证参数确实按上游形态传给了 adb
        device.Click(640, 360);
        device.Swipe(100, 200, 300, 400, 150);
        string log = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
        Console.WriteLine("[操作    ] 桩 adb 收到的命令：");
        foreach (string line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Console.WriteLine($"           {line}");
        if (!log.Contains("input tap 640 360")) problems.Add("click 未按预期传递参数");
        if (!log.Contains("input swipe 100 200 300 400 150")) problems.Add("swipe 未按预期传递参数");

        Console.WriteLine();
        if (problems.Count == 0)
        {
            Console.WriteLine("结果: OK");
            return 0;
        }
        Console.WriteLine($"结果: FAIL（{problems.Count} 处）");
        foreach (string p in problems) Console.WriteLine($"  - {p}");
        return 1;
    }
}
