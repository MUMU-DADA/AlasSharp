using System.Text.Json;
using System.Text.Json.Serialization;
using Alas.Core;
using Alas.Core.Imaging;

namespace Alas.Core.Diagnostics;

public sealed class FixtureCase
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("image_shape")] public List<int> ImageShape { get; set; } = new();
    [JsonPropertyName("area")] public List<double> Area { get; set; } = new();
    [JsonPropertyName("color")] public List<double> Color { get; set; } = new();
    [JsonPropertyName("stored_color")] public List<double>? StoredColor { get; set; }
    [JsonPropertyName("similarity")] public double? Similarity { get; set; }
    [JsonPropertyName("appear_default")] public bool? AppearDefault { get; set; }
    [JsonPropertyName("appear_30")] public bool? Appear30 { get; set; }
}

public sealed class SyntheticCase
{
    [JsonPropertyName("image")] public string Image { get; set; } = "";
    [JsonPropertyName("case")] public string Case { get; set; } = "";
    [JsonPropertyName("area")] public List<double> Area { get; set; } = new();
    [JsonPropertyName("region_shape")] public List<int>? RegionShape { get; set; }
    [JsonPropertyName("region_sum")] public long? RegionSum { get; set; }
    [JsonPropertyName("color")] public List<double>? Color { get; set; }
    [JsonPropertyName("upstream_error")] public string? UpstreamError { get; set; }
}

public sealed class ImagingFixture
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("cases")] public List<FixtureCase> Cases { get; set; } = new();
    [JsonPropertyName("synthetic")] public List<SyntheticCase> Synthetic { get; set; } = new();
}

/// <summary>
/// S1 图像原语对拍：C# 实现 vs ALAS 真值。
///
/// 判定标准分三档：
///   1. 通道语义（灰度/彩色）必须一致 —— 错了后面全错
///   2. appear 判定（布尔）必须**完全一致** —— 这是运行时真正依赖的结果
///   3. 均值颜色允许浮点误差（OpenCV 的求和顺序与逐行累加不同，末位 ulp 级别）
/// </summary>
internal static class ImagingCheck
{
    private const double ColorTolerance = 1e-6;

