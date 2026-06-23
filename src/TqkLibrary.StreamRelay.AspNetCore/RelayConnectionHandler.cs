using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TqkLibrary.StreamRelay.Interfaces;

namespace TqkLibrary.StreamRelay.AspNetCore
{
    /// <summary>
    /// Drives both relay WebSocket roles. Ingest: read container bytes off the socket into a per-stream
    /// <see cref="IStreamDemuxer"/> while a demux loop fans packets out via the session. View: attach a
    /// subscriber and run exactly one send loop into a <see cref="WebSocketPacketSink"/>.
    /// </summary>
    public sealed class RelayConnectionHandler
    {
        readonly StreamRegistry _registry;
        readonly IStreamDemuxerFactory _demuxerFactory;
        readonly StreamRelayOptions _options;
        readonly ILogger<RelayConnectionHandler> _logger;

        public RelayConnectionHandler(
            StreamRegistry registry,
            IStreamDemuxerFactory demuxerFactory,
            IOptions<StreamRelayOptions> options,
            ILogger<RelayConnectionHandler> logger)
        {
            _registry = registry;
            _demuxerFactory = demuxerFactory;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>True if <paramref name="format"/> is allowed (or null/empty and probing is permitted).</summary>
        public bool IsFormatAllowed(string? format)
        {
            if (string.IsNullOrEmpty(format))
                return true; // let the demuxer probe
            return _options.AllowedFormats.Contains(format!);
        }

        // -------------------------------------------------------------------------------------------
        // Ingest
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Try to create the demuxer for an ingest request before upgrading the socket, so a worker-cap
        /// rejection becomes HTTP 503. Returns false (with <paramref name="demuxer"/> null) at capacity.
        /// </summary>
        public bool TryCreateDemuxer(string? format, out IStreamDemuxer? demuxer)
        {
            try
            {
                demuxer = _demuxerFactory.Create(format);
                return true;
            }
            catch (DemuxCapacityExceededException ex)
            {
                _logger.LogWarning(ex, "Ingest rejected: demux capacity exceeded.");
                demuxer = null;
                return false;
            }
        }

        /// <summary>
        /// Handle an ingest connection for <paramref name="streamId"/> using a pre-created
        /// <paramref name="demuxer"/>: create the session, pump socket bytes into the demuxer, and run the
        /// session's demux loop until the device disconnects.
        /// </summary>
        public async Task HandleIngestAsync(Guid streamId, IStreamDemuxer demuxer, WebSocket socket, CancellationToken cancellationToken)
        {
            StreamSession session = _registry.GetOrCreateForIngest(streamId);

            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.SessionToken);
            CancellationToken token = connCts.Token;

            // Pump socket -> demuxer on this task; the demux loop pulls packets on another.
            Task pump = PumpIngestBytesAsync(socket, demuxer, token);

            try
            {
                using (var openCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    openCts.CancelAfter(_options.OpenTimeout);
                    await demuxer.OpenAsync(openCts.Token).ConfigureAwait(false);
                }
                if (demuxer.Init != null)
                    session.SetInit(demuxer.Init);

                await session.RunDemuxLoopAsync(demuxer, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown / device disconnect
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ingest demux loop ended with error for stream {StreamId}", streamId);
            }
            finally
            {
                try { connCts.Cancel(); } catch { /* ignore */ }
                try { await pump.ConfigureAwait(false); } catch { /* ignore */ }
                await demuxer.DisposeAsync().ConfigureAwait(false);
                _registry.ReleaseIngest(streamId);
                await CloseQuietlyAsync(socket).ConfigureAwait(false);
            }
        }

        async Task PumpIngestBytesAsync(WebSocket socket, IStreamDemuxer demuxer, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.IngestReceiveBufferSize);
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    ValueWebSocketReceiveResult result;
                    try
                    {
                        result = await socket.ReceiveAsync(new Memory<byte>(buffer), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (WebSocketException) { break; }

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    if (result.Count > 0)
                        await demuxer.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, result.Count), token).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                demuxer.CompleteInput();
            }
        }

        // -------------------------------------------------------------------------------------------
        // View
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Handle a viewer connection for <paramref name="streamId"/>: attach a subscriber and run one send
        /// loop into the socket. Returns false (without using the socket) if no device is streaming this id.
        /// </summary>
        public async Task<bool> HandleViewAsync(Guid streamId, WebSocket socket, CancellationToken cancellationToken)
        {
            if (!_registry.TryGet(streamId, out StreamSession session))
                return false;

            Guid viewerId = Guid.NewGuid();
            StreamSession.Subscriber subscriber = session.AddSubscriber(viewerId);
            var sink = new WebSocketPacketSink(socket);

            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.SessionToken);
            CancellationToken token = connCts.Token;

            // Watch for the viewer closing the socket so we can tear down promptly.
            Task closeWatch = WatchViewerCloseAsync(socket, connCts);

            try
            {
                await session.WriteToSinkAsync(subscriber, sink, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Viewer send loop ended for stream {StreamId}", streamId);
            }
            finally
            {
                session.RemoveSubscriber(viewerId);
                try { connCts.Cancel(); } catch { }
                try { await closeWatch.ConfigureAwait(false); } catch { }
                await CloseQuietlyAsync(socket).ConfigureAwait(false);
            }
            return true;
        }

        static async Task WatchViewerCloseAsync(WebSocket socket, CancellationTokenSource connCts)
        {
            byte[] scratch = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                while (socket.State == WebSocketState.Open && !connCts.IsCancellationRequested)
                {
                    ValueWebSocketReceiveResult result;
                    try
                    {
                        result = await socket.ReceiveAsync(new Memory<byte>(scratch), connCts.Token).ConfigureAwait(false);
                    }
                    catch { break; }
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
                try { connCts.Cancel(); } catch { }
            }
        }

        static async Task CloseQuietlyAsync(WebSocket socket)
        {
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* peer gone */ }
        }
    }
}
