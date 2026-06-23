using System;
using TqkLibrary.StreamRelay.Buffers;

namespace TqkLibrary.StreamRelay.Models
{
    /// <summary>
    /// One demuxed compressed packet (an AVPacket equivalent) carried opaquely by the relay. The payload
    /// lives in a pooled, reference-counted <see cref="RefCountedBuffer"/> so the same bytes can be retained
    /// by the GOP buffer and fanned out to many viewers without copying.
    /// </summary>
    public sealed class RelayPacket
    {
        public RelayPacket(int streamIndex, bool isKeyframe, long pts, long dts, int duration, RefCountedBuffer payload)
        {
            StreamIndex = streamIndex;
            IsKeyframe = isKeyframe;
            Pts = pts;
            Dts = dts;
            Duration = duration;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        public int StreamIndex { get; }
        public bool IsKeyframe { get; }
        public long Pts { get; }
        public long Dts { get; }
        public int Duration { get; }

        /// <summary>Reference-counted payload buffer. Use <see cref="AddRef"/>/<see cref="Release"/> to share ownership.</summary>
        public RefCountedBuffer Payload { get; }

        /// <summary>Increment the payload ref count and return this packet (for retaining/sharing).</summary>
        public RelayPacket AddRef()
        {
            Payload.AddRef();
            return this;
        }

        /// <summary>Release one reference to the payload; the buffer returns to the pool at zero.</summary>
        public void Release() => Payload.Release();
    }
}
