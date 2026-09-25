using System.Text.Json;
using System.Text.Json.Serialization;
using Alas.Core;
using Alas.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Alas.Core.Diagnostics;

public sealed class GrayCase
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("rect")] public List<double> Rect { get; set; } = new();
    [JsonPropertyName("rgb_shape")] public List<int> RgbShape { get; set; } = new();
    [JsonPropertyName("gray_sum")] public long GraySum { get; set; }
    [JsonPropertyName("gray_samples")] public List<int> GraySamples { get; set; } = new();
    [JsonPropertyName("gray_rgb_sum")] public long GrayRgbSum { get; set; }
    [JsonPropertyName("gray_rgb_samples")] public List<int> GrayRgbSamples { get; set; } = new();
    [JsonPropertyName("luma_sum")] public long LumaSum { get; set; }
    [JsonPropertyName("luma_samples")] public List<int> LumaSamples { get; set; } = new();
    [JsonPropertyName("otsu")] public int Otsu { get; set; }
    [JsonPropertyName("binary_sum")] public long BinarySum { get; set; }
}

public sealed class MatchCase
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("area")] public List<double> Area { get; set; } = new();
    [JsonPropertyName("offset")] public double Offset { get; set; }
    [JsonPropertyName("template_shape")] public List<int> TemplateShape { get; set; } = new();
    [JsonPropertyName("search_shape")] public List<int> SearchShape { get; set; } = new();
    [JsonPropertyName("result_shape")] public List<int> ResultShape { get; set; } = new();
    [JsonPropertyName("min")] public double Min { get; set; }
    [JsonPropertyName("max")] public double Max { get; set; }
    [JsonPropertyName("min_loc")] public List<int> MinLoc { get; set; } = new();
    [JsonPropertyName("max_loc")] public List<int> MaxLoc { get; set; } = new();
    [JsonPropertyName("sum")] public double Sum { get; set; }
    [JsonPropertyName("matrix")] public List<double>? Matrix { get; set; }
}

public sealed class GifFrameTruth
{
    [JsonPropertyName("shape")] public List<int> Shape { get; set; } = new();
    [JsonPropertyName("sum")] public long Sum { get; set; }
    [JsonPropertyName("samples")] public List<int> Samples { get; set; } = new();
}

public sealed class GifCase
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("frame_count")] public int FrameCount { get; set; }
    [JsonPropertyName("first_frame_ndim")] public int FirstFrameNdim { get; set; }
    [JsonPropertyName("channel_mode")] public int ChannelMode { get; set; }
    [JsonPropertyName("frames")] public List<GifFrameTruth> Frames { get; set; } = new();
}

public sealed class MatchingFixture
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("gray")] public List<GrayCase> Gray { get; set; } = new();
    [JsonPropertyName("match")] public List<MatchCase> Match { get; set; } = new();
    [JsonPropertyName("gif")] public List<GifCase> Gif { get; set; } = new();
}

/// <summary>
/// S1b 对拍：灰度化 / 亮度 / OTSU / 模板匹配 / GIF 帧语义。
///
/// 判据分档（与 S1a 一致）：
///   - 灰度与亮度：整数像素值，**必须完全一致**（求和 + 采样点双重比对）
///   - OTSU 阈值：整数，**必须完全一致**
///   - 模板匹配：最大值/位置必须一致（这是 ALAS 真正用来判定的量），矩阵逐元素容差 1e-5
///   - GIF：帧数、每帧形状与像素和
/// </summary>
internal static class MatchingCheck
{
    private const double MatrixTolerance = 1e-5;
    private const double ScalarTolerance = 1e-6;

