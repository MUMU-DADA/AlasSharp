using Alas.Core;

namespace Alas.DataTool;

/// <summary>
/// S0 工具：读取上游数据契约并用 C# 独立复现校验。
///
/// 之所以用 C# 重写一遍校验（而不是信任 Python 侧的结论），是因为 S0 的验收标准是
/// 「C# 能消费这些数据」，而不是「Python 说数据没问题」。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string dataDir = Environment.GetEnvironmentVariable("ALAS_DATA")
                         ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                             "..", "..", "..", "..", "..", "data"));
        string repoDir = Environment.GetEnvironmentVariable("ALAS_REPO")
                         ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                             "..", "..", "..", "..", "..", "..",
                             "my fork project", "AzurLaneAutoScript"));

        string command = args.Length > 0 ? args[0] : "verify";
        string? fixture = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--data") dataDir = args[i + 1];
            if (args[i] == "--repo") repoDir = args[i + 1];
            if (args[i] == "--fixture") fixture = args[i + 1];
        }
        string? target = args.Length > 1 && !args[1].StartsWith("--") && command != "verify"
            ? args[1] : null;

        try
        {
            if (command == "imaging")
            {
                fixture ??= Path.Combine(dataDir, "fixtures", "imaging.json");
                return ImagingCheck.Run(fixture, repoDir);
            }
            if (command == "matching")
            {
                fixture ??= Path.Combine(dataDir, "fixtures", "matching.json");
                return MatchingCheck.Run(fixture, repoDir);
            }
            if (command == "vision")
            {
                fixture ??= Path.Combine(dataDir, "fixtures", "imaging.json");
                int limit = 200;
                string mode = Environment.GetEnvironmentVariable("ALAS_VISION_MODE") ?? "worker";
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--limit" && int.TryParse(args[i + 1], out int n)) limit = n;
                    if (args[i] == "--mode") mode = args[i + 1];
                }
                // bin/Release/net8.0 -> csharp/tools
                string toolsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", "..", "tools"));
                return VisionCheck.Run(fixture, repoDir, toolsDir, limit, mode);
            }

            var catalog = UpstreamData.Catalog.Open(dataDir);
            return command switch
            {
                "verify" => Verify(catalog, repoDir),
                "list" => List(catalog, target),
                "show" => Show(catalog, target),
                _ => Fail($"未知命令: {command}"
                          + "（可用: verify / list / show / imaging / matching / vision）"),
            };
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    // ------------------------------------------------------------------ verify
    private static int Verify(UpstreamData.Catalog catalog, string repoDir)
    {
        var problems = new List<string>();

        Console.WriteLine($"数据目录      : {catalog.DataDirectory}");
        Console.WriteLine($"上游提交      : {catalog.UpstreamCommit ?? "(未知)"}"
                          + (catalog.UpstreamDirty ? "  ⚠ 导出时工作区不干净" : ""));
        Console.WriteLine($"素材绑定      : {catalog.Assets.Assets.Count}");
        Console.WriteLine($"关卡          : {catalog.Campaign.Chapters.Count}");
        Console.WriteLine();

        // ---- 素材：文件存在性 + 字段完整性
        // 注意：上游 Template 只有 file（无 area/color/button），area 只对 Button/Mask 是必需的。
        int missingFiles = 0, noServers = 0, noArea = 0, noFile = 0;
        var missingSample = new List<string>();
        foreach (var (id, a) in catalog.Assets.Assets)
        {
            bool needsArea = a.Kind is "Button" or "Mask";
            if (a.Servers.Count == 0) noServers++;
            if (needsArea && (a.Area is null || a.Area.Count == 0)) noArea++;
            if (a.File is null || a.File.Count == 0)
            {
                noFile++;
                continue;
            }
            foreach (var (server, rel) in a.File)
            {
                string p = Path.Combine(repoDir, rel.Replace("./", "")
                    .Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p))
                {
                    missingFiles++;
                    if (missingSample.Count < 5) missingSample.Add($"{id} [{server}] -> {rel}");
                }
            }
        }
        Console.WriteLine($"[素材] 缺图 {missingFiles}，无 file {noFile}，"
                          + $"无服务器变体 {noServers}，Button/Mask 缺 area {noArea}");
        foreach (var s in missingSample) Console.WriteLine($"       {s}");
        if (missingFiles > 0) problems.Add($"{missingFiles} 个素材引用的图片不存在");
        if (noFile > 0) problems.Add($"{noFile} 个素材没有 file（无法加载图片）");
        if (noServers > 0) problems.Add($"{noServers} 个素材没有任何服务器变体");
        if (noArea > 0) problems.Add($"{noArea} 个 Button/Mask 没有 area（无法定位）");

        // ---- 关卡：网格自洽 + 计划不变量 + 分级
        var tiers = new Dictionary<string, int>();
        int gridBad = 0, planBad = 0, sirenCount = 0, bossKnown = 0;
        int chaptersWithOverrides = 0, superDelegateCount = 0;
        var overrideMethods = new SortedSet<string>(StringComparer.Ordinal);
        var badSample = new List<string>();
        foreach (var entry in catalog.Campaign.Chapters)
        {
            tiers[entry.Tier] = tiers.GetValueOrDefault(entry.Tier) + 1;
            if (entry.HasSiren) sirenCount++;
            if (entry.BossBattle is not null) bossKnown++;
            if (entry.NativeOverrides.Count > 0) chaptersWithOverrides++;
            superDelegateCount += entry.SuperDelegates.Count;
            foreach (var m in entry.NativeOverrides) overrideMethods.Add(m);

            var ir = catalog.LoadCampaign(entry);

            var shape = CampaignIr.ParseShape(ir.Shape);
            string? grid = ir.MapData;
            if (shape is not null && grid is not null)
            {
                var rows = grid.Split('\n')
                    .Select(r => r.Trim())
                    .Where(r => r.Length > 0)
                    .ToList();
                if (rows.Count != shape.Value.Rows)
                {
                    gridBad++;
                    if (badSample.Count < 5)
                        badSample.Add($"{entry.Source}: 行数 {rows.Count} != shape {shape.Value.Rows}");
                }
                else
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        int cells = rows[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                        if (cells != shape.Value.Columns)
                        {
                            gridBad++;
                            if (badSample.Count < 5)
                                badSample.Add($"{entry.Source}: 第 {i} 行 {cells} 列 != shape {shape.Value.Columns}");
                            break;
                        }
                    }
                }
            }

            foreach (var b in ir.Campaign.Battles)
            {
                if (!b.PlanComplete && b.Steps.Count > 0)
                {
                    planBad++;
                    if (badSample.Count < 5)
                        badSample.Add($"{entry.Source}.{b.Method}: plan_complete=false 却有 steps");
                }
                if (b.PlanComplete && b.Steps.Count == 0)
                {
                    planBad++;
                    if (badSample.Count < 5)
                        badSample.Add($"{entry.Source}.{b.Method}: plan_complete=true 却无 steps");
                }
            }
        }
        Console.WriteLine($"[关卡] 网格不自洽 {gridBad}，计划不变量违例 {planBad}，"
                          + $"有塞壬 {sirenCount}，boss 回合已知 {bossKnown}");
        foreach (var s in badSample) Console.WriteLine($"       {s}");
        if (gridBad > 0) problems.Add($"{gridBad} 个关卡网格与 shape 不自洽");
        if (planBad > 0) problems.Add($"{planBad} 处计划不变量违例");

        // ---- 分级（决定 S3 的工作量构成）
        Console.WriteLine();
        Console.WriteLine("[分级] A = JSON 规则表即可；B = 计划完整但用词表外算子；C = 需插件/原生实现");
        foreach (var t in tiers.OrderBy(kv => kv.Key))
            Console.WriteLine($"       tier {t.Key}: {t.Value,5}  ({t.Value * 100.0 / catalog.Campaign.Chapters.Count:F1}%)");

        Console.WriteLine();
        Console.WriteLine("[引擎钩子] 非 battle_* 的覆写 —— S3 必须逐个在 C# 里实现");
        Console.WriteLine($"       涉及关卡 {chaptersWithOverrides}，"
                          + $"去重后 {overrideMethods.Count} 个钩子方法，"
                          + $"另有 {superDelegateCount} 处纯 super 委托（无需新增逻辑）");
        foreach (var m in overrideMethods.Take(15)) Console.WriteLine($"       {m}");
        if (overrideMethods.Count > 15) Console.WriteLine($"       ...（其余 {overrideMethods.Count - 15} 个）");

        Console.WriteLine();
        if (problems.Count == 0)
        {
            Console.WriteLine("结果: OK");
            return 0;
        }
        Console.WriteLine("结果: FAIL");
        foreach (var p in problems) Console.WriteLine($"  - {p}");
        return 1;
    }

    // ------------------------------------------------------------------ list
    private static int List(UpstreamData.Catalog catalog, string? tier)
    {
        var rows = catalog.Campaign.Chapters
            .Where(c => tier is null || c.Tier == tier)
            .OrderBy(c => c.Source, StringComparer.Ordinal);
        foreach (var c in rows)
        {
            Console.WriteLine($"{c.Tier}  {c.Name,-24} {c.Source}"
                              + (c.NeedsReview ? "   [需复核]" : ""));
        }
        Console.WriteLine($"共 {rows.Count()} 个（tier 过滤: {tier ?? "全部"}）");
        return 0;
    }

    // ------------------------------------------------------------------ show
    private static int Show(UpstreamData.Catalog catalog, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Fail("用法: alashub show <关卡名 | 源文件路径片段>");

        var entry = catalog.Campaign.Chapters.FirstOrDefault(c =>
            string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? catalog.Campaign.Chapters.FirstOrDefault(c =>
                c.Source.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return Fail($"找不到关卡: {key}");

        var ir = catalog.LoadCampaign(entry);
        Console.WriteLine($"源文件   : {entry.Source}");
        Console.WriteLine($"关卡名   : {ir.Name}（来源 {ir.NameSource}）");
        Console.WriteLine($"分级     : {entry.Tier}   plan_complete={entry.PlanComplete}"
                          + $"   template_only={entry.TemplateOnly}");
        Console.WriteLine($"boss 回合: {entry.BossBattle}   有塞壬: {entry.HasSiren}");
        Console.WriteLine($"shape    : {ir.Shape}");
        Console.WriteLine($"map 字段 : {string.Join(", ", ir.Map.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
        Console.WriteLine($"config   : {string.Join(", ", ir.Config.Keys.OrderBy(k => k, StringComparer.Ordinal))}");

        string? grid = ir.MapData;
        if (grid is not null)
        {
            Console.WriteLine("网格     :");
            foreach (var row in grid.Split('\n').Where(r => r.Trim().Length > 0))
                Console.WriteLine($"  {row.Trim()}");
        }

        Console.WriteLine("战斗计划 :");
        foreach (var b in ir.Campaign.Battles)
        {
            if (!b.PlanComplete)
            {
                Console.WriteLine($"  {b.Method}: 计划不完整（未识别语句: "
                                  + $"{string.Join(",", b.Unparsed)}），需插件或原生实现");
                continue;
            }
            Console.WriteLine($"  {b.Method}:");
            foreach (var s in b.Steps)
                Console.WriteLine($"    {s.Kind,-20} {s.Op}"
                                  + (s.Target is null ? "" : $" -> {s.Target}"));
        }
        if (ir.Unresolved.Count > 0)
            Console.WriteLine("未解析   : " + string.Join(" | ", ir.Unresolved));
        return 0;
    }
}
