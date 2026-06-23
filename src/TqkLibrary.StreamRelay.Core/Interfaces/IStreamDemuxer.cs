using System;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Interfaces
{
    /// <summary>
    /// A per-stream demuxer. Raw container bytes from the ingest connection are fed in via
    /// <see cref="WriteAsync"/>; demuxed <see cref="RelayPacket"/>s are pulled out via
    /// <see cref="ReadPacketAsync"/>. Implementations wrap FFmpeg either in-process (P/Invoke) or
    /// out-of-process (worker process); the relay core depends only on this interface.
    /// </summary>
    public interface IStreamDemuxer : IAsyncDisposable
    {
        /// <summary>
        /// Probe the container header and populate <see cref="Init"/>. Requires bytes to already be flowing
        /// in through <see cref="WriteAsync"/> on another task, since probing reads from the same input.
        /// </summary>
        Task OpenAsync(CancellationToken cancellationToken);

        /// <summary>The discovered media init; available only after <see cref="OpenAsync"/> completes.</summary>
        MediaInit? Init { get; }

        /// <summary>Feed raw container bytes received from the ingest connection.</summary>
        ValueTask WriteAsync(ReadOnlyMemory<byte> containerBytes, CancellationToken cancellationToken);

        /// <summary>Signal that the ingest side closed and no more bytes will arrive.</summary>
        void CompleteInput();

        /// <summary>Pull the next demuxed packet, or null at end of stream.</summary>
        ValueTask<RelayPacket?> ReadPacketAsync(CancellationToken cancellationToken);
    }
}
