using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Alas.UI.Statistics;

/// <summary>AzurPilot frontend/src/api/types.ts 的报告形状；本层只做展示，不生成业务统计。</summary>
public sealed record StatisticsReport(string Instance, string Category, string Month,
    IReadOnlyList<StatisticsMetric> Metrics, IReadOnlyList<StatisticsSeries> Series,
    IReadOnlyList<StatisticsTable> Tables, IReadOnlyList<string> Notes)
{
    public static StatisticsReport Parse(JsonObject json)
    {
        static JsonArray Array(JsonObject value, string key) => value[key] as JsonArray
            ?? throw new FormatException($"统计报告缺少数组 {key}");
        static string Text(JsonObject value, string key) => value[key]?.GetValue<string>()
            ?? throw new FormatException($"统计报告缺少文本 {key}");
        var metrics = Array(json, "metrics").Select(item =>
        {
            var value = item?.AsObject() ?? throw new FormatException("指标不能为空");
            return new StatisticsMetric(Text(value, "label"), Number(value["value"]), Text(value, "unit"));
        }).ToArray();
        var series = Array(json, "series").Select(item =>
        {
            var value = item?.AsObject() ?? throw new FormatException("序列不能为空");
            return new StatisticsSeries(Text(value, "key"), Text(value, "label"), Array(value, "points").Select(p =>
            {
                var point = p?.AsObject() ?? throw new FormatException("数据点不能为空");
                return new StatisticsPoint(Text(point, "time"), Number(point["value"]) ?? throw new FormatException("数据点缺少数值"), point["source"]?.GetValue<string>() ?? "");
            }).ToArray());
        }).ToArray();
        var tables = Array(json, "tables").Select(item =>
        {
            var value = item?.AsObject() ?? throw new FormatException("明细表不能为空");
            var columns = Array(value, "columns").Select(c => c!.GetValue<string>()).ToArray();
            var rows = Array(value, "rows").Select(r => r!.AsArray().Select(Scalar).ToArray()).ToArray();
            if (rows.Any(row => row.Length != columns.Length)) throw new FormatException("明细列数与行数据不一致");
            var sort = value["defaultSort"] as JsonObject;
            var index = sort?["index"]?.GetValue<int>();
            if (index is < 0 || index >= columns.Length) throw new FormatException("明细默认排序列无效");
            return new StatisticsTable(Text(value, "title"), columns, rows, value["note"]?.GetValue<string>() ?? "",
                index, sort?["descending"]?.GetValue<bool>() ?? false);
        }).ToArray();
        return new(Text(json, "instance"), Text(json, "category"), Text(json, "month"), metrics, series, tables,
            Array(json, "notes").Select(n => n!.GetValue<string>()).ToArray());
    }

    private static object? Scalar(JsonNode? node) => node is null ? null : node.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
        System.Text.Json.JsonValueKind.Number => Number(node),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => throw new FormatException("统计表只支持上游 Scalar：文本、数字、布尔或空值")
    };
    private static double? Number(JsonNode? node)
    {
        if (node is null || node.GetValueKind() == System.Text.Json.JsonValueKind.Null) return null;
        if (node.GetValueKind() != System.Text.Json.JsonValueKind.Number) throw new FormatException("统计数值必须为 JSON number 或 null");
        return System.Text.Json.JsonDocument.Parse(node.ToJsonString()).RootElement.GetDouble();
    }
}

public sealed record StatisticsMetric(string Label, double? Value, string Unit)
{
    public string DisplayValue => Value?.ToString("N2", CultureInfo.CurrentCulture).TrimEnd('0').TrimEnd(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator.ToCharArray()) ?? "—";
}
public sealed record StatisticsPoint(string Time, double Value, string Source = "");
public sealed record StatisticsSeries(string Key, string Label, IReadOnlyList<StatisticsPoint> Points);
public sealed record StatisticsTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows,
    string Note = "", int? SortIndex = null, bool Descending = false);
public sealed record StatisticsBucket(string Time, double Open, double Close, double Low, double High);
public sealed record StatisticsExport(string FileName, string MediaType, byte[] Content);

