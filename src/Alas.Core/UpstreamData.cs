using System.Text.Json;

namespace Alas.Core;

/// <summary>
/// 上游数据契约的读取入口。
///
/// 数据由 <c>csharp/tools/export_upstream_data.py</c> 从上游生成的
/// <c>module/**/assets.py</c> 与 <c>campaign/**/*.py</c> 导出，本类只负责读取，
/// 不做任何语义推断 —— 上游数据长什么样，这里就呈现什么样。
/// </summary>
public static class UpstreamData
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>数据目录（内含 assets.json / campaign_index.json / campaign/ / schema/ / manifest.json）。</summary>
    public sealed class Catalog
    {
        public required string DataDirectory { get; init; }
        public required AssetCatalog Assets { get; init; }
        public required CampaignIndex Campaign { get; init; }
        public JsonDocument? Manifest { get; init; }

        public string? UpstreamCommit =>
            Manifest?.RootElement.TryGetProperty("upstream_commit", out var v) == true
                ? v.GetString() : null;

        public bool UpstreamDirty =>
            Manifest?.RootElement.TryGetProperty("upstream_dirty", out var v) == true
                && v.ValueKind == JsonValueKind.True;

        public CampaignIr LoadCampaign(CampaignIndexEntry entry)
            => LoadJson<CampaignIr>(System.IO.Path.Combine(DataDirectory,
                entry.Json.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        public static Catalog Open(string dataDirectory)
        {
            string dir = System.IO.Path.GetFullPath(dataDirectory);
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException($"数据目录不存在: {dir}（先跑 csharp/tools/export_upstream_data.py）");

            JsonDocument? manifest = null;
            string manifestPath = System.IO.Path.Combine(dir, "manifest.json");
            if (File.Exists(manifestPath))
                manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

            return new Catalog
            {
                DataDirectory = dir,
                Assets = LoadJson<AssetCatalog>(System.IO.Path.Combine(dir, "assets.json")),
                Campaign = LoadJson<CampaignIndex>(System.IO.Path.Combine(dir, "campaign_index.json")),
                Manifest = manifest,
            };
        }
    }

    public static T LoadJson<T>(string path)
    {
        using var stream = File.OpenRead(path);
        var value = JsonSerializer.Deserialize<T>(stream, Options);
        if (value is null)
            throw new InvalidDataException($"反序列化失败（null）: {path}");
        return value;
    }
}
