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

    /// <summary>
    /// 确保 <see cref="Serial"/> 处于 device 状态：不在就 `adb connect` 一次。
    ///
    /// 为什么必须做：adb 的 **daemon 会被新起的客户端重启**，daemon 一换，
    /// 之前的 TCP 连接就没了 —— 表现为 `adb devices` 里空空如也，紧接着截图报
    /// `device '...' not found`。本机实测：沙箱在每次进程结束时连 daemon 一起回收，
    /// 所以**跨进程调用之间必须重新 connect**。没有这一步，机器人会在"设备其实好好的"
    /// 情况下直接失败（现场症状是"模拟器一点动作都没有"，很容易被误判成卡死）。
    /// </summary>
    public bool EnsureConnected(int attempts = 3)
    {
        if (string.IsNullOrEmpty(Serial)) return Devices().Count > 0;
        for (int i = 0; i < attempts; i++)
        {
            if (Devices().Contains(Serial)) return true;
            // 列表为空也要试着连：本机最常见的情形正是"新 daemon 起来了、列表是空的、
            // 需要一次 connect 才能把 TCP 设备挂回来"。第一版在这里提前 return，
            // 结果明明能自愈却直接报"设备不在线"。
            _adb.Run(Args("connect", Serial));
            Thread.Sleep(1500);
        }
        return Devices().Contains(Serial);
    }

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
    /// <summary>只取 PNG 字节，不交给宿主解码（缩放适配扫描要反复重放同一帧）。</summary>
    public byte[] ScreenshotBytes()
    {
        var r = _adb.Run(Args("exec-out", "screencap", "-p"));
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"screencap 失败（exit={r.ExitCode}）: {r.Stderr.Trim()}");
        if (r.StdoutBytes.Length == 0)
            throw new InvalidOperationException("screencap 返回空字节流");
        return r.StdoutBytes;
    }

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

    /// <summary>
    /// 长按。对应上游 <c>Device.long_click(button, duration=(1, 1.2))</c>：adb 后端的实现就是
    /// `swipe_adb((x,y), (x,y), duration)` —— **同点滑动**，时长 1~1.2 秒随机。
    /// 注意上游对长按**不**做下面那个 ×2.5 的加长（只有 <see cref="SwipeUpstream"/> 才有）。
    /// </summary>
    public void LongClick(int x, int y, int durationMs = 1100)
        => _adb.Run(Args("shell", "input", "swipe", x.ToString(), y.ToString(),
                         x.ToString(), y.ToString(), durationMs.ToString()));

    /// <summary>
    /// 按键事件。上游用它发返回键：`adb_shell(['input', 'keyevent', '4'])`
    /// （见 module/equipment/equipment_code.py）；KEYCODE_BACK 的编号就是 4。
    /// </summary>
    public void Keyevent(string key)
        => _adb.Run(Args("shell", "input", "keyevent", key));

    /// <summary>返回键（KEYCODE_BACK = 4）。</summary>
    public void Back() => Keyevent("4");

    /// <summary>线级滑动：durationMs 直接就是这个 adb 命令收到的毫秒数。</summary>
    public void Swipe(int x1, int y1, int x2, int y2, int durationMs = 100)
        => _adb.Run(Args("shell", "input", "swipe", x1.ToString(), y1.ToString(),
                         x2.ToString(), y2.ToString(), durationMs.ToString()));

    /// <summary>
    /// 与上游 <c>Device.swipe(p1, p2, duration=(0.1, 0.2))</c> 对齐的滑动：入参是**秒**，
    /// 且 adb 后端会把时长 **×2.5**。上游注释原话是 "ADB needs to be slow, or swipe
    /// doesn't work" —— 照抄这个系数，否则同样的滑动在真机上会滑不到位，
    /// 表现为"识别正常但翻页翻不动"这类难查的问题。
    /// </summary>
    public void SwipeUpstream(int x1, int y1, int x2, int y2, double durationSeconds = 0.15)
        => Swipe(x1, y1, x2, y2, (int)(durationSeconds * 2.5 * 1000));

    public AdbResult Shell(params string[] command)
        => _adb.Run(Args(new[] { "shell" }.Concat(command).ToArray()));
}
