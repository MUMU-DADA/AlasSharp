using System.Diagnostics;
using Alas.Device;
using Alas.Vision;

namespace Alas.DataTool;

/// <summary>
/// 常驻 runner 的最小骨架：`alashub run`。
///
/// 为什么需要它：实测证明设备后端（scrcpy 抓图 128ms、MaaTouch 点击 53ms）的收益
/// **只在长驻进程里兑现** —— CLI 一次调用一个进程，设备层初始化会吃掉全部收益
/// （见 docs/device-engine.md）。S3 的自动化循环必然是长驻的，这里先把那个"壳"搭起来：
///
///   1) 一次性构造识图引擎 + 设备层（含后端选择），并做预热；
///   2) 之后按 tick 循环：抓帧（引擎通道，像素不跨语言边界）→ 页面判定 → 记录耗时；
///   3) 定期打印稳态统计（抓帧中位/分位、判定耗时、tick 实际间隔），结束时给总表。
///
/// 现在它只"看"不"动"（不改游戏状态），是安全的观测器；S3 的战斗/关卡循环会挂在 tick 上。
/// </summary>
internal static class RunLoop
{
    public static int Run(string adbPath, string serial, string forkDir, string toolsDir,
                          string screenshot, string control, double tickSeconds, double seconds)
    {
        using IVisionEngine vision = InProcessVisionEngine.StartFromAlasFork(forkDir, toolsDir);
        var adb = new ProcessAdbTransport(adbPath);
        var device = new DeviceController(adb, vision, serial);
        device.UseEngineCapture = true;

        if (!device.EnsureConnected())
        {
            Console.WriteLine("[错误    ] 设备不在线");
            return 1;
        }
        var cfg = device.ConfigureEngineDevice(screenshot, control);
        Console.WriteLine($"[runner  ] serial={serial} screenshot={screenshot} control={control} " +
                          $"tick={tickSeconds:F2}s duration={seconds:F0}s");

        var warm = Stopwatch.StartNew();
        var first = device.CaptureViaEngine(raw: true);
        warm.Stop();
        if (first.Error is not null)
        {
            Console.WriteLine($"[错误    ] 首次抓帧失败：{first.Error}");
            return 1;
        }
        Console.WriteLine($"[warmup  ] 设备层构造+首次抓帧 {warm.Elapsed.TotalMilliseconds:F0} ms " +
                          $"（这一步在长驻进程里只付一次）");

        var caps = new List<double>();
        var thinks = new List<double>();
        var gaps = new List<double>();
        var deadline = Stopwatch.StartNew();
        var lastTick = deadline.Elapsed.TotalSeconds;
        int ticks = 0, errors = 0;
        string lastPages = "";

        while (deadline.Elapsed.TotalSeconds < seconds)
        {
            var t0 = Stopwatch.StartNew();
            var r = device.CaptureViaEngine(raw: true);
            if (r.Error is not null) { errors++; }
            else caps.Add(r.CaptureMs);
            var t1 = Stopwatch.StartNew();
            var pc = vision.PageCurrent();
            t1.Stop();
            thinks.Add(t1.Elapsed.TotalMilliseconds);
            lastPages = string.Join(",", pc.Hit ?? new());
            ticks++;

            double now = deadline.Elapsed.TotalSeconds;
            gaps.Add((now - lastTick) * 1000);
            lastTick = now;
            t0.Stop();

            if (ticks % 5 == 0)
                Console.WriteLine($"[tick {ticks,4}] cap={r.CaptureMs,6:F0}ms think={t1.Elapsed.TotalMilliseconds,4:F0}ms " +
                                  $"pages={lastPages}");

            double elapsed = deadline.Elapsed.TotalSeconds;
            if (tickSeconds > 0 && elapsed + tickSeconds < seconds)
                Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0, tickSeconds - t0.Elapsed.TotalSeconds)));
        }

        static string Stat(List<double> xs)
        {
            if (xs.Count == 0) return "n/a";
            var s = xs.OrderBy(x => x).ToList();
            return $"n={s.Count} 中位={s[s.Count / 2]:F0} p25={s[s.Count / 4]:F0} " +
                   $"p75={s[3 * s.Count / 4]:F0} 最大={s[^1]:F0}";
        }
        double wall = deadline.Elapsed.TotalSeconds;
        Console.WriteLine();
        Console.WriteLine($"[稳态    ] 共 {ticks} tick / {wall:F1}s（{ticks / wall:F2} tick/s，错误 {errors}）");
        Console.WriteLine($"[抓帧    ] {Stat(caps)}");
        Console.WriteLine($"[判定    ] {Stat(thinks)}");
        Console.WriteLine($"[tick间隔] {Stat(gaps)}");
        Console.WriteLine($"[末页    ] {lastPages}");
        return errors == 0 ? 0 : 1;
    }
}
