using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Alas.Engine.Devices;

public sealed record ProcessResponse(int ExitCode, byte[] Output, string Error);

public interface IProcessRunner
{
    Task<ProcessResponse> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken token = default);
}

/// <summary>Bounded binary process transport. No shell, inherited stdin, or visible terminal windows.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResponse> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        token.ThrowIfCancellationRequested();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start device transport");
        process.StandardInput.Close();
        Exception? readFailure = null;
        async Task<byte[]> ReadGuardedAsync(Stream stream, int maximum)
        {
            try { return await ReadAsync(stream, maximum, limit.Token); }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                Interlocked.CompareExchange(ref readFailure, failure, null);
                await limit.CancelAsync();
                throw;
            }
        }
        Task<byte[]> output = ReadGuardedAsync(process.StandardOutput.BaseStream, 32 * 1024 * 1024);
        Task<byte[]> error = ReadGuardedAsync(process.StandardError.BaseStream, 1024 * 1024);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(limit.Token));
            return new ProcessResponse(process.ExitCode, await output, Encoding.UTF8.GetString(await error));
        }
        catch
        {
            bool timedOut = limit.IsCancellationRequested;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await limit.CancelAsync();
            // Observe both readers before disposing streams; partial screenshots are never returned.
            try { await Task.WhenAll(output, error); }
            catch (Exception) { }
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (readFailure is not null) ExceptionDispatchInfo.Capture(readFailure).Throw();
            if (timedOut)
                throw new TimeoutException("Device transport did not complete within its time limit");
            throw;
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token);
            if (count == 0) return bytes.ToArray();
            if (bytes.Length + count > maximum) throw new IOException("Device transport response exceeded its size limit");
            bytes.Write(buffer, 0, count);
        }
    }
}