public static class StatisticsData
{
    /// <summary>沿 statisticsData.aggregatePoints 保留输入顺序与 OHLC；无分桶时每条记录独立。</summary>
    public static IReadOnlyList<StatisticsBucket> Aggregate(IReadOnlyList<StatisticsPoint> points, int minutes)
    {
        if (minutes is not (0 or 5 or 60 or 1440)) throw new ArgumentOutOfRangeException(nameof(minutes));
        var buckets = new List<StatisticsBucket>();
        var indices = new Dictionary<DateTime, int>();
        foreach (var point in points)
        {
            if (!double.IsFinite(point.Value) || !DateTime.TryParse(point.Time.Replace('T', ' '), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (minutes == 0) { buckets.Add(new(point.Time, point.Value, point.Value, point.Value, point.Value)); continue; }
            date = minutes == 1440 ? date.Date : new DateTime(date.Year, date.Month, date.Day, date.Hour, date.Minute / minutes * minutes, 0, date.Kind);
            if (indices.TryGetValue(date, out var index))
            {
                var previous = buckets[index];
                buckets[index] = previous with { Close = point.Value, Low = Math.Min(previous.Low, point.Value), High = Math.Max(previous.High, point.Value) };
            }
            else
            {
                indices[date] = buckets.Count;
                buckets.Add(new(date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), point.Value, point.Value, point.Value, point.Value));
            }
        }
        return buckets;
    }

    public static IReadOnlyList<StatisticsPoint> Filter(StatisticsSeries series, string from, string to) => series.Points.Where(p =>
        (from.Length == 0 || string.CompareOrdinal(p.Time.Replace(' ', 'T'), from.Replace(' ', 'T')) >= 0) &&
        (to.Length == 0 || string.CompareOrdinal(p.Time.Replace(' ', 'T'), to.Replace(' ', 'T') + (to.Length == 16 ? ":59.999" : "")) <= 0)).ToArray();

    public static StatisticsTable RawTable(IReadOnlyList<StatisticsSeries> series, string from, string to)
    {
        if (series.Count == 1)
            return new($"{series[0].Label}原始记录", ["时间", "数值", "来源"],
                Filter(series[0], from, to).Select(p => new object?[] { p.Time, p.Value, p.Source.Length == 0 ? "—" : p.Source }).ToArray(), SortIndex: 0, Descending: true);
        var map = new Dictionary<string, (Dictionary<string, double> Values, string Source)>(StringComparer.Ordinal);
        foreach (var item in series)
            foreach (var point in Filter(item, from, to))
            {
                if (!map.TryGetValue(point.Time, out var entry)) entry = (new(), "");
                entry.Values[item.Key] = point.Value;
                if (entry.Source.Length == 0) entry.Source = point.Source;
                map[point.Time] = entry;
            }
        return new("多指标原始记录", ["时间", .. series.Select(s => s.Label), "来源"], map.OrderByDescending(k => k.Key, StringComparer.Ordinal)
            .Select(pair => new object?[] { pair.Key }.Concat(series.Select(s => pair.Value.Values.TryGetValue(s.Key, out var v) ? (object?)v : "—"))
                .Append(pair.Value.Source.Length == 0 ? "—" : pair.Value.Source).ToArray()).ToArray(), SortIndex: 0, Descending: true);
    }

    public static StatisticsExport Csv(string name, IEnumerable<IEnumerable<object?>> rows)
    {
        static string Escape(object? value)
        {
            var text = value is bool flag ? flag.ToString().ToLowerInvariant() : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            // 与上游相同：仅文本防公式，数值负号保留；额外涵盖前导空白后的公式。
            if (value is string && (text.StartsWith('\t') || text.StartsWith('\r') || text.StartsWith('\n') || "=+-@".Contains(text.TrimStart().FirstOrDefault('\0')))) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        var body = "\uFEFF" + string.Join("\r\n", rows.Select(row => string.Join(',', row.Select(Escape))));
        return new(SafeFileName(name) + ".csv", "text/csv;charset=utf-8", Encoding.UTF8.GetBytes(body));
    }

    public static string SafeFileName(string name)
    {
        var safe = new string(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (safe.Length == 0) safe = "statistics";
        if (safe.Length > 120) safe = safe[..120];
        var stem = safe.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) safe = "_" + safe;
        return safe;
    }
}
