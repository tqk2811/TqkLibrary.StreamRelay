using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Interfaces;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Demux.FFmpeg
{
    /// <summary>
    /// An <see cref="IPacketSink"/> that remuxes packets to fragmented MP4 and writes the bytes to an HTTP
    /// response stream for a browser <c>MediaSource</c>. The init segment is emitted on the first init; each
    /// packet appends its media fragment. Exactly one send loop drives this sink.
    /// </summary>
    public sealed class FragmentedMp4PacketSink : IPacketSink
    {
        readonly Stream _output;
        FragmentedMp4Remuxer? _remuxer;
        bool _faulted;

        public FragmentedMp4PacketSink(Stream output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public async ValueTask SendInitAsync(MediaInit init, CancellationToken cancellationToken)
        {
            // Re-create the muxer on each (re)init so a resync produces a fresh init segment.
            _remuxer?.Dispose();
            _remuxer = new FragmentedMp4Remuxer(init);
            byte[] header = _remuxer.WriteHeader();
            if (header.Length > 0)
                await WriteAsync(header, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask SendPacketAsync(RelayPacket packet, CancellationToken cancellationToken)
        {
            if (_remuxer == null || _faulted)
                return;
            byte[] fragment;
            try
            {
                fragment = _remuxer.WritePacket(packet);
            }
            catch (Exception)
            {
                // A mux error (e.g. a stream the mp4 muxer rejects) must not kill the whole connection;
                // stop muxing and let the stream end gracefully.
                _faulted = true;
                return;
            }
            if (fragment.Length > 0)
                await WriteAsync(fragment, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch { /* client gone */ }
        }

        async ValueTask WriteAsync(byte[] data, CancellationToken cancellationToken)
        {
            await _output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => _remuxer?.Dispose();
    }
}
