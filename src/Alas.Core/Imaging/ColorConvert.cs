namespace Alas.Core.Imaging;

/// <summary>
/// 颜色空间转换，逐条对齐 OpenCV 的 8 位定点实现。
///
/// ⚠️ 这里有两件"反直觉但必须照抄"的事，都是实测确认的：
///
/// <b>一、通道顺序错位</b>：上游内部图像是 RGB（见 <see cref="ImageBuffer"/>），但
/// <c>module/base/button.py</c> 的 <c>match_binary()</c> / <c>match_luma()</c> 与
/// <c>template.py</c> 的 <c>image_binary</c> 里写的是
/// <c>cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)</c> —— 把 **BGR 的权重套在 RGB 数据上**。
/// 这是上游既有行为，移植必须照抄（R 通道吃到 B 的权重）。
/// 实测：若"顺手修正"成 RGB2GRAY 的权重，60 个样本的灰度总和偏差达 140 万，判定必然错。
///
/// <b>二、不同转换用的定点精度不同</b>（OpenCV 5.0 实测，不是文档写的）：
///   - <c>COLOR_BGR2GRAY</c> / <c>COLOR_RGB2GRAY</c>：**15 位**定点，系数按 32768 缩放
///   - <c>COLOR_RGB2YUV</c> 的 Y 通道：**14 位**定点，系数按 16384 缩放
/// 两套系数下总偏差分别为 0 与 277（60 个样本、约 20 万像素），因此不是"约等于"而是必须区分。
/// </summary>
public static class ColorConvert
{
    // ---- 15 位定点：COLOR_BGR2GRAY / COLOR_RGB2GRAY
    private const int K15_R = 9798;    // 0.299 * 32768
    private const int K15_G = 19235;   // 0.587 * 32768
    private const int K15_B = 3735;    // 0.114 * 32768
    private const int S15 = 15;
    private const int H15 = 1 << (S15 - 1);

    // ---- 14 位定点：COLOR_RGB2YUV 的 Y
    private const int K14_R = 4899;    // 0.299 * 16384
    private const int K14_G = 9617;    // 0.587 * 16384
    private const int K14_B = 1868;    // 0.114 * 16384
    private const int S14 = 14;
    private const int H14 = 1 << (S14 - 1);

    /// <summary>
    /// 对应上游 <c>cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)</c>。
    /// 注意：**按位置**使用 BGR 权重，传进来的 RGB 缓冲会得到与上游完全相同（同样错位）的结果。
    /// </summary>
    public static ImageBuffer ToGrayBgrWeights(ImageBuffer src)
    {
        RequireThreeChannels(src, nameof(ToGrayBgrWeights));
        return Gray(src, K15_B, K15_G, K15_R, S15, H15);
    }

    /// <summary>对应 <c>cv2.cvtColor(image, cv2.COLOR_RGB2GRAY)</c>（RGB 权重，位置正确）。</summary>
    public static ImageBuffer ToGrayRgbWeights(ImageBuffer src)
    {
        RequireThreeChannels(src, nameof(ToGrayRgbWeights));
        return Gray(src, K15_R, K15_G, K15_B, S15, H15);
    }

    /// <summary>
    /// 对应上游 <c>utils.rgb2luma()</c> = <c>cv2.cvtColor(image, cv2.COLOR_RGB2YUV)</c> 的 Y 通道。
    /// 用的是 **14 位**系数（与 2GRAY 系列不同，见类注释）。
    /// </summary>
    public static ImageBuffer Rgb2Luma(ImageBuffer src)
    {
        RequireThreeChannels(src, nameof(Rgb2Luma));
        return Gray(src, K14_R, K14_G, K14_B, S14, H14);
    }

    /// <summary>按 (ch0, ch1, ch2) 各自的权重做定点灰度化。</summary>
    private static ImageBuffer Gray(ImageBuffer src, int w0, int w1, int w2, int shift, int half)
    {
        var dst = new ImageBuffer(src.Width, src.Height, 1);
        var srcPixels = src.Pixels;
        var dstPixels = dst.Pixels;
        for (int i = 0, j = 0; i < srcPixels.Length; i += 3, j++)
        {
            int v = srcPixels[i] * w0 + srcPixels[i + 1] * w1 + srcPixels[i + 2] * w2 + half;
            int y = v >> shift;
            dstPixels[j] = (byte)(y > 255 ? 255 : y);
        }
        return dst;
    }

    private static void RequireThreeChannels(ImageBuffer src, string caller)
    {
        if (src.Channels != 3)
            throw new ArgumentException(
                $"{caller} 需要三通道图像（上游对单通道图调用 cv2.cvtColor 同样会抛错），"
                + $"实际 {src.Channels} 通道", nameof(src));
    }
}
