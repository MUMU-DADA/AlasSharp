using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.MapDetection;

namespace Alas.DataTool;

/// <summary>
/// 关卡地图模型（S2 数据半边）的全量校验：`alashub map-ir`。
///
/// 做两件事：
///   1. 把 1437 个章节 IR 全部解析一遍，检查**数据自洽性**（shape 推出的网格数 vs
///      map_data/weight_data 的行列数、token 是否在词表内、camera 是否越界）。
///      这些错了后面全错，而且不会以"识别不准"的形式暴露，只会让引擎行为诡异。
///   2. 导出每个章节的规范化摘要（`data/map_ir_digests.json`），供 Python 侧与
///      **上游活对象**逐字段对照（`tools/diagnostics/verify_map_ir.py`）。
/// </summary>
internal static class MapIRCheck
{
    public static int Run(string? file, string? dir, string dataDir)
    {
        var files = new List<string>();
        if (!string.IsNullOrEmpty(file)) files.Add(file);
        if (!string.IsNullOrEmpty(dir))
            files.AddRange(Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories));
        if (files.Count == 0)
        {
            Console.WriteLine("用法: map-ir [--file <json>] [--dir <data/campaign>]");
            return 2;
        }

        int parsed = 0, shapeNull = 0, withErrors = 0, totalErrors = 0;
        var tokenHist = new Dictionary<string, int>();
        var errSamples = new List<string>();
        var digests = new SortedDictionary<string, string>();

        foreach (string path in files)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                withErrors++; totalErrors++;
                if (errSamples.Count < 5) errSamples.Add($"{Path.GetFileName(path)}: JSON 解析失败 {e.Message}");
                continue;
            }
            string name = Path.GetFileNameWithoutExtension(path);
            var ir = MapIR.Parse(root!, name);
            parsed++;
            if (!ir.HasShape) shapeNull++;
            foreach (var row in ir.MapData)
                foreach (string t in row)
                    tokenHist[t] = tokenHist.GetValueOrDefault(t) + 1;
            if (ir.Errors.Count > 0)
            {
                withErrors++;
                totalErrors += ir.Errors.Count;
                if (errSamples.Count < 12) errSamples.Add($"{name}: {ir.Errors[0]}");
            }
            digests[Path.GetRelativePath(dir ?? Path.GetDirectoryName(path)!, path)
                .Replace('\\', '/')] = ir.DigestComparable();
        }

        Console.WriteLine($"[map-ir ] 解析 {parsed}/{files.Count}，shape 缺失 {shapeNull}，" +
                          $"有错 {withErrors}（{totalErrors} 条）");
        Console.WriteLine("[tokens] " + string.Join(" ",
            tokenHist.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (string s in errSamples) Console.WriteLine("[err   ] " + s);

        string outPath = Path.Combine(dataDir, "map_ir_digests.json");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(outPath,
            JsonSerializer.Serialize(digests, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[digest] {digests.Count} 条 → {outPath}");
        return withErrors == 0 ? 0 : 1;
    }
}
