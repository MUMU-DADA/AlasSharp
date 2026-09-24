using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Alas.Contracts;

namespace Alas.Client;

public sealed partial class ControlClient
{
    /// <summary>
    /// Observe complete state snapshots. Replace all displayed state, including
    /// the recent log window. Cursor gaps are allowed; this is not an audit replay.
    /// On disconnect/EOF, reconnect by calling again with the last delivered cursor.
    /// EOF and request cancellation say nothing about a running queue's outcome.
    /// </summary>
    public async IAsyncEnumerable<ControlStateUpdate> WatchStateAsync(string? lastCursor = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (lastCursor is not null && !ControlProtocol.IsEventCursor(lastCursor))
            throw new ArgumentException("事件游标无效", nameof(lastCursor));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "api/events"));
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (lastCursor is not null) request.Headers.Add("Last-Event-ID", lastCursor);
        // Separate header deadline from the long-lived body; never give the queue
        // this cancellation token. HTTP streaming support on WASM needs its own run.
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        opening.CancelAfter(_requestTimeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, opening.Token)
            .ConfigureAwait(false);
        await RequireStatusAsync(response, HttpStatusCode.OK, opening.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream" ||
            !response.Headers.TryGetValues("X-Alas-Events", out var contracts) ||
            !contracts.SequenceEqual([ControlProtocol.EventsContract]))
            throw new ControlProtocolException("控制状态事件流合同不匹配");
        opening.CancelAfter(Timeout.InfiniteTimeSpan);
        await using var stream = new EventReadStream(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _requestTimeout);
        var parser = SseParser.Create(stream);
        await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item.EventType is not ("reset" or "snapshot") || !ControlProtocol.IsEventCursor(item.EventId))
                throw new ControlProtocolException("控制状态事件类型或游标无效");
            ControlState state;
            try
            {
                state = JsonSerializer.Deserialize(item.Data, ControlJsonContext.Default.ControlState)
                    ?? throw new ControlProtocolException("控制状态事件为空");
            }
            catch (JsonException error)
            {
                throw new ControlProtocolException("控制状态事件不符合合同", error);
            }
            AcceptState(state);
            yield return new ControlStateUpdate(item.EventId!, item.EventType == "reset", state);
        }
    }

    // Apply an idle deadline to each network read, including SSE comment heartbeats.
    // Waiting for the UI to request the next item does not consume this deadline.
    private sealed class EventReadStream(Stream source, TimeSpan idleTimeout) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(idleTimeout);
            return await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => source.DisposeAsync();
    }
}
