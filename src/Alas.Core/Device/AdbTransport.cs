using System.Diagnostics;
using System.Text;

namespace Alas.Device;

/// <summary>一次 adb 调用的结果。<see cref="StdoutBytes"/> 是**二进制**原始输出（截图要用）。</summary>
public sealed record AdbResult(int ExitCode, byte[] StdoutBytes, string Stderr)
{
    public string StdoutText => Encoding.UTF8.GetString(StdoutBytes);
}

/// <summary>adb 执行通道的抽象：便于在没有真机时用桩可执行文件跑通同一条调用链。</summary>
public interface IAdbTransport
{
    AdbResult Run(IReadOnlyList<string> args, TimeSpan? timeout = null);
}

/// <summary>
/// 真实 adb 通道。
///
/// <see cref="PrefixArguments"/> 的存在是为了**可测试**：把 adb 换成桩可执行文件时，
/// 只要给出「解释器 + 桩脚本路径」作为前缀即可，进程启动、二进制输出捕获、
/// 参数拼接这些真正容易出错的环节照旧被完整走到。
/// </summary>
public sealed class ProcessAdbTransport : IAdbTransport
{
    public string Executable { get; }
    public IReadOnlyList<string> PrefixArguments { get; }
    public TimeSpan DefaultTimeout { get; }

    /// <summary>额外传给子进程的环境变量（桩 adb 靠它接收截图路径与日志路径）。</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>();

    public ProcessAdbTransport(string executable, IEnumerable<string>? prefixArguments = null,
                               TimeSpan? defaultTimeout = null)
    {
        Executable = executable;
        PrefixArguments = prefixArguments?.ToList() ?? new List<string>();
        DefaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
    }

    public AdbResult Run(IReadOnlyList<string> args, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string p in PrefixArguments) psi.ArgumentList.Add(p);
        foreach (string a in args) psi.ArgumentList.Add(a);
        foreach (var kv in Environment) psi.Environment[kv.Key] = kv.Value;

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"无法启动 {Executable}");
        // 手工读二进制流：StandardOutput.ReadToEnd() 会按文本解码，截图字节会被破坏
        using var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw new TimeoutException($"adb 超时: {string.Join(' ', args)}");
        }
        copy.Wait(TimeSpan.FromSeconds(5));
        return new AdbResult(process.ExitCode, stdout.ToArray(), stderr);
    }
}
