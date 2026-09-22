namespace Alas.Core.Imaging;

/// <summary>匹配结果矩阵（对应 OpenCV 的 CV_32F 单通道结果）。</summary>
public sealed class MatchResult
{
    public int Width { get; }
    public int Height { get; }
    public float[] Data { get; }

    public MatchResult(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new float[width * height];
    }

    public float At(int x, int y) => Data[y * Width + x];

    /// <summary>
    /// 对应 OpenCV <c>cv2.minMaxLoc()</c>：按行优先扫描，因此并列时取**最先出现**的位置。
    /// </summary>
    public (double MinVal, (int X, int Y) MinLoc, double MaxVal, (int X, int Y) MaxLoc) MinMaxLoc()
    {
        double minVal = double.MaxValue, maxVal = -double.MaxValue;
        int minX = 0, minY = 0, maxX = 0, maxY = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                float v = Data[y * Width + x];
                if (v < minVal) { minVal = v; minX = x; minY = y; }
                if (v > maxVal) { maxVal = v; maxX = x; maxY = y; }
            }
        }
        if (Data.Length == 0) return (0, (0, 0), 0, (0, 0));
        return (minVal, (minX, minY), maxVal, (maxX, maxY));
    }
}

/// <summary>
/// 模板匹配：<c>TM_CCOEFF_NORMED</c>。
///
/// 这是**参考实现**，不是最终性能路径。本机 NuGet 不通、拿不到 OpenCvSharp，
/// 所以先用受控实现把语义钉住；将来换 OpenCvSharp 时用同一套基准验收即可。
///
/// 三个必须复现的 OpenCV 行为（都是实测确认的，不是照文档推的）：
///   1. **参数自动交换**：<c>matchTemplate(image, templ)</c> 在 templ 大于 image 时会交换两者。
///      上游 `Button.match()` 写的正是 <c>cv2.matchTemplate(self.image, image)</c>
///      （模板在前、搜索区在后），完全依赖这个交换。
///      实测：<c>matchTemplate(t, s)</c> 与 <c>matchTemplate(s, t)</c> 结果逐位相同。
///   2. **均值按通道分别扣减**（这条最容易搞错）：模板与窗口都先减去**各自通道**的均值，
///      再把各通道的平方和**合并**起来做归一化。若按"全通道统一均值"实现，
///      实测与 OpenCV 的最大偏差可达 0.14（z 分数整体偏移），足以让判定翻转。
///   3. **结果类型 CV_32F**：内部 double 计算后落回 float。
///
/// 公式（c 为通道，N = 模板每通道像素数 = th×tw）：
///   T'_c = T_c − mean(T_c)，I'_c = I_c − mean(I_c)
///   num    = Σ_c Σ (T'_c · I'_c)
///   denomT = Σ_c (Σ T_c² − N·mean(T_c)²)
///   denomI = Σ_c (Σ I_c² − N·mean(I_c)²)
///   R      = num / sqrt(denomT·denomI)   （分母为 0 时 R = 0）
/// </summary>
public static class TemplateMatch
{
    /// <summary>
    /// 对应 <c>cv2.matchTemplate(image, templ, cv2.TM_CCOEFF_NORMED)</c>。
    /// 会自动处理 templ 大于 image 的情形（与 OpenCV 一致）。
    /// </summary>
    public static MatchResult CcoeffNormed(ImageBuffer image, ImageBuffer templ)
    {
        // OpenCV 行为：templ 在任一维大于 image 时交换两者（实测两序结果逐位相同）
        if (templ.Height > image.Height || templ.Width > image.Width)
            (image, templ) = (templ, image);

        if (templ.Height > image.Height || templ.Width > image.Width)
            throw new ArgumentException(
                $"模板 {templ.Height}x{templ.Width} 大于图像 {image.Height}x{image.Width}");

        if (templ.Channels != image.Channels)
            throw new ArgumentException(
                $"通道数必须一致（上游 OpenCV 同样会拒绝）：图像 {image.Channels} vs 模板 {templ.Channels}");

        int channels = image.Channels;
        int resultW = image.Width - templ.Width + 1;
        int resultH = image.Height - templ.Height + 1;
        var result = new MatchResult(resultW, resultH);

        int th = templ.Height, tw = templ.Width;
        double n = (double)th * tw;                   // 每通道像素数

        // ---- 模板的逐通道统计量
        var sumT = new double[channels];
        var sumT2 = new double[channels];
        for (int ty = 0; ty < th; ty++)
        {
            int rowBase = ty * templ.Stride;
            for (int tx = 0; tx < tw; tx++)
            {
                int p = rowBase + tx * channels;
                for (int c = 0; c < channels; c++)
                {
                    double v = templ.Pixels[p + c];
                    sumT[c] += v;
                    sumT2[c] += v * v;
                }
            }
        }
        var meanT = new double[channels];
        double denomT = 0;
        for (int c = 0; c < channels; c++)
        {
            meanT[c] = sumT[c] / n;
            denomT += sumT2[c] - n * meanT[c] * meanT[c];
        }

        // ---- 图像侧的逐通道积分图（窗口和与平方和 O(1) 取）
        int iw = image.Width, ih = image.Height;
        int strideI = iw + 1;
        var integral = new long[channels][];
        var integralSq = new long[channels][];
        for (int c = 0; c < channels; c++)
        {
            integral[c] = new long[strideI * (ih + 1)];
            integralSq[c] = new long[strideI * (ih + 1)];
        }
        for (int y = 0; y < ih; y++)
        {
            int rowBase = y * image.Stride;
            for (int c = 0; c < channels; c++)
            {
                long rowSum = 0, rowSumSq = 0;
                var acc = integral[c];
                var accSq = integralSq[c];
                for (int x = 0; x < iw; x++)
                {
                    long v = image.Pixels[rowBase + x * channels + c];
                    rowSum += v;
                    rowSumSq += v * v;
                    acc[(y + 1) * strideI + (x + 1)] = acc[y * strideI + (x + 1)] + rowSum;
                    accSq[(y + 1) * strideI + (x + 1)] = accSq[y * strideI + (x + 1)] + rowSumSq;
                }
            }
        }

        // ---- 逐通道互相关：模板像素在外层，结果与图像行都顺序访问
        var cross = new double[channels][];
        for (int c = 0; c < channels; c++) cross[c] = new double[resultW * resultH];
        for (int c = 0; c < channels; c++)
        {
            var acc = cross[c];
            for (int ty = 0; ty < th; ty++)
            {
                int tRow = ty * templ.Stride;
                for (int tx = 0; tx < tw; tx++)
                {
                    double tv = templ.Pixels[tRow + tx * channels + c];
                    if (tv == 0) continue;
                    for (int y = 0; y < resultH; y++)
                    {
                        int srcRow = (y + ty) * image.Stride + tx * channels + c;
                        int dstRow = y * resultW;
                        for (int x = 0; x < resultW; x++)
                            acc[dstRow + x] += tv * image.Pixels[srcRow + x * channels];
                    }
                }
            }
        }

        // ---- 归一化
        //
        // 退化情形是**非对称**的，实测（cv2 5.0）：
        //   - 模板方差为 0（纯色模板，如 campaign/EVENT_*_ENTRANCE）→ 整张结果矩阵返回 **1.0**
        //   - 窗口方差为 0（截图里的纯色区域）而模板非常量 → 返回 **0.0**
        // 若把两者都按 0 处理，纯色模板的关卡会从"恒真"变成"恒假"；
        // 若都按 1 处理，含纯色区域的截图会出现大量伪 1.0（实测 427 个位置里错 126 个）。
        bool flatTemplate = denomT <= 0;
        for (int y = 0; y < resultH; y++)
        {
            for (int x = 0; x < resultW; x++)
            {
                double num = 0, denomI = 0;
                for (int c = 0; c < channels; c++)
                {
                    var acc = integral[c];
                    var accSq = integralSq[c];
                    long s = acc[(y + th) * strideI + (x + tw)]
                             - acc[y * strideI + (x + tw)]
                             - acc[(y + th) * strideI + x]
                             + acc[y * strideI + x];
                    long s2 = accSq[(y + th) * strideI + (x + tw)]
                              - accSq[y * strideI + (x + tw)]
                              - accSq[(y + th) * strideI + x]
                              + accSq[y * strideI + x];

                    double mI = s / n;
                    // Σ (T_c − meanT_c)(I_c − meanI_c) 展开
                    num += cross[c][y * resultW + x]
                           - mI * sumT[c] - meanT[c] * s + n * meanT[c] * mI;
                    denomI += s2 - (double)s * s / n;
                }

                float value;
                if (flatTemplate) value = 1.0f;
                else if (denomI <= 0) value = 0.0f;
                else value = (float)(num / Math.Sqrt(denomT * denomI));
                result.Data[y * resultW + x] = value;
            }
        }

        return result;
    }
}
