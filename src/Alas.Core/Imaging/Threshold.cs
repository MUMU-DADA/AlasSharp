namespace Alas.Core.Imaging;

/// <summary>
/// 阈值化，对齐 OpenCV <c>cv2.threshold(src, 0, 255, THRESH_BINARY | THRESH_OTSU)</c>。
///
/// Otsu 的阈值选取用 OpenCV 的原实现逐句移植（<c>getThreshVal_Otsu_8u</c>）：
///   - 直方图是 256 桶的 int 计数
///   - 用 double 累积，且 <c>mu1 *= q1</c> 这种「先退归一化再累加」的写法必须照抄，
///     它决定了浮点累积顺序，进而影响边界情况的取值
///   - q1/q2 任一接近 0 或 1 时跳过（FLT_EPSILON 判定）
///   - 严格用 <c>&gt;</c> 比较，所以并列时取**较小**的阈值
/// </summary>
public static class Threshold
{
    private const float FLT_EPSILON = 1.1920929e-7f;

    public sealed record OtsuResult(int ThresholdValue, ImageBuffer Binary);

    /// <summary>算 Otsu 阈值，并按 THRESH_BINARY 生成二值图（&gt; 阈值为 255，否则 0）。</summary>
    public static OtsuResult OtsuBinary(ImageBuffer src)
    {
        if (src.Channels != 1)
            throw new ArgumentException("Otsu 只作用于单通道图（上游先做 cvtColor 灰度化）", nameof(src));

        int value = OtsuValue(src);
        var dst = new ImageBuffer(src.Width, src.Height, 1);
        for (int i = 0; i < src.Pixels.Length; i++)
            dst.Pixels[i] = src.Pixels[i] > value ? (byte)255 : (byte)0;
        return new OtsuResult(value, dst);
    }

    /// <summary>对应 OpenCV <c>getThreshVal_Otsu_8u</c>。</summary>
    public static int OtsuValue(ImageBuffer src)
    {
        if (src.Channels != 1)
            throw new ArgumentException("Otsu 只作用于单通道图", nameof(src));

        long total = (long)src.Width * src.Height;
        if (total == 0) return 0;

        Span<int> histogram = stackalloc int[256];
        foreach (byte p in src.Pixels) histogram[p]++;

        double scale = 1.0 / total;
        double mu = 0;
        for (int i = 0; i < 256; i++) mu += i * (double)histogram[i];
        mu *= scale;

        double mu1 = 0, q1 = 0;
        double maxSigma = 0, maxVal = 0;
        for (int i = 0; i < 256; i++)
        {
            double pI = histogram[i] * scale;
            mu1 *= q1;
            q1 += pI;
            double q2 = 1.0 - q1;
            if (Math.Min(q1, q2) < FLT_EPSILON || Math.Max(q1, q2) > 1.0 - FLT_EPSILON)
                continue;
            mu1 = (mu1 + i * pI) / q1;
            double mu2 = (mu - q1 * mu1) / q2;
            double sigma = q1 * q2 * (mu1 - mu2) * (mu1 - mu2);
            if (sigma > maxSigma)
            {
                maxSigma = sigma;
                maxVal = i;
            }
        }
        return (int)maxVal;
    }

    /// <summary>对应 OpenCV <c>cv2.threshold(src, thresh, maxval, THRESH_BINARY)</c>。</summary>
    public static ImageBuffer Binary(ImageBuffer src, int thresh, int maxval = 255)
    {
        if (src.Channels != 1)
            throw new ArgumentException("THRESH_BINARY 这里只用于单通道图", nameof(src));
        var dst = new ImageBuffer(src.Width, src.Height, 1);
        for (int i = 0; i < src.Pixels.Length; i++)
            dst.Pixels[i] = src.Pixels[i] > thresh ? (byte)maxval : (byte)0;
        return dst;
    }
}