    public static int Run(string fixturePath, string repoDir)
    {
        if (!System.IO.File.Exists(fixturePath))
            return Fail($"基准文件不存在: {fixturePath}\n先跑 tools/make_imaging_fixture.py");

        var fixture = JsonSerializer.Deserialize<ImagingFixture>(
            System.IO.File.ReadAllText(fixturePath), UpstreamData.Options)
            ?? throw new InvalidDataException("基准文件反序列化失败");
        string fixtureDir = Path.GetDirectoryName(Path.GetFullPath(fixturePath))!;

        int channelMismatch = 0, appearMismatch = 0, colorMismatch = 0,
            similarityMismatch = 0, loadError = 0;
        double maxColorDelta = 0;
        var samples = new List<string>();

        foreach (var c in fixture.Cases)
        {
            string path = Path.Combine(repoDir, c.File.Replace("./", "")
                .Replace('/', Path.DirectorySeparatorChar));
            ImageBuffer img;
            try
            {
                img = ImageLoader.LoadPng(path);
            }
            catch (Exception ex)
            {
                loadError++;
                if (samples.Count < 8) samples.Add($"加载失败 {c.Id} [{c.Server}]: {ex.Message}");
                continue;
            }

            // ---- 1. 通道语义
            int expectedChannels = c.ImageShape.Count == 2 ? 1 : 3;
            if (img.Channels != expectedChannels || img.Width != c.ImageShape[1]
                || img.Height != c.ImageShape[0])
            {
                channelMismatch++;
                if (samples.Count < 8)
                    samples.Add($"通道/尺寸 {c.Id} [{c.Server}] mode={c.Mode}: "
                                + $"C# {img.Height}x{img.Width}x{img.Channels} "
                                + $"vs ALAS {string.Join("x", c.ImageShape)}");
            }

            var area = new Area(c.Area[0], c.Area[1], c.Area[2], c.Area[3]);
            double[] got = ImageOps.GetColor(img, area);

            // ---- 3. 均值（容差）
            for (int i = 0; i < 3; i++)
            {
                double d = Math.Abs(got[i] - c.Color[i]);
                if (d > maxColorDelta) maxColorDelta = d;
                if (d > ColorTolerance)
                {
                    colorMismatch++;
                    if (samples.Count < 8)
                        samples.Add($"均值 {c.Id} [{c.Server}]: C# [{got[0]:F6},{got[1]:F6},{got[2]:F6}] "
                                    + $"vs ALAS [{c.Color[0]:F6},{c.Color[1]:F6},{c.Color[2]:F6}]");
                    break;
                }
            }

            if (c.StoredColor is null) continue;

            // ---- 2. appear 判定必须完全一致
            double sim = ImageOps.ColorSimilarity(got, c.StoredColor);
            if (c.Similarity is not null && Math.Abs(sim - c.Similarity.Value) > 1e-6)
            {
                similarityMismatch++;
                if (samples.Count < 8)
                    samples.Add($"容差 {c.Id} [{c.Server}]: C# {sim:F6} vs ALAS {c.Similarity.Value:F6}");
            }
            bool appear10 = ImageOps.ColorSimilar(got, c.StoredColor, 10);
            bool appear30 = ImageOps.ColorSimilar(got, c.StoredColor, 30);
            if (c.AppearDefault is not null && appear10 != c.AppearDefault.Value)
            {
                appearMismatch++;
                if (samples.Count < 8)
                    samples.Add($"appear(10) {c.Id} [{c.Server}]: C# {appear10} vs ALAS {c.AppearDefault.Value}");
            }
            if (c.Appear30 is not null && appear30 != c.Appear30.Value)
            {
                appearMismatch++;
                if (samples.Count < 8)
                    samples.Add($"appear(30) {c.Id} [{c.Server}]: C# {appear30} vs ALAS {c.Appear30.Value}");
            }
        }

        // ---- 合成裁剪用例
        int synChecked = 0, synMismatch = 0, synDivergence = 0;
        var synSamples = new List<string>();
        foreach (var s in fixture.Synthetic)
        {
            string path = Path.Combine(fixtureDir, s.Image.Replace('/', Path.DirectorySeparatorChar));
            var img = ImageLoader.LoadPng(path);
            var area = new Area(s.Area[0], s.Area[1], s.Area[2], s.Area[3]);
            var region = ImageOps.Crop(img, area);

            if (s.UpstreamError is not null)
            {
                // 已知分歧：上游对反向区域抛 cv2.error，C# 返回空区域。
                // 这里断言 C# 不抛异常且给出空区域，把分歧钉住而不是假装一致。
                synDivergence++;
                if (region.Width != 0 || region.Height != 0)
                {
                    synMismatch++;
                    synSamples.Add($"{s.Case}: 上游抛错但 C# 返回 {region.Width}x{region.Height}");
                }
                continue;
            }

            synChecked++;
            int wantH = s.RegionShape![0], wantW = s.RegionShape[1];
            int wantC = s.RegionShape.Count > 2 ? s.RegionShape[2] : 1;
            long sum = 0;
            foreach (byte b in region.Pixels) sum += b;

            if (region.Height != wantH || region.Width != wantW || region.Channels != wantC
                || sum != s.RegionSum)
            {
                synMismatch++;
                if (synSamples.Count < 8)
                    synSamples.Add($"{Path.GetFileName(s.Image)}/{s.Case} area=[{string.Join(",", s.Area)}]: "
                                   + $"C# {region.Height}x{region.Width}x{region.Channels} sum={sum} "
                                   + $"vs ALAS {wantH}x{wantW}x{wantC} sum={s.RegionSum}");
            }

            double[] got = ImageOps.Mean(region);
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(got[i] - s.Color![i]) > ColorTolerance)
                {
                    synMismatch++;
                    if (synSamples.Count < 8)
                        synSamples.Add($"{s.Case} 均值通道{i}: C# {got[i]:F6} vs ALAS {s.Color[i]:F6}");
                    break;
                }
            }
        }

        int problems = channelMismatch + appearMismatch + colorMismatch + similarityMismatch
                       + loadError + synMismatch;

        Console.WriteLine($"基准文件    : {fixturePath}");
        Console.WriteLine($"素材用例    : {fixture.Cases.Count}（其中带存储色 {fixture.Cases.Count(c => c.StoredColor is not null)}）");
        Console.WriteLine($"合成裁剪用例: {fixture.Synthetic.Count}（可比 {synChecked}，上游抛错 {synDivergence}）");
        Console.WriteLine();
        Console.WriteLine($"[通道语义] 不一致 {channelMismatch}");
        Console.WriteLine($"[appear  ] 不一致 {appearMismatch}");
        Console.WriteLine($"[容差值  ] 不一致 {similarityMismatch}");
        Console.WriteLine($"[均值颜色] 超差 {colorMismatch}（最大偏差 {maxColorDelta:G3}，容差 {ColorTolerance:G3}）");
        Console.WriteLine($"[加载失败] {loadError}");
        Console.WriteLine($"[裁剪边界] 不一致 {synMismatch}");
        foreach (var s in samples) Console.WriteLine($"   素材: {s}");
        foreach (var s in synSamples) Console.WriteLine($"   裁剪: {s}");

        Console.WriteLine();
        if (problems == 0)
        {
            Console.WriteLine("结果: OK");
            return 0;
        }
        Console.WriteLine($"结果: FAIL（{problems} 处）");
        return 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
