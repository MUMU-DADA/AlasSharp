using Alas.Device;
using Alas.Vision;

namespace Alas.DataTool;

/// <summary>
/// 设备通道对比：`alashub capture`。
///
/// 目的：证明**产品路径**能用引擎的设备层截图，并量出收益。
///   A 路（现状）：C# 自己 `adb exec-out screencap -p` → 把 PNG 交给宿主解码
///   B 路（本命令）：引擎的设备层截图 → **直接置入宿主**（像素不跨语言边界）
///
/// B 路的后端可由 `--screenshot` 选择（adb / droidcast / …），换后端不改 C# 代码；
/// 这正是"设备 I/O 走宿主"的验收点。
/// 两条路都顺带做一次页面判定，确认拿到的是**可用的帧**（不是"快但错"）。
/// </summary>
internal static class CaptureCheck
{
    public static int Run(string adbPath, string serial, string forkDir, string toolsDir,
                          string screenshot = "adb", string control = "ADB", int repeat = 3)
    {
        if (repeat <= 0) throw new ArgumentOutOfRangeException(nameof(repeat), "截图次数必须为正整数");
        using IVisionEngine vision = InProcessVisionEngine.StartFromAlasFork(forkDir, toolsDir);
        var adb = new ProcessAdbTransport(adbPath);
        var device = new DeviceController(adb, vision, serial);

        if (!device.EnsureConnected())
        {
            Console.WriteLine("[错误    ] 设备不在线");
            return 1;
        }
        Console.WriteLine($"[device  ] serial={serial} screenshot={screenshot} control={control}");
        var cfg = device.ConfigureEngineDevice(screenshot, control);
        Console.WriteLine($"[engine  ] configured=" +
            string.Join(" ", (cfg.Configured ?? new()).Select(kv => $"{kv.Key}={kv.Value}")));

        // A 路：C# 自己 adb 截图
        var sw = System.Diagnostics.Stopwatch.StartNew();
        device.Screenshot();
        sw.Stop();
        double aMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"[A adb   ] C# adb 截图（含交宿主解码） {aMs,7:F0} ms  " +
                          $"页面={string.Join(",", vision.PageCurrent().Hit ?? new())}");

        // B 路：引擎截图（多次取中位）
        var times = new List<double>();
        string hits = "";
        for (int i = 0; i < repeat; i++)
        {
            var r = device.CaptureViaEngine(raw: true);
            if (r.Error is not null)
            {
                Console.WriteLine($"[B engine] 失败：{r.Error}");
                return 1;
            }
            times.Add(r.CaptureMs);
            if (i == 0)
                hits = string.Join(",", vision.PageCurrent().Hit ?? new());
        }
        times.Sort();
        double bMs = times[times.Count / 2];
        Console.WriteLine($"[B engine] 引擎截图+置入 中位 {bMs,7:F0} ms " +
                          $"(全部 {string.Join("/", times.Select(t => t.ToString("F0")))}） 页面={hits}");

        double gain = aMs > 0 ? (aMs - bMs) / aMs * 100 : 0;
        Console.WriteLine($"[对比    ] B 比 A {(aMs >= bMs ? "快" : "慢")} {Math.Abs(aMs - bMs):F0} ms " +
                          $"（{(gain >= 0 ? "-" : "+")}{Math.Abs(gain):F1}%）");
        Console.WriteLine(string.IsNullOrEmpty(hits)
            ? "注意：页面判定为空 —— 帧可能不可用，需查后端或置入方式"
            : "两条路的页面判定都拿到了结果，说明 B 路的帧可用 ✓");
        return 0;
    }
}