    public static int Run(string fixturePath, string repoDir)
    {
        if (!File.Exists(fixturePath))
            return Fail($"基准文件不存在: {fixturePath}\n先跑 tools/make_matching_fixture.py");

        var fx = JsonSerializer.Deserialize<MatchingFixture>(
            File.ReadAllText(fixturePath), UpstreamData.Options)
            ?? throw new InvalidDataException("基准反序列化失败");

        var problems = new List<string>();
        int grayBad = 0, lumaBad = 0, otsuBad = 0, binBad = 0;
        var samples = new List<string>();

        // ---------------------------------------------------------- 灰度 / 亮度 / OTSU
        foreach (var c in fx.Gray)
        {
            var full = ImageLoader.LoadPng(Path.Combine(repoDir, Rel(c.File)));
            var region = ImageOps.Crop(full, new Area(c.Rect[0], c.Rect[1], c.Rect[2], c.Rect[3]));
            if (region.Channels != 3)
            {
                grayBad++;
                if (samples.Count < 8)
                    samples.Add($"灰度 {c.Id}: 区域是 {region.Channels} 通道（基准为 3）");
                continue;
            }

            var gray = ColorConvert.ToGrayBgrWeights(region);   // 上游就是 BGR2GRAY 套在 RGB 上
            long graySum = 0;
            foreach (byte b in gray.Pixels) graySum += b;
            if (graySum != c.GraySum || !SamplesMatch(gray, c.GraySamples))
            {
                grayBad++;
                if (samples.Count < 8)
                    samples.Add($"灰度 {c.Id} [{c.Server}]: sum C#={graySum} ALAS={c.GraySum}");
            }

            var luma = ColorConvert.Rgb2Luma(region);
            long lumaSum = 0;
            foreach (byte b in luma.Pixels) lumaSum += b;
            if (lumaSum != c.LumaSum || !SamplesMatch(luma, c.LumaSamples))
            {
                lumaBad++;
                if (samples.Count < 8)
                    samples.Add($"亮度 {c.Id} [{c.Server}]: sum C#={lumaSum} ALAS={c.LumaSum}");
            }

            var grayRgb = ColorConvert.ToGrayRgbWeights(region);
            long grayRgbSum = 0;
            foreach (byte b in grayRgb.Pixels) grayRgbSum += b;
            if (grayRgbSum != c.GrayRgbSum || !SamplesMatch(grayRgb, c.GrayRgbSamples))
            {
                grayBad++;
                if (samples.Count < 8)
                    samples.Add($"RGB2GRAY {c.Id} [{c.Server}]: sum C#={grayRgbSum} ALAS={c.GrayRgbSum}");
            }

            int otsu = Threshold.OtsuValue(gray);
            if (otsu != c.Otsu)
            {
                otsuBad++;
                if (samples.Count < 8)
                    samples.Add($"OTSU {c.Id} [{c.Server}]: C#={otsu} ALAS={c.Otsu}");
            }

            var binary = Threshold.Binary(gray, otsu);
            long binSum = 0;
            foreach (byte b in binary.Pixels) binSum += b;
            if (binSum != c.BinarySum)
            {
                binBad++;
                if (samples.Count < 8)
                    samples.Add($"二值 {c.Id} [{c.Server}]: sum C#={binSum} ALAS={c.BinarySum}");
            }
        }

        // ---------------------------------------------------------- 模板匹配
        int shapeBad = 0, maxBad = 0, locBad = 0, matrixBad = 0, sumBad = 0;
        double maxMatrixDelta = 0;
        var matchSamples = new List<string>();

        foreach (var c in fx.Match)
        {
            var full = ImageLoader.LoadPng(Path.Combine(repoDir, Rel(c.File)));
            var area = new Area(c.Area[0], c.Area[1], c.Area[2], c.Area[3]);
            var template = ImageOps.Crop(full, area);
            var search = ImageOps.Crop(full, new Area(
                area.X1 - 3, area.Y1 - c.Offset, area.X2 + 3, area.Y2 + c.Offset));

            if (!ShapeEquals(template, c.TemplateShape) || !ShapeEquals(search, c.SearchShape))
            {
                shapeBad++;
                if (matchSamples.Count < 8)
                    matchSamples.Add($"输入形状 {c.Id}: 模板 {template.Height}x{template.Width}x{template.Channels} "
                                     + $"vs {string.Join("x", c.TemplateShape)}");
                continue;
            }

            // 与上游同样的实参顺序：模板在前、搜索区在后（依赖自动交换）
            var res = TemplateMatch.CcoeffNormed(template, search);

            if (res.Height != c.ResultShape[0] || res.Width != c.ResultShape[1])
            {
                shapeBad++;
                if (matchSamples.Count < 8)
                    matchSamples.Add($"结果形状 {c.Id}: C# {res.Height}x{res.Width} "
                                     + $"vs ALAS {string.Join("x", c.ResultShape)}");
                continue;
            }

            var (minVal, minLoc, maxVal, maxLoc) = res.MinMaxLoc();
            if (Math.Abs(maxVal - c.Max) > MatrixTolerance || Math.Abs(minVal - c.Min) > MatrixTolerance)
            {
                maxBad++;
                if (matchSamples.Count < 8)
                    matchSamples.Add($"极值 {c.Id}: C# max={maxVal:F6} min={minVal:F6} "
                                     + $"vs ALAS max={c.Max:F6} min={c.Min:F6}");
            }
            if (maxLoc.X != c.MaxLoc[0] || maxLoc.Y != c.MaxLoc[1]
                || minLoc.X != c.MinLoc[0] || minLoc.Y != c.MinLoc[1])
            {
                locBad++;
                if (matchSamples.Count < 8)
                    matchSamples.Add($"极值位置 {c.Id}: C# max@{maxLoc} min@{minLoc} "
                                     + $"vs ALAS max@({c.MaxLoc[0]},{c.MaxLoc[1]}) min@({c.MinLoc[0]},{c.MinLoc[1]})");
            }

            double sum = 0;
            foreach (float v in res.Data) sum += v;
            if (Math.Abs(sum - c.Sum) > Math.Abs(c.Sum) * 1e-6 + 1e-3)
            {
                sumBad++;
                if (matchSamples.Count < 8)
                    matchSamples.Add($"矩阵和 {c.Id}: C# {sum:F4} vs ALAS {c.Sum:F4}");
            }

            if (c.Matrix is not null)
            {
                for (int i = 0; i < c.Matrix.Count && i < res.Data.Length; i++)
                {
                    double d = Math.Abs(res.Data[i] - c.Matrix[i]);
                    if (d > maxMatrixDelta) maxMatrixDelta = d;
                    if (d > MatrixTolerance)
                    {
                        matrixBad++;
                        if (matchSamples.Count < 8)
                            matchSamples.Add($"矩阵 [{i}] {c.Id}: C# {res.Data[i]:F6} vs ALAS {c.Matrix[i]:F6}");
                        break;
                    }
                }
            }
        }

        // ---------------------------------------------------------- GIF 帧语义
        int gifBad = 0, gifChecked = 0;
        var gifSamples = new List<string>();
        foreach (var c in fx.Gif)
        {
            gifChecked++;
            using var img = Image.Load<Rgba32>(Path.Combine(repoDir, Rel(c.File)));
            if (img.Frames.Count != c.FrameCount)
            {
                gifBad++;
                if (gifSamples.Count < 6)
                    gifSamples.Add($"{Path.GetFileName(c.File)}: 帧数 C#={img.Frames.Count} ALAS={c.FrameCount}");
                continue;
            }
            for (int i = 0; i < c.FrameCount; i++)
            {
                var frame = img.Frames[i];
                var truth = c.Frames[i];
                bool expectGray = truth.Shape.Count == 2;
                // 上游「跟随首帧」规则：首帧 3 维则截三通道，首帧 2 维则取第 0 通道
                bool gray = c.ChannelMode == 2;
                long sum = 0;
                int w = frame.Width, h = frame.Height;
                frame.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < h; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < w; x++)
                            sum += gray ? row[x].R : row[x].R + row[x].G + row[x].B;
                    }
                });
                if (w != truth.Shape[1] || h != truth.Shape[0])
                {
                    gifBad++;
                    if (gifSamples.Count < 6)
                        gifSamples.Add($"{Path.GetFileName(c.File)} 帧{i}: 尺寸 C#={h}x{w} "
                                       + $"ALAS={truth.Shape[0]}x{truth.Shape[1]}");
                    break;
                }
                if (sum != truth.Sum)
                {
                    gifBad++;
                    if (gifSamples.Count < 6)
                        gifSamples.Add($"{Path.GetFileName(c.File)} 帧{i}: 像素和 C#={sum} ALAS={truth.Sum} "
                                       + $"(gray={gray}, expectGrayFrame={expectGray})");
                    break;
                }
            }
        }

        // ---------------------------------------------------------- 汇总
        int total = grayBad + lumaBad + otsuBad + binBad + shapeBad + maxBad + locBad
                    + matrixBad + sumBad + gifBad;
        Console.WriteLine($"基准文件    : {fixturePath}");
        Console.WriteLine($"灰度用例    : {fx.Gray.Count}（含亮度/OTSU/二值）");
        Console.WriteLine($"匹配用例    : {fx.Match.Count}（含完整矩阵 {fx.Match.Count(c => c.Matrix is not null)}）");
        Console.WriteLine($"GIF 用例    : {fx.Gif.Count}");
        Console.WriteLine();
        Console.WriteLine($"[灰度化  ] 不一致 {grayBad}    [亮度    ] 不一致 {lumaBad}");
        Console.WriteLine($"[OTSU    ] 不一致 {otsuBad}    [二值图  ] 不一致 {binBad}");
        Console.WriteLine($"[匹配形状] 不一致 {shapeBad}    [极值    ] 不一致 {maxBad}");
        Console.WriteLine($"[极值位置] 不一致 {locBad}    [矩阵和  ] 不一致 {sumBad}");
        Console.WriteLine($"[矩阵逐元] 超差 {matrixBad}（最大偏差 {maxMatrixDelta:G3}，容差 {MatrixTolerance:G3}）");
        Console.WriteLine($"[GIF 帧  ] 不一致 {gifBad} / {gifChecked}");
        foreach (var s in samples) Console.WriteLine($"   原语: {s}");
        foreach (var s in matchSamples) Console.WriteLine($"   匹配: {s}");
        foreach (var s in gifSamples) Console.WriteLine($"   GIF : {s}");

        Console.WriteLine();
        if (total == 0)
        {
            Console.WriteLine("结果: OK");
            return 0;
        }
        Console.WriteLine($"结果: FAIL（{total} 处）");
        return 1;
    }

    private static string Rel(string p) => p.Replace("./", "").Replace('/', Path.DirectorySeparatorChar);

    private static bool ShapeEquals(ImageBuffer b, List<int> shape)
        => b.Height == shape[0] && b.Width == shape[1]
           && b.Channels == (shape.Count > 2 ? shape[2] : 1);

    private static bool SamplesMatch(ImageBuffer b, List<int> samples)
    {
        var flat = b.Pixels;
        for (int i = 0; i < samples.Count; i++)
        {
            int idx = (int)((long)i * (flat.Length - 1) / Math.Max(samples.Count - 1, 1));
            if (flat[idx] != samples[i]) return false;
        }
        return true;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
