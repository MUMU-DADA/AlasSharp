using Alas.Vision;

namespace Alas.Device;

/// <summary>
/// 设备控制：截图与操作。
///
/// **架构要点：像素不跨语言边界。** 截图拿到的是 PNG 字节，直接交给识图宿主解码
/// （`IVisionEngine.SetScreenshot`），C# 侧从不持有、也不解释像素。
/// 这样上游那套解码/匹配语义始终是唯一真值来源。
///
/// adb 命令形态照抄上游 `module/device/method/adb.py`：
///   - 截图：`exec-out screencap -p`（用 exec-out 而非 shell，避免 CRLF 破坏二进制）
///   - 点击：`shell input tap x y`
///   - 滑动：`shell input swipe x1 y1 x2 y2 duration`
/// </summary>
public sealed class DeviceController
{
    private readonly IAdbTransport _adb;
    private readonly IVisionEngine _vision;

    public DeviceController(IAdbTransport adb, IVisionEngine vision, string? serial = null)
    {
        _adb = adb;
        _vision = vision;
        Serial = serial;
    }

    public string? Serial { get; }

    private List<string> Args(params string[] rest)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(Serial)) { list.Add("-s"); list.Add(Serial); }
        list.AddRange(rest);
        return list;
    }

    /// <summary>已连接设备列表（对应 `adb devices`）。</summary>
    public List<string> Devices()
    {
        var r = _adb.Run(Args("devices"));
        var devices = new List<string>();
        foreach (string line in r.StdoutText.Split('\n').Skip(1))
        {
            string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Trim() == "device") devices.Add(parts[0].Trim());
        }
        return devices;
    }

    public string GetState() => _adb.Run(Args("get-state")).StdoutText.Trim();

    /// <summary>屏幕物理分辨率（对应 `wm size`，输出形如 `Physical size: 1280x720`）。</summary>
    public (int Width, int Height)? ScreenSize()
    {
        var r = _adb.Run(Args("shell", "wm", "size"));
        foreach (string line in r.StdoutText.Split('\n'))
        {
            int idx = line.IndexOf("size:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            string[] wh = line[(idx + 5)..].Trim().Split('x');
            if (wh.Length == 2 && int.TryParse(wh[0], out int w) && int.TryParse(wh[1], out int h))
                return (w, h);
        }
        return null;
    }

    /// <summary>
    /// 截图：`exec-out screencap -p` 取 PNG 字节，交给识图宿主解码。
    /// 返回解码后的形状（宽, 高, 通道）。
    /// </summary>
    public ScreenshotInfo Screenshot()
    {
        var r = _adb.Run(Args("exec-out", "screencap", "-p"));
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"screencap 失败（exit={r.ExitCode}）: {r.Stderr.Trim()}");
        if (r.StdoutBytes.Length == 0)
            throw new InvalidOperationException("screencap 返回空字节流");
        return _vision.SetScreenshot(r.StdoutBytes, "adb://screencap");
    }

    public void Click(int x, int y)
        => _adb.Run(Args("shell", "input", "tap", x.ToString(), y.ToString()));

    public void Swipe(int x1, int y1, int x2, int y2, int durationMs = 100)
        => _adb.Run(Args("shell", "input", "swipe", x1.ToString(), y1.ToString(),
                         x2.ToString(), y2.ToString(), durationMs.ToString()));

    public AdbResult Shell(params string[] command)
        => _adb.Run(Args(new[] { "shell" }.Concat(command).ToArray()));
}
