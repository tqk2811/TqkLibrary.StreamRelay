using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Interfaces;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.AspNetCore
{
    /// <summary>
    /// An <see cref="IPacketSink"/> that frames init/packet/control messages with <see cref="WireProtocol"/>
    /// and sends them as binary WebSocket messages. Exactly one send loop drives this sink (the relay's
    /// per-connection send loop), so sends are naturally serialised.
    /// </summary>
    public sealed class WebSocketPacketSink : IPacketSink
    {
        readonly WebSocket _socket;

        public WebSocketPacketSink(WebSocket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        }

        public async ValueTask SendInitAsync(MediaInit init, CancellationToken cancellationToken)
        {
            byte[] frame = WireProtocol.BuildInitFrame(init);
            await _socket.SendAsync(new ReadOnlyMemory<byte>(frame), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask SendPacketAsync(RelayPacket packet, CancellationToken cancellationToken)
        {
            byte[] frame = WireProtocol.RentPacketFrame(packet, out int length);
            try
            {
                await _socket.SendAsync(new ReadOnlyMemory<byte>(frame, 0, length), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            if (_socket.State != WebSocketState.Open)
                return;
            byte[] frame = WireProtocol.BuildControlFrame(WireProtocol.ControlEndOfStream);
            try
            {
                await _socket.SendAsync(new ReadOnlyMemory<byte>(frame), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Peer may already be gone; ignore.
            }
        }
    }
}
