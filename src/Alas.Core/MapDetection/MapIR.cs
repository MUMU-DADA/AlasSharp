using System.Text.Json.Nodes;

namespace Alas.MapDetection;

/// <summary>
/// 关卡地图模型（S2 的数据半边）。
///
/// 上游把地图写成几个字符串字段（`shape` / `map_data` / `weight_data` / `camera_data` /
/// `spawn_data`），由 `CampaignMap` 的属性解析。识别出来的网格最终要跟这份模型对齐，
/// 所以解析必须与上游**逐位一致**：
///
/// - `shape`：上游走 `node2location()`，其末行是
///   `return ord(node[0]) % 32 - 1, int(node[1:]) - 1` —— 即 x=字母序号-1（A→0）、
///   y=数字-1；而网格数是 `(x+1) × (y+1)`（`CampaignMap.shape` setter 里
///   `range(self._shape[0] + 1)` 写得很清楚）。
/// - `map_data` / `weight_data`：按行按空白切分。
/// - `camera_data`：形如 `D3`，同样走 `node2location`。
///
/// 本类只管**数据**，不算任何图（架构铁律 2）。
/// </summary>
public sealed class MapIR
{
    /// <summary>上游 token 词表（全量 1437 个章节里出现过的 12 种）。</summary>
    public static readonly IReadOnlySet<string> KnownTokens = new HashSet<string>
    {
        "--", "++", "ME", "Me", "MS", "SP", "MB", "__", "MM", "MA", "SI", "-",
    };

    public string Name { get; init; } = "";
    public string? ShapeRaw { get; init; }
    /// <summary>node2location 的结果；(0,0) 表示解析不出（shape 为 None）。</summary>
    public int ShapeX { get; init; }
    public int ShapeY { get; init; }
    public bool HasShape => ShapeRaw is not null;
    /// <summary>网格数（上游是 shape+1）。</summary>
    public int Width => ShapeX + 1;
    public int Height => ShapeY + 1;

    public List<string> CameraData { get; init; } = new();
    public List<string> CameraDataSpawnPoint { get; init; } = new();
    public List<List<string>> MapData { get; init; } = new();
    public List<List<int>> WeightData { get; init; } = new();
    public List<JsonObject> SpawnData { get; init; } = new();
    public int BattleCount { get; init; }
    public List<string> Errors { get; init; } = new();

    /// <summary>上游 `node2location(node)`：`ord(node[0]) % 32 - 1, int(node[1:]) - 1`。</summary>
    public static (int X, int Y) NodeToLocation(string node)
    {
        if (string.IsNullOrEmpty(node)) throw new FormatException("空节点名");
        char c = char.ToUpperInvariant(node[0]);
        if (c < 'A' || c > 'Z') throw new FormatException($"节点首字符不是字母: {node}");
        int x = c % 32 - 1;
        string rest = node[1..].Trim();
        if (!int.TryParse(rest, out int n)) throw new FormatException($"节点数字部分无效: {node}");
        return (x, n - 1);
    }

