namespace Alas.Core.Imaging;

/// <summary>
/// 上游 <c>module/base/utils.py</c> 中图像原语的逐条移植。
///
/// 这些函数是 ALAS 每帧判定按钮是否存在的底层路径（<c>Button.appear_on</c> →
/// <c>color_similar(get_color(image, area), self.color, threshold)</c>），
/// 实测单次 0.02ms，是最高频的判定路径。移植必须逐位对齐，见 S1 对拍基准。
/// </summary>
public static class ImageOps
{
    /// <summary>
    /// 对应上游 <c>crop()</c>（utils.py:573）。
    ///
    /// 要点：
    ///   - 坐标先做 **Python round()**，即银行家舍入（四舍六入五取偶），
    ///     所以这里必须用 <see cref="MidpointRounding.ToEven"/>，不能用默认的 AwayFromZero。
    ///   - 区域完全落在图外时（overflow），返回**全零**图，尺寸按四舍五入后的坐标算。
    ///   - 区域部分越界时，用零（黑）填充边界。
    /// </summary>
    public static ImageBuffer Crop(ImageBuffer image, Area area)
    {
        int x1 = PyRound(area.X1);
        int y1 = PyRound(area.Y1);
        int x2 = PyRound(area.X2);
        int y2 = PyRound(area.Y2);

        int h = image.Height;
        int w = image.Width;
        bool overflow = false;
        int top, bottom, left, right;

        if (y1 >= 0)
        {
            top = 0;
            if (y1 >= h) overflow = true;
        }
        else
        {
            top = -y1;
        }

        if (y2 > h)
        {
            bottom = y2 - h;
        }
        else
        {
            bottom = 0;
            if (y2 <= 0) overflow = true;
        }

        if (x1 >= 0)
        {
            left = 0;
            if (x1 >= w) overflow = true;
        }
        else
        {
            left = -x1;
        }

        if (x2 > w)
        {
            right = x2 - w;
        }
        else
        {
            right = 0;
            if (x2 <= 0) overflow = true;
        }

        if (overflow)
        {
            // 上游此处是 np.zeros((y2-y1, x2-x1, ch))；尺寸为负时 numpy 会抛错，
            // 这里夹到 0 以避免异常（上游正常路径不会出现负尺寸）。
            return new ImageBuffer(Math.Max(0, x2 - x1), Math.Max(0, y2 - y1), image.Channels);
        }

        if (x1 < 0) x1 = 0;
        if (y1 < 0) y1 = 0;
        if (x2 < 0) x2 = 0;
        if (y2 < 0) y2 = 0;

        // 上游这里做的是 `image[y1:y2, x1:x2]`：numpy 切片会把超出边界的部分**自动截断**，
        // 而超出部分稍后由 right/bottom 的补边补回来。因此：
        //   内容尺寸 = min(x2, w) - max(x1, 0)
        //   结果尺寸 = 内容尺寸 + left/right（或 top/bottom）补边
        // 直接用 (x2-x1) 会把溢出部分算两遍（实测踩过：48..60 的区域被算成 19 宽而不是 12）。
        int contentW = Math.Max(0, Math.Min(x2, w) - x1);
        int contentH = Math.Max(0, Math.Min(y2, h) - y1);
        var result = new ImageBuffer(contentW + left + right, contentH + top + bottom, image.Channels);

        int copyBytes = contentW * image.Channels;
        if (copyBytes > 0)
        {
            for (int row = 0; row < contentH; row++)
            {
                int srcY = y1 + row;
                if (srcY < 0 || srcY >= image.Height) continue;
                int srcOffset = srcY * image.Stride + x1 * image.Channels;
                int dstOffset = (top + row) * result.Stride + left * image.Channels;
                Array.Copy(image.Pixels, srcOffset, result.Pixels, dstOffset, copyBytes);
            }
        }

        return result;
    }

    /// <summary>
    /// 对应上游 <c>get_color()</c>（utils.py:779）= <c>cv2.mean(crop(image, area))[:3]</c>。
    ///
    /// 返回长度恒为 3 的数组：灰度图（上游二维数组）的均值放在第 0 位，其余补 0，
    /// 精确复现 <c>cv2.mean</c> 对单通道输入返回 <c>(mean, 0, 0, 0)</c> 的行为。
    /// </summary>
    public static double[] GetColor(ImageBuffer image, Area area)
    {
        var temp = Crop(image, area);
        return Mean(temp);
    }

    /// <summary>对应上游 <c>cv2.mean()</c>：逐通道均值，结果扩展到 3 位。</summary>
    public static double[] Mean(ImageBuffer image)
    {
        var sums = new double[image.Channels];
        long count = (long)image.Width * image.Height;
        if (count > 0)
        {
            var pixels = image.Pixels;
            int channels = image.Channels;
            int stride = image.Stride;
            for (int y = 0; y < image.Height; y++)
            {
                int offset = y * stride;
                for (int x = 0; x < image.Width; x++)
                {
                    int i = offset + x * channels;
                    for (int c = 0; c < channels; c++)
                        sums[c] += pixels[i + c];
                }
            }
            for (int c = 0; c < image.Channels; c++)
                sums[c] /= count;
        }

        var result = new double[3];
        for (int c = 0; c < image.Channels && c < 3; c++)
            result[c] = sums[c];
        return result;
    }

    /// <summary>
    /// 对应上游 <c>color_similarity()</c>（utils.py:923）。
    ///
    /// 语义：最大正差 − 最小负差（0 为基线），即 Photoshop 的容差定义。
    /// 上游用 if/elif 链写成，等价于 <c>max(0, 各差值) - min(0, 各差值)</c>，此处逐字对应。
    /// </summary>
    public static double ColorSimilarity(IReadOnlyList<double> color1, IReadOnlyList<double> color2)
    {
        double diffR = color1[0] - color2[0];
        double diffG = color1[1] - color2[1];
        double diffB = color1[2] - color2[2];

        double maxPositive = 0;
        double maxNegative = 0;

        if (diffR > maxPositive) maxPositive = diffR;
        else if (diffR < maxNegative) maxNegative = diffR;

        if (diffG > maxPositive) maxPositive = diffG;
        else if (diffG < maxNegative) maxNegative = diffG;

        if (diffB > maxPositive) maxPositive = diffB;
        else if (diffB < maxNegative) maxNegative = diffB;

        return maxPositive - maxNegative;
    }

    /// <summary>对应上游 <c>color_similar()</c>（utils.py:958）：容差 &lt;= 阈值即视为同色。</summary>
    public static bool ColorSimilar(IReadOnlyList<double> color1, IReadOnlyList<double> color2,
                                    double threshold = 10)
        => ColorSimilarity(color1, color2) <= threshold;

    /// <summary>Python <c>round()</c> 的银行家舍入（四舍六入五取偶）。</summary>
    private static int PyRound(double value) => (int)Math.Round(value, MidpointRounding.ToEven);
}
