using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Alas.Engine.Imaging;

/// <summary>One persistent, image-only worker. Protocol failures permanently close this instance.</summary>
public sealed class PythonTemplateVision : IVision
{
    private readonly Process _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _stderr;
    private readonly TimeSpan _timeout;
    private long _requestId;
    private bool _broken;
    private int _disposed;
    private readonly object _errorGate = new();
    private string _errorTail = "";

    public PythonTemplateVision(string python, string worker, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(python);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        if (!File.Exists(worker)) throw new FileNotFoundException("Pure vision worker is missing", worker);
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(worker))!
        };
        // Isolated Python ignores PYTHONPATH, the user site, and ambient startup customization.
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Path.GetFullPath(worker));
        start.Environment["OPENCV_IO_MAX_IMAGE_PIXELS"] = "16777216";
        _process = Process.Start(start) ?? throw new IOException("Cannot start pure vision worker");
        _stderr = DrainErrorAsync(_lifetime.Token);
    }

    public async ValueTask<TemplateObservation> MatchAsync(ScreenFrame frame, TemplateRequest request, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Validate(frame, request);
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_broken) throw new IOException("Pure vision session failed; it cannot reuse an uncertain response stream");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            limit.CancelAfter(_timeout);
            long id = ++_requestId;
            string command = JsonSerializer.Serialize(new
            {
                protocol = "alas-cv/1", id, operation = "template_match", frame = frame.Sequence,
                image = Convert.ToBase64String(frame.Png.Span), template = Convert.ToBase64String(request.TemplatePng.Span),
                area = new[] { request.SearchArea.X, request.SearchArea.Y, request.SearchArea.Width, request.SearchArea.Height },
                preprocessing = request.Preprocessing.ToString().ToLowerInvariant()
            });
            try
            {
                await _process.StandardInput.WriteLineAsync(command.AsMemory(), limit.Token);
                await _process.StandardInput.FlushAsync(limit.Token);
                string line = await ReadResponseAsync(limit.Token);
                using var document = JsonDocument.Parse(line);
                var response = document.RootElement;
                if (response.GetProperty("protocol").GetString() != "alas-cv/1" || response.GetProperty("id").GetInt64() != id ||
                    response.GetProperty("frame").GetInt64() != frame.Sequence)
                    throw new InvalidDataException("Pure vision response identity mismatch");
                if (response.TryGetProperty("error", out var error))
                    throw new InvalidDataException($"Pure vision rejected image input: {error.GetString()}");
                double similarity = response.GetProperty("similarity").GetDouble();
                var position = response.GetProperty("location");
                if (position.GetArrayLength() != 2 || !double.IsFinite(similarity) || similarity is < -1 or > 1)
                    throw new InvalidDataException("Pure vision returned an invalid match");
                var point = new PixelPoint(position[0].GetInt32(), position[1].GetInt32());
                var area = request.SearchArea;
                if (point.X < area.X || point.Y < area.Y || point.X >= (long)area.X + area.Width || point.Y >= (long)area.Y + area.Height)
                    throw new InvalidDataException("Pure vision returned a match outside the search area");
                // Upstream Button/Template use a strict comparison, including at equality.
                return new TemplateObservation(frame.Sequence, similarity > request.Similarity, similarity, point);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _broken = true;
                StopWorker();
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
                if (limit.IsCancellationRequested) throw new TimeoutException("Pure vision request timed out", error);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public ValueTask<OcrObservation> ReadTextAsync(ScreenFrame frame, OcrRequest request, CancellationToken token = default)
        => ValueTask.FromException<OcrObservation>(new NotSupportedException("The new pure vision service has no OCR implementation yet"));

    private async Task<string> ReadResponseAsync(CancellationToken token)
    {
        // Responses contain numbers only; bound input before constructing a full line.
        var text = new StringBuilder();
        char[] character = new char[1];
        while (text.Length < 4096)
        {
            int count = await _process.StandardOutput.ReadAsync(character.AsMemory(), token);
            if (count == 0)
            {
                // EOF normally follows process exit; give the concurrently drained error stream a bounded finish.
                try { await _stderr.WaitAsync(TimeSpan.FromSeconds(1), token); }
                catch (TimeoutException) { }
                lock (_errorGate) throw new EndOfStreamException("Pure vision worker exited before responding: " + _errorTail);
            }
            if (character[0] == '\n') return text.ToString();
            text.Append(character[0]);
        }
        throw new InvalidDataException("Pure vision response exceeded its protocol limit");
    }

    private async Task DrainErrorAsync(CancellationToken token)
    {
        char[] buffer = new char[2048];
        try
        {
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer.AsMemory(), token)) > 0)
                lock (_errorGate)
                {
                    _errorTail += new string(buffer, 0, count);
                    if (_errorTail.Length > 4096) _errorTail = _errorTail[^4096..];
                }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static void Validate(ScreenFrame frame, TemplateRequest request)
    {
        var area = request.SearchArea;
        if (frame.Sequence < 1 || frame.Png.IsEmpty || request.TemplatePng.IsEmpty ||
            frame.Png.Length > 16 * 1024 * 1024 || request.TemplatePng.Length > 16 * 1024 * 1024)
            throw new ArgumentException("Vision requires a frame identity and bounded image data");
        if (area.Width < 1 || area.Height < 1 || (long)area.Width * area.Height > 16 * 1024 * 1024 ||
            (long)area.X + area.Width > int.MaxValue || (long)area.Y + area.Height > int.MaxValue ||
            !double.IsFinite(request.Similarity) || request.Similarity is < -1 or > 1 || !Enum.IsDefined(request.Preprocessing))
            throw new ArgumentException("Invalid template match parameters");
    }

    private void StopWorker()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        try
        {
            StopWorker();
            await _stderr;
            _process.Dispose();
        }
        finally
        {
            _gate.Release();
            _lifetime.Dispose();
        }
    }
}
