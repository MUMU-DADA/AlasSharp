namespace Alas.Core.Imaging;

/// <summary>
/// 与 ALAS 内部约定一致的图像缓冲。
///
/// 关键约定（来自上游实现，不是我们的选择）：
///   1. **通道顺序是 RGB**。ALAS 的截图方法在 <c>cv2.imdecode</c> 之后统一执行
///      <c>cv2.cvtColor(image, cv2.COLOR_BGR2RGB)</c>（`module/device/method/*.py` 共 5 处），
///      而素材用 PIL 的 <c>load_image</c> 加载，本身就是 RGB。所以全内部都是 RGB。
///   2. **通道数可以是 1**。上游 304 张 PNG 是 8 位灰度（PIL mode 'L'），
///      <c>np.array()</c> 得到的是 (H, W) 二维数组，于是 <c>cv2.mean()[:3]</c> 返回
///      <c>(mean, 0.0, 0.0)</c>。这里保留 <see cref="Channels"/> 来表达同一件事。
///   3. **RGBA 直接丢 alpha**。上游对 4 通道走 <c>cv2.cvtColor(RGBA2RGB)</c>，即丢弃 alpha，
///      不与任何背景合成。
/// </summary>
public sealed class ImageBuffer
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>1（灰度）或 3（RGB）。</summary>
    public int Channels { get; }

    /// <summary>行优先、通道交错。</summary>
    public byte[] Pixels { get; }

    public int Stride => Width * Channels;

    public ImageBuffer(int width, int height, int channels)
    {
        if (width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (channels != 1 && channels != 3) throw new ArgumentException("只支持 1 或 3 通道", nameof(channels));
        Width = width;
        Height = height;
        Channels = channels;
        Pixels = new byte[width * height * channels];
    }

    public ImageBuffer(int width, int height, int channels, byte[] pixels) : this(width, height, channels)
    {
        if (pixels.Length != Pixels.Length)
            throw new ArgumentException($"像素数量不匹配: 期望 {Pixels.Length}, 实际 {pixels.Length}");
        Array.Copy(pixels, Pixels, pixels.Length);
    }

    public byte At(int x, int y, int c) => Pixels[y * Stride + x * Channels + c];
}

/// <summary>矩形区域，语义与 ALAS 的 <c>area=(x1, y1, x2, y2)</c> 一致。</summary>
public readonly record struct Area(double X1, double Y1, double X2, double Y2)
{
    public static Area FromInts(IReadOnlyList<int> v)
    {
        if (v.Count != 4) throw new ArgumentException("area 必须是 4 个整数");
        return new Area(v[0], v[1], v[2], v[3]);
    }
}
