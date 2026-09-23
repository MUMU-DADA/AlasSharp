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
                          string? adbPath, string? serial, string? chapter = null,
                          string? dataDir = null, string mode = "main")
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
        // 识图和 IR 校验必须使用同一章节；章节 Config 包含上游生成的识别规则。
        string? module = string.IsNullOrEmpty(chapter) ? null
            : "campaign." + Path.ChangeExtension(chapter, null).Replace('/', '.').Replace('\\', '.');
        var map = client.DetectMap(mode, module);
        Console.WriteLine($"[mode   ] {mode}");
        Console.WriteLine($"[map    ] backend={map.Backend} detected={map.Detected} " +
                          $"grids={map.GridCount?.ToString() ?? "-"} " +
                          $"reason={(map.Reason ?? "-")} load={map.Load} predict={map.Predict}");
        string? mapError = map.ExecutionError;
        if (mapError is not null)
        {
            Console.WriteLine($"[错误   ] {mapError}");
            problems++;
        }

        Console.WriteLine(problems == 0
            ? (map.Detected
                ? $"S2 产品路径验收通过（素材链 + 单应性往返 + 真机地图正样本 grids={map.GridCount}）"
                : "S2 产品路径验收通过（素材链 + 单应性往返 + 非地图负样本语义）")
            : $"S2 验收有 {problems} 处问题");
        // 识别结果 vs 关卡 IR 的交叉校验（C# 侧独立完成）
        if (!string.IsNullOrEmpty(chapter) && mapError is null && map.Detected
            && !string.IsNullOrEmpty(dataDir))
            problems += CrossCheck(map, chapter, dataDir!);
        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// 识别结果 vs 关卡 IR 的交叉校验（**C# 侧独立完成**，不依赖 Python 诊断脚本）：
    /// 用移植过来的 <see cref="MapIR"/> / <see cref="GridFlags"/> 解 IR，再与识别结果比。
    ///
    /// 三条判据（由弱到强）：
    ///   1. 检出的 shape 必须与 IR 声明**严格一致**（F4→(5,3)、I6→(8,5)）；
    ///   2. 缺格允许存在，但要列出坐标（左侧舰队栏 / 顶部信息条遮住的格子本就检不到）；
    ///   3. **船不可能落在陆地格上** —— 识别坐标差一格就会被违反，这条最强。
    /// </summary>
    public static int CrossCheck(MapDetectResult map, string chapterPath, string dataDir)
    {
        string full = Path.Combine(dataDir, "campaign", chapterPath);
        if (!File.Exists(full))
        {
            Console.WriteLine($"[ir     ] 找不到关卡 IR：{full}");
            return 1;
        }
        var ir = MapIR.Parse(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(full))!,
            Path.GetFileNameWithoutExtension(chapterPath));
        var (sx, sy) = ir.EffectiveShape;
        var detShape = map.Shape ?? new List<int>();
        bool shapeOk = detShape.Count == 2 && detShape[0] == sx && detShape[1] == sy;

        // 缺格
        var keys = new HashSet<string>((map.GridKeys ?? new List<List<int>>())
            .Where(k => k.Count == 2).Select(k => $"{k[0]},{k[1]}"));
        var missing = new List<string>();
        for (int y = 0; y <= sy; y++)
            for (int x = 0; x <= sx; x++)
                if (!keys.Contains($"{x},{y}")) missing.Add($"{x},{y}");

        // 船 vs 陆地。**判据分两档**（实测校准）：
        //   敌人 / BOSS / 塞壬 —— 硬判据：它们与地形强相关，落陆地即说明识别坐标有问题；
        //   己方舰队（is_fleet / is_current_fleet / is_submarine）—— 只警告：
        //     这些是**派生指派**（预测器判定哪支检出的舰队是当前舰队），实测困难 1-4 上
        //     is_current_fleet 指到了陆地格，把唯一候选偏移也否掉了；拿它当硬判据会误报。
        var grid = ir.DecodeGrid();
        var ships = new List<string>();
        var enemyOnLand = new List<string>();
        var friendlyOnLand = new List<string>();
        foreach (var kv in map.GridFlags ?? new Dictionary<string, List<string>>())
        {
            bool isEnemy = kv.Value.Any(n => n is "is_enemy" or "is_boss" or "is_siren");
            bool isFriendly = kv.Value.Any(n => n is "is_fleet" or "is_current_fleet" or "is_submarine");
            if (!isEnemy && !isFriendly) continue;
            ships.Add(kv.Key);
            var parts = kv.Key.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int x)
                && int.TryParse(parts[1], out int y)
                && y >= 0 && y < grid.Length && x >= 0 && x < grid[y].Length
                && grid[y][x].IsLand)
                (isEnemy ? enemyOnLand : friendlyOnLand).Add(kv.Key);
        }

        Console.WriteLine($"[ir     ] {Path.GetFileName(chapterPath)} shape={ir.ShapeRaw} " +
                          $"→ 期望 {sx + 1}x{sy + 1}，网格 {ir.EffectiveWidth * ir.EffectiveHeight} 格");
        Console.WriteLine($"[校验 1 ] shape 一致：{shapeOk}（检出 [{(detShape.Count == 2 ? $"{detShape[0]},{detShape[1]}" : "-")}]）");
        Console.WriteLine($"[校验 2 ] 缺格 {missing.Count}：" +
                          (missing.Count == 0 ? "无" : string.Join(" ", missing.Take(12))));
        Console.WriteLine($"[校验 3 ] 船格 {ships.Count} 个（{string.Join(" ", ships.Take(8))}）");
        Console.WriteLine($"[校验 3a] 敌人/BOSS/塞壬落在陆地上 {enemyOnLand.Count}：" +
                          (enemyOnLand.Count == 0 ? "无 ✅" : string.Join(" ", enemyOnLand)) + "（硬判据）");
        Console.WriteLine($"[校验 3b] 己方舰队落在陆地上 {friendlyOnLand.Count}：" +
                          (friendlyOnLand.Count == 0 ? "无" : string.Join(" ", friendlyOnLand)) +
                          "（仅警告：is_current_fleet 是派生指派，可能指错格）");
        int problems = (shapeOk ? 0 : 1) + (enemyOnLand.Count == 0 ? 0 : 1);
        Console.WriteLine(problems == 0
            ? "S2 交叉校验通过（shape 严格一致 + 敌人未落陆地）"
            : $"S2 交叉校验发现 {problems} 处不一致");
        return problems == 0 ? 0 : 1;
    }

    private static string Fmt<T>(List<T>? v)
        => v is null ? "-" : string.Join("x", v);
}
