using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Interfaces;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Test.Fakes
{
    /// <summary>
    /// An in-memory <see cref="IStreamDemuxer"/> the test drives directly: enqueue packets via
    /// <see cref="Produce"/>, then <see cref="Finish"/> to signal end of stream. Bytes fed via
    /// <see cref="WriteAsync"/> are ignored (the test produces packets explicitly).
    /// </summary>
    internal sealed class FakeStreamDemuxer : IStreamDemuxer
    {
        readonly Channel<RelayPacket> _packets = Channel.CreateUnbounded<RelayPacket>();
        long _bytesWritten;

        public FakeStreamDemuxer(MediaInit init)
        {
            Init = init;
        }

        public MediaInit? Init { get; private set; }

        public long BytesWritten => Interlocked.Read(ref _bytesWritten);

        public int DisposeCount { get; private set; }

        public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> containerBytes, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref _bytesWritten, containerBytes.Length);
            return ValueTask.CompletedTask;
        }

        public void CompleteInput() => _packets.Writer.TryComplete();

        /// <summary>Feed a packet to the demux loop. The loop owns releasing the ref it reads.</summary>
        public void Produce(RelayPacket packet) => _packets.Writer.TryWrite(packet);

        /// <summary>Signal end of stream.</summary>
        public void Finish() => _packets.Writer.TryComplete();

        public async ValueTask<RelayPacket?> ReadPacketAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _packets.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_packets.Reader.TryRead(out RelayPacket? packet))
                        return packet;
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            return null;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _packets.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
