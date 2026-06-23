using System;

namespace TqkLibrary.StreamRelay.AspNetCore.Models
{
    /// <summary>A packet frame parsed off the egress wire by a viewer client.</summary>
    public readonly struct WirePacket
    {
        public WirePacket(int streamIndex, bool isKeyframe, long pts, long dts, int duration, ReadOnlyMemory<byte> payload)
        {
            StreamIndex = streamIndex;
            IsKeyframe = isKeyframe;
            Pts = pts;
            Dts = dts;
            Duration = duration;
            Payload = payload;
        }

        public int StreamIndex { get; }
        public bool IsKeyframe { get; }
        public long Pts { get; }
        public long Dts { get; }
        public int Duration { get; }

        /// <summary>The compressed payload; a slice of the receive buffer, valid until the next read.</summary>
        public ReadOnlyMemory<byte> Payload { get; }
    }
}
