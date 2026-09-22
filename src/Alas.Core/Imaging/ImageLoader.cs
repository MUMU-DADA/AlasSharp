using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Alas.Core.Imaging;

/// <summary>
/// 素材图片解码，语义对齐上游 <c>module/base/utils.py: load_image()</c>。
///
/// 上游走 PIL：<c>np.array(Image.open(file))</c>，然后
///   - 4 通道 → <c>cv2.cvtColor(RGBA2RGB)</c>（丢 alpha）
///   - 1 通道（mode 'L'）→ 保持二维
///   - 3 通道 → 原样
/// 这里逐条对应，不做任何额外归一化。
/// </summary>
public static class ImageLoader
{
    public static ImageBuffer LoadPng(string path)
    {
        using var image = Image.Load<Rgba32>(path);

        // PIL mode 'L' 的等价物：灰度 PNG。ALAS 会得到二维数组，这里用 Channels=1 表达。
        bool grayscale = false;
        try
        {
            var png = image.Metadata.GetPngMetadata();
            grayscale = png.ColorType is PngColorType.Grayscale or PngColorType.GrayscaleWithAlpha;
        }
        catch (Exception)
        {
            // 非 PNG 或无 PNG 元数据：按彩色处理
        }

        int channels = grayscale ? 1 : 3;
        var buffer = new ImageBuffer(image.Width, image.Height, channels);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int offset = y * buffer.Stride;
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (channels == 1)
                    {
                        buffer.Pixels[offset + x] = p.R;
                    }
                    else
                    {
                        int i = offset + x * 3;
                        buffer.Pixels[i] = p.R;
                        buffer.Pixels[i + 1] = p.G;
                        buffer.Pixels[i + 2] = p.B;
                    }
                }
            }
        });
        return buffer;
    }
}