    private static List<List<string>> Rows(string? text)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text)) return rows;
        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            rows.Add(t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList());
        }
        return rows;
    }

    /// <summary>从导出的 IR JSON（一个章节文件）解析并校验。</summary>
    public static MapIR Parse(JsonNode root, string name = "")
    {
        var errors = new List<string>();
        var map = root["map"] as JsonObject ?? new JsonObject();
        string? shapeRaw = map["shape"]?.GetValue<string>();
        int sx = 0, sy = 0;
        if (shapeRaw is not null)
        {
            try
            {
                (sx, sy) = NodeToLocation(shapeRaw);
            }
            catch (Exception e)
            {
                errors.Add($"shape 解析失败: {e.Message}");
            }
        }

        var mapData = Rows(map["map_data"]?.GetValue<string>());
        var weightRows = Rows(map["weight_data"]?.GetValue<string>());
        var weights = new List<List<int>>();
        foreach (var row in weightRows)
        {
            var nums = new List<int>();
            foreach (string cell in row)
            {
                if (int.TryParse(cell, out int v)) nums.Add(v);
                else errors.Add($"weight 不是整数: {cell}");
            }
            weights.Add(nums);
        }

        var camera = new List<string>();
        foreach (var node in map["camera_data"] as JsonArray ?? new JsonArray())
        {
            string s = node?.GetValue<string>() ?? "";
            camera.Add(s);
        }
        var cameraSpawn = new List<string>();
        foreach (var node in map["camera_data_spawn_point"] as JsonArray ?? new JsonArray())
            cameraSpawn.Add(node?.GetValue<string>() ?? "");

        var spawnData = new List<JsonObject>();
        foreach (var node in map["spawn_data"] as JsonArray ?? new JsonArray())
            if (node is JsonObject o) spawnData.Add(o);

        // ---- 校验（这些是"数据自洽性"，与识别无关，但错了后面全错）
        if (shapeRaw is null)
        {
            // 103 个章节没有 shape：上游会在运行时算，记录但不报错
        }
        else
        {
            if (mapData.Count != sy + 1)
                errors.Add($"map_data 行数 {mapData.Count} != shape 推出的 {sy + 1}");
            for (int i = 0; i < mapData.Count; i++)
                if (mapData[i].Count != sx + 1)
                    errors.Add($"map_data 第 {i} 行 {mapData[i].Count} 列 != {sx + 1}");
            if (weights.Count != 0 && weights.Count != sy + 1)
                errors.Add($"weight_data 行数 {weights.Count} != {sy + 1}");
        }
        foreach (var row in mapData)
            foreach (string t in row)
                if (!KnownTokens.Contains(t))
                    errors.Add($"未知 token: {t}");
        foreach (string c in camera.Concat(cameraSpawn))
        {
            try
            {
                var (x, y) = NodeToLocation(c);
                if (shapeRaw is not null && (x > sx || y > sy))
                    errors.Add($"camera {c} 越界（shape={shapeRaw} → {sx + 1}x{sy + 1}）");
            }
            catch (Exception e)
            {
                errors.Add($"camera 解析失败 {c}: {e.Message}");
            }
        }
        int battles = 0;
        foreach (var o in spawnData)
        {
            int b = o["battle"]?.GetValue<int>() ?? -1;
            battles = Math.Max(battles, b + 1);
        }

        return new MapIR
        {
            Name = name,
            ShapeRaw = shapeRaw,
            ShapeX = sx,
            ShapeY = sy,
            CameraData = camera,
            CameraDataSpawnPoint = cameraSpawn,
            MapData = mapData,
            WeightData = weights,
            SpawnData = spawnData,
            BattleCount = battles,
            Errors = errors,
        };
    }

    /// <summary>上游 `CampaignMap.camera_sight` 的默认值（map_base.py:36）。</summary>
    public static readonly int[] DefaultCameraSight = { -3, -1, 3, 2 };

    /// <summary>
    /// 有效相机点列表。
    ///
    /// **关键**：章节没显式写 `camera_data` 时，上游会在 `shape` 的 setter 里
    /// **自动生成**（map_base.py:77）：
    /// <code>
    /// self.camera_data = [location2node(loca)
    ///                     for loca in camera_2d((0, 0, *self._shape), sight=self.camera_sight)]
    /// </code>
    /// 而导出的 IR 只有字面量，所以这里必须补上这条规则 —— 否则所有"没写字面量"的章节
    /// 都会缺相机点（跨语言对照实测抓到过：`event_20200521_en/d3` IR 是空、上游是 6 个）。
    /// </summary>
    public List<string> EffectiveCameraData
        => CameraData.Count > 0 || !HasShape
            ? CameraData
            : Camera2D(0, 0, ShapeX, ShapeY, DefaultCameraSight)
                .Select(p => LocationToNode(p.X, p.Y)).ToList();

    /// <summary>上游 `location2node`：0 基坐标 → Excel 风格节点名（(0,0)→A1、(3,1)→D2）。</summary>
    public static string LocationToNode(int x, int y)
        => $"{(char)('A' + x)}{y + 1}";

    /// <summary>
    /// 上游 `camera_1d(shape, sight)`：
    /// <code>
    /// start, step = abs(sight[0]), sight[1] - sight[0] + 1
    /// if shape &lt;= start: out = shape // 2
    /// else:
    ///     out = list(range(start, 26, step)); out.append(shape - sight[1])
    ///     out = [x for x in set(out) if x &lt;= shape - sight[1]]
    /// </code>
    /// 上游那支返回标量的分支只在极窄的边界形状上出现；这里同样返回单元素列表。
    /// 去重后的顺序用升序（CPython 对小整数集合的迭代顺序就是按值升序）。
    /// </summary>
    public static List<int> Camera1D(int shape, int sight0, int sight1)
    {
        int start = Math.Abs(sight0);
        int step = sight1 - sight0 + 1;
        if (shape <= start) return new List<int> { shape / 2 };
        var raw = new List<int>();
        for (int x = start; x < 26; x += step) raw.Add(x);
        raw.Add(shape - sight1);
        return raw.Where(x => x <= shape - sight1).Distinct().OrderBy(x => x).ToList();
    }

    /// <summary>上游 `camera_2d(area, sight)`：`meshgrid(x, y).T.reshape(-1, 2) + area[:2]`。</summary>
    public static List<(int X, int Y)> Camera2D(int x0, int y0, int x1, int y1, int[] sight)
    {
        var xs = Camera1D(x1 - x0, sight[0], sight[2]);
        var ys = Camera1D(y1 - y0, sight[1], sight[3]);
        var outp = new List<(int, int)>();
        // meshgrid 默认 indexing='xy' 后转置展平 → 外层 x、内层 y
        foreach (int x in xs)
            foreach (int y in ys)
                outp.Add((x + x0, y + y0));
        return outp;
    }

    /// <summary>
    /// 全网格指纹：逐格按 token 解码成一个字符（见 <see cref="GridFlags.Fingerprint"/>），
    /// 按行拼起来。跨语言对照用 —— 它把"整张地图的语义"压成一行，
    /// 任何一格解码错了都会立刻不一致。
    /// </summary>
    public string GridFingerprint()
        => string.Join("\n", MapData.Select(
            row => new string(row.Select(t => GridFlags.Decode(t).Fingerprint).ToArray())));

    /// <summary>解码后的网格（引擎做寻路时用）。</summary>
    public GridFlags[][] DecodeGrid()
        => MapData.Select(row => row.Select(GridFlags.Decode).ToArray()).ToArray();

    /// <summary>规范化摘要：跨语言对照用（C# 与上游 Python 必须给出同一串）。</summary>
    public string Digest()
    {
        int tokens = MapData.Sum(r => r.Count);
        int weightSum = WeightData.Sum(r => r.Sum());
        return string.Join("|",
            ShapeRaw ?? "none", $"{Width}x{Height}",
            $"rows={MapData.Count}", $"tokens={tokens}", $"weight={weightSum}",
            $"camera={string.Join(",", CameraData)}",
            $"spawnpts={string.Join(",", CameraDataSpawnPoint)}",
            $"spawn={SpawnData.Count}", $"battles={BattleCount}");
    }

    /// <summary>
    /// **可与上游活对象对照**的摘要：用解析后的形状元组而不是原始字符串
    /// （上游的 `shape` setter 会把字符串吃掉，活对象上只剩元组）。
    /// 两边字段必须完全同名同序，否则对照没有意义。
    /// </summary>
    public string DigestComparable()
    {
        int tokens = MapData.Sum(r => r.Count);
        int weightSum = WeightData.Sum(r => r.Sum());
        var camera = EffectiveCameraData;
        return string.Join("|",
            $"{ShapeX},{ShapeY}", $"{Width}x{Height}",
            $"rows={MapData.Count}", $"tokens={tokens}", $"weight={weightSum}",
            $"camera={string.Join(",", camera)}",
            $"spawnpts={string.Join(",", CameraDataSpawnPoint)}",
            $"spawn={SpawnData.Count}", $"battles={BattleCount}");
    }
}
