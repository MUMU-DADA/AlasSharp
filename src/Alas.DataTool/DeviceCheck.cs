using System.Text.Json;
using System.Text.Json.Serialization;
using Alas.Core;
using Alas.Device;
using Alas.Vision;

namespace Alas.DataTool;

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
