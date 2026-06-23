using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Interfaces
{
    /// <summary>
    /// A viewer output (for example a WebSocket connection). The session drives these methods. Payload
    /// buffers passed to <see cref="SendPacketAsync"/> are owned by the session and must not be retained
    /// past the returned task.
    /// </summary>
    public interface IPacketSink
    {
        /// <summary>Send the media init; always the first thing a viewer receives.</summary>
        ValueTask SendInitAsync(MediaInit init, CancellationToken cancellationToken);

        /// <summary>Send one packet.</summary>
        ValueTask SendPacketAsync(RelayPacket packet, CancellationToken cancellationToken);

        /// <summary>Signal end of stream to the viewer.</summary>
        ValueTask CompleteAsync(CancellationToken cancellationToken);
    }
}
