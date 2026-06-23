using System;
using TqkLibrary.StreamRelay.Buffers;
using TqkLibrary.StreamRelay.Enums;
using TqkLibrary.StreamRelay.Models;

namespace TqkLibrary.StreamRelay.Test.Fakes
{
    /// <summary>Helpers to build <see cref="RelayPacket"/>s and a default <see cref="MediaInit"/> for tests.</summary>
    internal static class TestPacketFactory
    {
        public static MediaInit VideoInit(int videoIndex = 0)
        {
            var stream = new MediaStreamInfo
            {
                Index = videoIndex,
                Kind = MediaCodecKind.Video,
                CodecId = 27, // AV_CODEC_ID_H264
                CodecName = "h264",
                Width = 640,
                Height = 480,
                TimeBaseNum = 1,
                TimeBaseDen = 90000,
            };
            return new MediaInit("mpegts", new[] { stream }) { PrimaryVideoStreamIndex = videoIndex };
        }

        /// <summary>Build a packet of <paramref name="length"/> bytes with refcount 1, filled with <paramref name="fill"/>.</summary>
        public static RelayPacket Packet(int streamIndex, bool keyframe, long pts, int length = 16, byte fill = 0xAB)
        {
            RefCountedBuffer buffer = RefCountedBuffer.Rent(length);
            Span<byte> span = buffer.WritableSpan;
            for (int i = 0; i < span.Length; i++)
                span[i] = fill;
            return new RelayPacket(streamIndex, keyframe, pts, pts, 0, buffer);
        }
    }
}
