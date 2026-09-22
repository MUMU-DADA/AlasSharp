using System.Text.Json;
using Alas.MapDetection;
using Alas.Vision;

namespace Alas.DataTool;

/// <summary>
/// S2（地图识别）的产品路径验收：`alashub map`。
///
/// 与 `tools/diagnostics/verify_map_detection.py` 的区别：那个是 Python 诊断脚本，
/// 这个走**产品路径**（C# → 进程内 CPython → 上游 module/map_detection），
/// 证明"适配完的 S2 能被 C# 直接调用"，而不是只在我的诊断脚本里能跑。
///
/// 用法：
///   alashub map --fixture data/fixtures/os_map.png   # 离线：用已有截图
///   alashub map --adb &lt;adb&gt; --serial &lt;serial&gt;      # 真机：先截图再识别
/// </summary>
internal static class MapCheck
{
    public static int Run(string? fixture, string forkDir, string toolsDir,
                          string? adbPath, string? serial)
    {
        using IVisionEngine vision = InProcessVisionEngine.StartFromAlasFork(forkDir, toolsDir);
        var client = new MapDetectionClient(vision);

        if (!string.IsNullOrEmpty(fixture))
        {
            vision.LoadScreenshot(fixture);
            Console.WriteLine($"[source ] fixture {fixture}");
        }
        else if (!string.IsNullOrEmpty(adbPath) && !string.IsNullOrEmpty(serial))
        {
            var adb = new Alas.Device.ProcessAdbTransport(adbPath);
            var device = new Alas.Device.DeviceController(adb, vision, serial);
            if (!device.EnsureConnected())
            {
                Console.WriteLine("[错误   ] 设备不在线");
                return 1;
            }
            device.Screenshot();
            Console.WriteLine($"[source ] adb {serial}（已截图并交给宿主解码）");
        }
        else
        {
            Console.WriteLine("用法: map [--fixture <png>] 或 map --adb <adb> --serial <serial>");
            return 2;
        }

        int problems = 0;

        // 1) 素材链：读不出来就是环境问题，立刻报
        var assets = client.Assets();
        Console.WriteLine($"[assets ] detecting_area={string.Join(",", assets.DetectingArea)} " +
                          $"ui_mask={Fmt(assets.UiMask)} tile_center={Fmt(assets.TileCenter)} " +
                          $"tile_corner={Fmt(assets.TileCorner)}");
        if (assets.UiMask is null || assets.TileCenter is null || assets.TileCorner is null)
        {
            Console.WriteLine("[错误   ] S2 素材链不完整（UI 遮罩或瓦片模板读不出来）");
            problems++;
        }

        // 2) 大世界：单应性 + 坐标往返自检
        var pts = new[] { (640, 360), (200, 200) };
        var globe = client.DetectGlobe(pts);
        Console.WriteLine($"[globe  ] load={globe.Load} homo_size={Fmt(globe.HomoSize)}");
        if (globe.GlobeToScreen is not null && globe.GlobeToScreen.Count == pts.Length)
        {
            double worst = 0;
            for (int i = 0; i < pts.Length; i++)
                worst = Math.Max(worst,
                    Math.Abs(pts[i].Item1 - globe.GlobeToScreen[i][0]) +
                    Math.Abs(pts[i].Item2 - globe.GlobeToScreen[i][1]));
            Console.WriteLine($"[往返   ] screen->globe->screen 最大误差 {worst:E2}" +
                              (worst < 1e-6 ? "（OK，同一变换的逆）" : "（异常：应当≈0）"));
            if (!(worst < 1e-6)) problems++;
        }
        else
        {
            Console.WriteLine($"[往返   ] 无往返数据：{globe.RoundtripError ?? globe.Load}");
            problems++;
        }

        // 3) 战役地图：未检测到属于正常结果
        var map = client.DetectMap();
        Console.WriteLine($"[map    ] backend={map.Backend} detected={map.Detected} " +
                          $"grids={map.GridCount?.ToString() ?? "-"} " +
                          $"reason={(map.Reason ?? "-")}");
        if (!map.Detected && string.IsNullOrEmpty(map.Reason))
        {
            Console.WriteLine("[错误   ] 未检测到却没给出原因（负样本必须带原因）");
            problems++;
        }

        Console.WriteLine(problems == 0
            ? (map.Detected
                ? $"S2 产品路径验收通过（素材链 + 单应性往返 + 真机地图正样本 grids={map.GridCount}）"
                : "S2 产品路径验收通过（素材链 + 单应性往返 + 非地图负样本语义）")
            : $"S2 验收有 {problems} 处问题");
        return problems == 0 ? 0 : 1;
    }

    private static string Fmt<T>(List<T>? v)
        => v is null ? "-" : string.Join("x", v);
}
